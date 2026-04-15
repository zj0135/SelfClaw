# SelfClaw - 项目上下文文档

## 项目概述

**SelfClaw** 是一个面向 Windows 桌面的个人 AI 助手客户端，采用 WPF 技术栈构建。它提供聊天式交互界面，支持 OpenAI 兼容模型、工作区工具、团队协作式 AI 讨论，以及外部频道消息接入。

当前后端结构已经做过一轮按功能归类的整理：

- `SelfClaw.Core` 负责领域模型、接口和运行时事件/请求契约
- `SelfClaw.Infrastructure` 负责 Agent 执行、SQLite 持久化、工具实现、安全和频道接入
- `SelfClaw.Tests` 目录结构基本镜像 `Infrastructure` 的功能分组

## 核心特性

- **多模型支持**：支持配置多个 OpenAI 兼容 Provider Profile
- **工作区集成**：支持列目录、搜索文本、读取文件，也支持受控写文件和 PowerShell 执行
- **团队模式**：支持多 Agent 讨论、协调者总结和可选文档导出
- **频道模式**：支持外部聊天频道接入，当前已包含飞书通道实现
- **对话管理**：支持主会话、Agent 分支会话、工具执行记录和团队状态持久化
- **安全存储**：使用 Windows DPAPI 加密存储 API 密钥
- **现代化渲染**：使用 WebView2 与 Markdig 渲染 Markdown，对流式消息中的 thinking/tool anchor 做分段处理

## 技术栈

| 层级 | 技术 |
|------|------|
| 框架 | .NET 10.0 |
| UI | WPF |
| MVVM | CommunityToolkit.Mvvm |
| AI/LLM | Microsoft.Agents.AI, Microsoft.Extensions.AI |
| 数据存储 | SQLite (Microsoft.Data.Sqlite) |
| 渲染 | WebView2, Markdig |
| 安全 | Windows DPAPI |
| 外部频道 | Feishu Open Platform |

## 项目结构

```text
SelfClaw/
├── SelfClaw.Core/
│   ├── Interfaces/
│   │   ├── Agents/
│   │   ├── Conversations/
│   │   ├── Profiles/
│   │   ├── Security/
│   │   └── Workspace/
│   ├── Models/
│   │   ├── Conversations/
│   │   ├── Profiles/
│   │   ├── Teams/
│   │   ├── Tooling/
│   │   └── Workspace/
│   └── Runtime/
│       ├── Approvals/
│       ├── Events/
│       └── Requests/
├── SelfClaw.Infrastructure/
│   ├── Agents/
│   │   ├── Runtime/
│   │   └── Tools/
│   ├── Channels/
│   │   └── Feishu/
│   ├── Data/
│   │   └── Sqlite/
│   │       └── Repositories/
│   ├── DependencyInjection/
│   ├── Options/
│   ├── Security/
│   └── Tools/
│       ├── Transcript/
│       └── Workspace/
├── SelfClaw.Desktop/
└── SelfClaw.Tests/
    └── Infrastructure/
        ├── Agents/
        ├── Data/
        ├── Security/
        └── Tools/
```

## 重要约定

### 1. 目录已按功能归类，但 Core 命名空间仍保持稳定

虽然 `SelfClaw.Core` 已拆成 `Conversations / Profiles / Teams / Tooling / Workspace` 子目录，但这些类型当前仍统一使用以下命名空间，避免大面积影响上层代码：

- `SelfClaw.Core.Interfaces`
- `SelfClaw.Core.Models`
- `SelfClaw.Core.Runtime`

也就是说：**目录分组更细了，但 Core 的命名空间没有跟着拆散。**

### 2. Infrastructure 的命名空间基本跟目录一致

当前常用命名空间：

- `SelfClaw.Infrastructure.Agents.Runtime`
- `SelfClaw.Infrastructure.Agents.Tools`
- `SelfClaw.Infrastructure.Data.Sqlite`
- `SelfClaw.Infrastructure.Data.Sqlite.Repositories`
- `SelfClaw.Infrastructure.Tools.Transcript`
- `SelfClaw.Infrastructure.Tools.Workspace`

## 构建和运行

### 环境要求

- .NET SDK 10.0.201 或更高版本
- Windows 10/11
- WebView2 Runtime

### 构建命令

```powershell
.\build.ps1

dotnet restore SelfClaw.slnx --force-evaluate
dotnet build SelfClaw.slnx
```

### 运行应用

```powershell
dotnet run --project SelfClaw.Desktop/SelfClaw.Desktop.csproj
```

### 运行测试

```powershell
.\test.ps1

dotnet test SelfClaw.Tests/SelfClaw.Tests.csproj
```

## 架构说明

### 1. 依赖注入配置

DI 在 `ServiceCollectionExtensions.AddSelfClawInfrastructure()` 中配置：

```csharp
services.AddSingleton(StoragePaths.CreateDefault());
services.AddSingleton<SqliteDatabase>();
services.AddSingleton<IProfileRepository, SqliteProfileRepository>();
services.AddSingleton<IConversationRepository, SqliteConversationRepository>();
services.AddSingleton<ISecretProtector, DpapiSecretProtector>();
services.AddSingleton<IWorkspaceToolService, WorkspaceToolService>();
services.AddSingleton<IAgentContextProviderFactory, FileSystemAgentContextProviderFactory>();
services.AddSingleton<IAgentChatRuntime, SelfClawAgentChatRuntime>();
services.AddSingleton<MarkdownHtmlRenderer>();
```

### 2. Agent 运行时分层

`SelfClawAgentChatRuntime` 是运行时编排入口，当前负责三种模式：

- `Programming`：普通编程/对话模式
- `Team`：多 Agent 讨论和协调总结模式
- `Channel`：外部频道回复模式

配套职责拆分如下：

- `SelfClawAgentChatRuntime`：编排回合、消息事件、团队讨论和最终总结
- `ChatClientAgentExecutionService`：封装模型调用与流式输出
- `FileSystemAgentContextProviderFactory`：提供本地 skills/context provider
- `WorkspaceToolFunctions`：把工作区服务包装成 Agent 可调用工具，并接入审批/观测
- `RuntimeToolObserver`：记录工具开始、审批中、完成、失败等运行时事件

### 3. 工作区工具与权限模型

当前工作区工具不再只是只读，实际能力如下：

| 工具名 | 功能 |
|--------|------|
| `list_workspace_files` | 列出工作区文件和目录 |
| `search_workspace_text` | 搜索工作区文本 |
| `read_workspace_file` | 读取文本文件 |
| `write_workspace_file` | 在工作区内创建或覆盖 UTF-8 文本文件 |
| `run_shell_command` | 在工作区根目录下执行 PowerShell 命令 |

权限模型由 `ToolPermissionMode` 控制：

- `RequireApproval`：写文件和执行命令前必须走 `IToolApprovalHandler`
- `FullAccess`：直接执行，不再请求确认

`WorkspaceToolService` 本身还负责：

- 路径归一化和越界防护
- 文本文件检测
- 读取/写入大小限制
- shell 超时和输出截断

### 4. 数据访问与仓储

SQLite 基础设施由 `SqliteDatabase` 管理，负责：

- 数据库文件创建
- schema 初始化
- `PRAGMA foreign_keys = ON`
- 兼容旧列的增量补齐
- schema version 记录

仓储实现：

- `SqliteProfileRepository`：ProviderProfile 持久化
- `SqliteConversationRepository`：会话、消息、团队 Agent、工具执行、工作区根目录的持久化

`SqliteConversationRepository` 目前还包含一层额外逻辑：

- 自动复用或合并重复的 Agent 分支会话
- 在读取时折叠重复 Agent 会话记录

### 5. Transcript / Markdown 渲染

Transcript 相关代码集中在 `SelfClaw.Infrastructure.Tools.Transcript`：

- `AssistantMessageSegmenter`：负责 thinking 区块、tool anchor、最终消息合并
- `MarkdownHtmlRenderer`：负责 Markdown -> HTML 渲染

这部分会同时被 Agent 运行时和 Desktop UI 使用。

## 关键数据模型

### ProviderProfile

Provider 配置，包含模型参数与密钥引用：

```csharp
record ProviderProfile(
    Guid Id,
    string Name,
    string Endpoint,
    string Model,
    bool TemperatureEnabled,
    double Temperature,
    bool TopPEnabled,
    double TopP,
    ApiStyle ApiStyle,
    string SecretRef,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);
```

### ConversationRecord

当前 `ConversationRecord` 已不仅仅是基础会话记录，还包含：

- `Mode`：`Programming / Team / Channel`
- `ToolPermissionMode`
- `TeamMaxRounds`
- `TeamOutputMode`
- `ParentConversationId / RootConversationId`
- `BoundAgentId / BoundAgentName / BoundAgentRole`
- `ChannelKind / ChannelConversationId / ChannelDisplayName`

### Team 相关模型

- `TeamAgentRecord`
- `TeamAgentStatus`
- `TeamDiscussionDefaults`
- `TeamOutputMode`

### Tool / Workspace 相关模型

- `ToolExecutionRecord`
- `ToolExecutionStatus`
- `ShellCommandResult`
- `WorkspaceRoot`
- `WorkspaceFileContent`
- `WorkspaceFileEntry`
- `WorkspaceFileWriteResult`
- `WorkspaceSearchHit`

## 开发约定

### 代码风格

- 使用 `ImplicitUsings` 和 `Nullable`
- 优先使用 `record` 表达不可变数据模型
- 异步方法使用 `Async` 后缀
- 传播 `CancellationToken`

### 测试

- 使用 xUnit
- 使用 FluentAssertions
- `SelfClaw.Tests` 通过 `<Reference>` 引用 `SelfClaw.Core.dll` 和 `SelfClaw.Infrastructure.dll`
- 测试目录尽量镜像 `Infrastructure` 的功能结构

这意味着：

- 如果改动了 `Core` / `Infrastructure` 的公开类型或命名空间，测试前必须确保对应项目已重新构建
- 直接运行 `dotnet test SelfClaw.Tests/SelfClaw.Tests.csproj` 时，会先触发 `BuildManagedDependencies`

### 数据访问

- 使用原生 SQL 与 `Microsoft.Data.Sqlite`
- 数据库存储路径默认在 `%LOCALAPPDATA%\SelfClaw\selfclaw.db`
- `SqliteConversationRepository` 目前职责较大，后续如继续演进，可考虑拆分仓储边界

## 关键文件

| 文件 | 说明 |
|------|------|
| `SelfClaw.Desktop/App.xaml.cs` | 应用入口与 DI 容器配置 |
| `SelfClaw.Desktop/ViewModels/MainWindowViewModel.cs` | 主窗口核心业务逻辑 |
| `SelfClaw.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs` | 基础设施服务注册 |
| `SelfClaw.Infrastructure/Agents/Runtime/SelfClawAgentChatRuntime.cs` | Agent 运行时编排入口 |
| `SelfClaw.Infrastructure/Agents/Runtime/ChatClientAgentExecutionService.cs` | 模型执行与流式输出 |
| `SelfClaw.Infrastructure/Agents/Tools/WorkspaceToolFunctions.cs` | Agent 工具适配与审批包装 |
| `SelfClaw.Infrastructure/Tools/Workspace/WorkspaceToolService.cs` | 工作区文件和 shell 能力实现 |
| `SelfClaw.Infrastructure/Data/Sqlite/SqliteDatabase.cs` | SQLite 初始化与 schema 管理 |
| `SelfClaw.Infrastructure/Data/Sqlite/Repositories/SqliteConversationRepository.cs` | 会话相关持久化 |
| `SelfClaw.Core/Interfaces/*` | 核心契约定义 |

## 注意事项

1. **Desktop 和 Tests 的引用方式**：二者都不是完全依赖 `<ProjectReference>`，而是直接引用已构建 DLL；修改后端公开类型时，要注意先构建再测试。

2. **Windows 专属**：WPF、DPAPI 和当前 PowerShell 工具能力都使该应用以 Windows 为主要目标平台。

3. **工作区工具已具备写入和命令执行能力**：不要再把它当成“纯只读工具”理解；安全边界依赖 `WorkspaceToolService` 的路径限制和 `ToolPermissionMode` 的审批策略。

4. **Core 目录与命名空间不完全一一对应**：新增文件时优先遵守当前稳定命名空间，除非明确要推动一次完整命名空间重构。

5. **当前构建存在一个已知提醒**：`SelfClaw.Desktop.csproj` 会给出 `System.Security.Cryptography.ProtectedData` 的 `NU1510` 警告，目前未处理，不影响功能。
