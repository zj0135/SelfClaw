# SelfClaw - 项目上下文文档

## 项目概述

**SelfClaw** 是一个面向 Windows 桌面的个人 AI 助手客户端，采用 WPF 技术栈构建。它提供了一个简洁的聊天界面，支持与各种 OpenAI 兼容的 AI 模型进行对话，并具备只读工作区工具访问能力，可帮助用户分析和理解本地项目文件。

### 核心特性

- **多模型支持**: 支持配置多个 AI Provider 配置文件，兼容 OpenAI API 格式
- **工作区集成**: 可浏览、搜索和读取本地工作区文件，AI 助手可基于项目上下文回答问题
- **对话管理**: 支持多对话历史记录，自动保存聊天上下文
- **安全存储**: 使用 Windows DPAPI 加密存储 API 密钥
- **现代化 UI**: 使用 WebView2 渲染 Markdown 格式的对话内容

## 技术栈

| 层级 | 技术 |
|------|------|
| **框架** | .NET 10.0 |
| **UI** | WPF (Windows Presentation Foundation) |
| **MVVM** | CommunityToolkit.Mvvm |
| **AI/LLM** | Microsoft.Agents.AI, Microsoft.Extensions.AI |
| **数据存储** | SQLite (Microsoft.Data.Sqlite) |
| **渲染** | WebView2, Markdig |
| **安全** | Windows DPAPI (System.Security.Cryptography.ProtectedData) |

## 项目结构

```
SelfClaw/
├── SelfClaw.Core/                    # 核心领域层
│   ├── Interfaces/                   # 仓库和服务接口
│   ├── Models/                       # 领域模型 (Profile, Conversation, Message等)
│   └── Runtime/                      # 运行时事件和请求模型
├── SelfClaw.Infrastructure/          # 基础设施层
│   ├── Agents/                       # AI Agent 运行时实现
│   ├── Data/                         # SQLite 数据库访问
│   ├── Repositories/                 # 数据仓库实现
│   ├── Security/                     # DPAPI 密钥保护
│   └── Tools/                        # 工作区工具服务
├── SelfClaw.Desktop/                 # WPF 桌面应用层
│   ├── Views/                        # XAML 视图
│   ├── ViewModels/                   # MVVM 视图模型
│   └── Services/                     # UI 相关服务
└── SelfClaw.Tests/                   # 单元测试项目 (xUnit)
```

## 构建和运行

### 环境要求

- **.NET SDK 10.0.201** 或更高版本
- **Windows 10/11** 操作系统
- **WebView2 Runtime** (用于渲染对话内容)

### 构建命令

```powershell
# 使用提供的构建脚本
.\build.ps1

# 或手动构建
dotnet restore SelfClaw.slnx --force-evaluate
dotnet build SelfClaw.slnx
```

### 运行应用

```powershell
dotnet run --project SelfClaw.Desktop/SelfClaw.Desktop.csproj
```

### 运行测试

```powershell
# 使用测试脚本
.\test.ps1

# 或手动运行
dotnet test SelfClaw.Tests/SelfClaw.Tests.csproj
```

## 架构说明

### 1. 依赖注入配置

依赖注入在 `ServiceCollectionExtensions.AddSelfClawInfrastructure()` 中配置：

```csharp
services.AddSingleton<SqliteDatabase>();
services.AddSingleton<IProfileRepository, SqliteProfileRepository>();
services.AddSingleton<IConversationRepository, SqliteConversationRepository>();
services.AddSingleton<ISecretProtector, DpapiSecretProtector>();
services.AddSingleton<IWorkspaceToolService, WorkspaceToolService>();
services.AddSingleton<IAgentChatRuntime, SelfClawAgentChatRuntime>();
```

### 2. AI Agent 运行时

`SelfClawAgentChatRuntime` 实现了 `IAgentChatRuntime` 接口，使用 `Microsoft.Agents.AI` 库与 AI 模型通信：

- **流式响应**: 通过 `Channel<T>` 实现异步流式响应
- **工具调用**: 支持工作区文件列表、搜索和读取工具
- **消息映射**: 将内部 `MessageRecord` 映射到 `Microsoft.Extensions.AI.ChatMessage`

### 3. 工作区工具

AI 助手可以调用以下只读工具：

| 工具名 | 功能 |
|--------|------|
| `list_workspace_files` | 列出工作区中的文件和目录 |
| `search_workspace_text` | 在工作区中搜索文本内容 |
| `read_workspace_file` | 读取工作区中的文本文件 |

### 4. 数据模型

**ProviderProfile**: AI 提供商配置
```csharp
record ProviderProfile(
    Guid Id,
    string Name,
    string Endpoint,
    string Model,
    ApiStyle ApiStyle,
    string SecretRef,  // DPAPI 加密后的密钥引用
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);
```

**ConversationRecord**: 对话记录
**MessageRecord**: 消息记录（支持流式状态）
**WorkspaceRoot**: 工作区根目录配置

## 开发约定

### 代码风格

- 使用 `ImplicitUsings` 和 `Nullable` 启用隐式 using 和可空引用类型
- 优先使用 `record` 类型定义不可变数据模型
- 异步方法使用 `Async` 后缀
- 使用 `CancellationToken` 传播取消操作

### 测试

- 使用 **xUnit** 作为测试框架
- 使用 **FluentAssertions** 进行断言
- 测试项目引用 Core 和 Infrastructure 层的 DLL（而非项目引用），以确保测试的是编译后的程序集

### 数据访问

- 使用 **Dapper** 风格的原始 SQL 进行 SQLite 操作
- 数据库在应用启动时自动初始化（调用 `InitializeAsync()`）
- 数据存储在 `%LOCALAPPDATA%\SelfClaw\selfclaw.db`

### 安全

- API 密钥使用 Windows DPAPI 加密存储
- 密钥从不以明文形式保存在内存中（仅在需要时解密）

## 关键文件说明

| 文件 | 说明 |
|------|------|
| `SelfClaw.Desktop/App.xaml.cs` | 应用入口，配置 DI 容器和主题 |
| `SelfClaw.Desktop/ViewModels/MainWindowViewModel.cs` | 主窗口视图模型，核心业务逻辑 |
| `SelfClaw.Infrastructure/Agents/SelfClawAgentChatRuntime.cs` | AI Agent 运行时实现 |
| `SelfClaw.Infrastructure/ServiceCollectionExtensions.cs` | DI 服务注册 |
| `SelfClaw.Core/Interfaces/*.cs` | 核心接口定义 |

## 注意事项

1. **Desktop 项目引用方式**: Desktop 和 Tests 项目使用 `<Reference>` 直接引用 DLL 文件而非 `<ProjectReference>`，这是为了在构建链中正确解析依赖顺序。

2. **WebView2 依赖**: 应用需要 Microsoft Edge WebView2 Runtime 才能正常渲染对话内容。

3. **Windows 专属**: 由于使用了 WPF 和 DPAPI，此应用仅支持 Windows 平台。

4. **SQLite 并发**: 数据库访问使用单例模式，确保并发安全。
