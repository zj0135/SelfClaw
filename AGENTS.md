# SelfClaw - 项目上下文文档

## 项目概述

**SelfClaw** 是一个面向 Windows 桌面的个人 AI 助手客户端，主应用采用 **WPF + .NET 10**。

当前项目由 5 个主要部分组成：

- `SelfClaw.Core`：领域模型、接口契约、运行时事件/请求契约
- `SelfClaw.Infrastructure`：Agent 运行时、SQLite 持久化、工具实现、安全、频道接入
- `SelfClaw.Desktop`：WPF 桌面应用、ViewModel、通知、系统托盘、频道管理
- `SelfClaw.Tests`：测试工程，目录结构基本镜像 `Infrastructure`
- `SelfClaw.TranscriptVue`：Vue + Vite 前端壳层，构建产物输出到 Desktop 资产目录

## 当前核心能力

- 多 Provider Profile（OpenAI 兼容 endpoint + model + 采样参数）
- 三种会话模式：`Programming` / `Team` / `Channel`
- Programming 模式支持 **Plan Mode**（先规划执行步骤，再分步执行）
- Team 模式支持多 Agent 讨论、协调者总结、可选文档导出
- Channel 模式支持外部消息接入（当前内置飞书）
- 工作区工具支持读写文件、全文搜索、PowerShell 执行（含审批模型）
- Transcript 支持 thinking 分段、tool anchor 和工具运行锚定渲染
- 消息支持图片附件持久化（`message_attachments`）
- API Key 使用 Windows DPAPI 加密存储

## 技术栈

| 层         | 技术                                                                         |
| ---------- | ---------------------------------------------------------------------------- |
| Runtime    | .NET 10                                                                      |
| Desktop UI | WPF, CommunityToolkit.Mvvm                                                   |
| AI         | Microsoft.Agents.AI, Microsoft.Extensions.AI, Microsoft.Extensions.AI.OpenAI |
| Data       | SQLite (`Microsoft.Data.Sqlite`)                                             |
| Render     | WebView2, Markdig, TranscriptVue (Vue 3 + Vite)                              |
| Logging    | Serilog                                                                      |
| Security   | Windows DPAPI                                                                |
| Channel    | Feishu Open Platform                                                         |

## 项目结构

```text
SelfClaw/
├── AGENTS.md
├── SelfClaw.slnx
├── SelfClaw.Core/
│   ├── Interfaces/
│   ├── Models/
│   └── Runtime/
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
│   ├── Services/
│   ├── ViewModels/
│   └── Assets/
│       └── TranscriptVue/
├── SelfClaw.TranscriptVue/
│   ├── src/
│   ├── package.json
│   └── vite.config.js
└── SelfClaw.Tests/
    └── Infrastructure/
```

## 重要约定

### 1. Core 目录细分，但命名空间保持稳定

`SelfClaw.Core` 虽然已按 `Conversations / Profiles / Teams / Tooling / Workspace` 分目录，但命名空间仍统一为：

- `SelfClaw.Core.Interfaces`
- `SelfClaw.Core.Models`
- `SelfClaw.Core.Runtime`

新增类型优先沿用这套稳定命名空间，除非明确推进一次命名空间重构。

### 2. Infrastructure 命名空间基本跟目录一致

常用命名空间：

- `SelfClaw.Infrastructure.Agents.Runtime`
- `SelfClaw.Infrastructure.Agents.Tools`
- `SelfClaw.Infrastructure.Data.Sqlite`
- `SelfClaw.Infrastructure.Data.Sqlite.Repositories`
- `SelfClaw.Infrastructure.Tools.Transcript`
- `SelfClaw.Infrastructure.Tools.Workspace`

### 3. Tests 当前使用 `ProjectReference`

`SelfClaw.Tests.csproj` 当前直接引用：

- `..\SelfClaw.Core\SelfClaw.Core.csproj`
- `..\SelfClaw.Infrastructure\SelfClaw.Infrastructure.csproj`

当前不是“仅依赖已构建 DLL”的模式。

### 4. TranscriptVue 产物由前端工程构建覆盖

`SelfClaw.TranscriptVue/vite.config.js` 的 `outDir` 指向：

- `SelfClaw.Desktop/Assets/TranscriptVue`

Desktop 构建还会通过 `SyncTranscriptVueAssets` 复制该目录内容到输出目录。

## 构建与运行

### 环境要求

- Windows 10/11
- .NET SDK 10.0.201+
- WebView2 Runtime
- （前端资产开发时）Node.js + npm

### 常用命令

```powershell
dotnet restore SelfClaw.slnx --force-evaluate
dotnet build SelfClaw.slnx
dotnet run --project SelfClaw.Desktop/SelfClaw.Desktop.csproj
dotnet test SelfClaw.Tests/SelfClaw.Tests.csproj
```

### TranscriptVue 开发/打包

```powershell
cd SelfClaw.TranscriptVue
npm install
npm run dev
npm run build
```

## 架构说明

### 1. DI 配置

基础设施 DI 在 `ServiceCollectionExtensions.AddSelfClawInfrastructure()` 注册：

- `StoragePaths`
- `SqliteDatabase`
- `IProfileRepository / IConversationRepository`
- `ISecretProtector`
- `IWorkspaceToolService`
- `IAgentContextProviderFactory`
- `IAgentChatRuntime`
- `MarkdownHtmlRenderer`

Desktop 侧在 `App.xaml.cs` 额外注册：

- `DesktopSettingsStore`
- `DesktopChannelManager`
- `DesktopToolApprovalHandler`
- `DesktopNotificationService`
- `SystemTrayService`
- `MainWindowViewModel` / `MainWindow`

### 2. Agent 运行时分层

`SelfClawAgentChatRuntime` 是运行时编排入口，支持：

- `Programming`
- `Programming + EnablePlanMode`
- `Team`
- `Channel`
- `BoundAgent`（主会话分支出的专属 Agent 会话）

配套组件：

- `ChatClientAgentExecutionService`：模型调用和流式输出
- `FileSystemAgentContextProviderFactory`：加载本地 skills（当前禁用脚本执行）
- `WorkspaceToolFunctions`：工具包装、审批、观测
- `RuntimeToolObserver`：工具开始/审批中/完成/失败事件落地

### 3. Plan Mode（新增重点）

Core 运行时已包含：

- `ExecutionPlan`
- `ExecutionPlanStep`
- `ExecutionPlanStepStatus`
- `ExecutionPlanDraftingStartedEvent`
- `ExecutionPlanPreparedEvent`
- `ExecutionPlanStepStatusChangedEvent`

Desktop 渲染层有 `TranscriptPlanPanel` / `TranscriptPlanStep` 对应展示。

### 4. 工作区工具与权限模型

工具能力：

- `list_workspace_files`
- `search_workspace_text`
- `read_workspace_file`
- `write_workspace_file`
- `run_shell_command`

权限由 `ToolPermissionMode` 控制：

- `RequireApproval`：写文件和命令执行前需审批
- `FullAccess`：直接执行

`WorkspaceToolService` 还负责：

- 路径归一化和越界防护
- 文本文件检测
- 大小限制（读 24k chars、写 200k chars、shell 输出 24k chars）
- shell 超时控制（1s~600s）

### 5. 数据访问与持久化

`SqliteDatabase` 负责 schema 初始化和增量补齐，当前：

- `CurrentSchemaVersion = 13`
- `PRAGMA foreign_keys = ON`
- 自动 `EnsureColumnExists` 兼容旧库

主要表：

- `profiles`
- `workspace_roots`
- `conversations`
- `messages`
- `message_attachments`
- `team_agents`
- `tool_runs`

`SqliteConversationRepository` 还负责：

- Agent 分支会话去重合并
- 消息附件读写
- team agent 去重读取

### 6. Transcript / Markdown

`SelfClaw.Infrastructure.Tools.Transcript`：

- `AssistantMessageSegmenter`：处理 `<think>`、内部 thinking 标记、tool anchor
- `MarkdownHtmlRenderer`：Markdig 渲染并禁用原生 HTML

### 7. Channel 模式（飞书）

`DesktopChannelManager` 统一管理通道配置、生命周期和消息转发。  
当前内置 `FeishuDesktopChannelAdapter`，通过 `FeishuBotService` 支持：

- 长连接接收消息
- @ 提及处理
- 流式回复（卡片轮换）
- 图片/文件/语音资源处理

## 关键数据模型

### ProviderProfile

Provider 配置模型，包含 endpoint/model/采样参数/secret 引用。

### ConversationRecord

除基础字段外，还包含：

- `Mode`
- `ToolPermissionMode`
- `TeamMaxRounds`
- `TeamOutputMode`
- `ParentConversationId / RootConversationId`
- `BoundAgentId / BoundAgentName / BoundAgentRole`
- `ChannelKind / ChannelConversationId / ChannelDisplayName`

### MessageRecord

已包含：

- Agent 元信息与 token 统计
- 错误信息
- `Attachments`（当前 `MessageAttachmentKind` 支持 `Image`）

### ChatTurnRequest

关键字段：

- `ContextMessages`
- `BoundAgent`
- `EnablePlanMode`

## 测试现状

- 测试框架：xUnit + FluentAssertions
- 目录组织：尽量镜像 `Infrastructure`
- 现有重点测试：
- `SelfClawAgentChatRuntimeTests`
- `FileSystemAgentContextProviderFactoryTests`
- `SqliteRepositoriesTests`
- `WorkspaceToolServiceTests`
- `AssistantMessageSegmenterTests`
- `DpapiSecretProtectorTests`

## 关键文件索引

| 文件                                                                               | 说明                                 |
| ---------------------------------------------------------------------------------- | ------------------------------------ |
| `SelfClaw.Desktop/App.xaml.cs`                                                     | 应用入口、日志、DI                   |
| `SelfClaw.Desktop/ViewModels/MainWindowViewModel*.cs`                              | 主业务流程（会话、团队、频道、通知） |
| `SelfClaw.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs`       | 基础设施服务注册                     |
| `SelfClaw.Infrastructure/Agents/Runtime/SelfClawAgentChatRuntime.cs`               | 运行时编排核心                       |
| `SelfClaw.Infrastructure/Agents/Runtime/ChatClientAgentExecutionService.cs`        | 模型执行层                           |
| `SelfClaw.Infrastructure/Agents/Tools/WorkspaceToolFunctions.cs`                   | 工具适配与审批包装                   |
| `SelfClaw.Infrastructure/Tools/Workspace/WorkspaceToolService.cs`                  | 工作区文件和 shell 实现              |
| `SelfClaw.Infrastructure/Data/Sqlite/SqliteDatabase.cs`                            | SQLite 初始化与 schema 管理          |
| `SelfClaw.Infrastructure/Data/Sqlite/Repositories/SqliteConversationRepository.cs` | 会话/消息/工具/团队持久化            |
| `SelfClaw.Desktop/Services/Channels/DesktopChannelManager.cs`                      | 外部频道接入编排                     |
| `SelfClaw.TranscriptVue/src/*`                                                     | Transcript 前端壳层                  |

## 注意事项

1. 项目是 Windows 优先（WPF、DPAPI、PowerShell、通知和托盘能力都偏 Windows）。
2. 工作区工具具备写入和命令执行能力，不应按“只读工具”理解。
3. 安全边界依赖 `WorkspaceToolService` 路径限制和 `ToolPermissionMode` 审批策略。
4. Core 的目录分组与命名空间目前不是一一对应，新增代码时优先保持一致性。
5. `SelfClaw.Desktop.csproj` 已通过 `NoWarn` 抑制 `NU1510`，不应继续作为未处理提醒。
