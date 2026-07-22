# SelfClaw 整体运行流程与 Direct / CLI 调用链

> 基于 2026-07-17 当前仓库实际代码整理。本文描述运行中的真实调用关系；设计目标与实施历史分别参见 `ai-provider-system-design.md` 和 `ai-provider-implementation-progress.md`。

## 1. 核心结论

SelfClaw 的一次编程对话回合只有一个桌面入口和一个统一运行时接口：

```text
Vue ChatView
  -> MainWindow.OnTranscriptWebMessageReceived()
  -> MainWindowViewModel.SubmitPromptAsync()
  -> MainWindowViewModel.SendAsync()
  -> IAgentChatRuntime.StreamTurnAsync()
  -> DispatchingAgentChatRuntime.StreamTurnAsync()
       -> DirectAgentChatRuntime.StreamTurnAsync()  [Direct]
       -> CliAgentChatRuntime.StreamTurnAsync()     [CLI]
  -> MainWindowViewModel.HandleAgentStreamEventAsync()
  -> TranscriptRenderState
  -> MainWindow.PostTranscript()
  -> Vue replaceState
```

两种模式只在“如何执行 Agent 并产生流事件”这一段不同：

- **Direct**：进程内读取模型档案、提供商连接和受保护凭据，构造 Microsoft.Extensions.AI `IChatClient`，直接请求远端或本地模型 API；工作区工具也由 SelfClaw 进程执行。
- **CLI**：读取本机已选择的 Claude Code / Codex / OpenCode，生成命令行并启动子进程；认证、服务端点、CLI 自身工具和权限策略均由对应 CLI 管理。
- 两者最终都输出 `IAsyncEnumerable<AgentStreamEvent>`，后续消息更新、工具卡片、用量、终态、SQLite 持久化和 Vue 渲染完全共用。

## 2. 启动与依赖装配

### 2.1 应用启动

入口是 `SelfClaw.Desktop/App.xaml.cs` 的 `App.OnStartup()`：

1. `StoragePaths.CreateDefault()` 解析应用数据、日志、附件和密钥目录。
2. `ConfigureLogging()` 创建 Serilog 文件日志。
3. `Host.CreateApplicationBuilder()` 创建 Generic Host。
4. `AddSelfClawInfrastructure()` 注册数据库、提供商、工具和两套运行时。
5. Desktop 层注册 `DesktopAgentStore`、`ProgrammingAssistantSettingsService`、`AiProviderSettingsBridge`、`DesktopToolApprovalHandler`、`MainWindowViewModel`、`MainWindow` 等单例。
6. `_host.StartAsync()` 启动容器。
7. 依次执行：
   - `IConversationRepository.InitializeAsync()`；
   - `IAiProviderRepository.InitializeAsync()`；
   - `ProgrammingAssistantSettingsService.GetOrInitializeAsync()`，首次启动时扫描本机 CLI 并保存结果。
8. 解析并显示 `MainWindow`。

### 2.2 运行时 DI 关系

`SelfClaw.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs` 的 `AddSelfClawInfrastructure()` 注册：

```text
IAgentChatRuntime
  = DispatchingAgentChatRuntime
      |- CliAgentChatRuntime
      |   |- CliAgentRegistry
      |   |- CliSessionResolver -> ICliAgentSessionStore
      |   |- CliCommandResolver
      |   `- ICliAgentProcessHost
      `- DirectAgentChatRuntime
          |- IAiChatClientFactory
          |   |- IAiProviderRepository
          |   |- IAiProviderRegistry -> IAiProviderAdapter[]
          |   `- ISecretProtector
          `- WorkspaceAgentToolset -> IWorkspaceToolService
```

`DispatchingAgentChatRuntime` 是 Desktop 唯一注入的 `IAgentChatRuntime` 实现。Desktop 不直接选择具体运行时。

### 2.3 窗口与前端初始化

`MainWindow.OnLoadedAsync()` 依次执行：

1. `EnsureTranscriptHostAsync()` 初始化 WebView2、注册 `WebMessageReceived`，并加载构建后的 `Assets/TranscriptVue/index.html`。
2. `MainWindowViewModel.InitializeAsync()`：
   - 从 `DesktopSettingsJsonStore` 恢复 composer 的 `cli/direct` 覆盖值；
   - `ReloadAgents()` 从 `{AppData}/agents/*.md` 加载 Agent；
   - 加载工作区和会话列表；
   - `PublishShell(false)` 发布首个 transcript 快照。

## 3. 模式、模型与 Agent 如何确定

### 3.1 Agent 自带模式

`DesktopAgentStore` 从 Agent markdown front matter 的 `mode` 读取：

```yaml
mode: direct
# 或
mode: cli
```

`MainWindowViewModel.ResolveRuntimeAgent()` 把 `DesktopAgentDefinition` 转换为 `AgentRuntimeDefinition`，保留 `agent.Mode`、Instructions、Skills 和 ToolPolicy。

当前该方法明确传入：

```csharp
McpServers: [],
ConfiguredMcpServers: []
```

因此 markdown 中声明的 MCP server 目前不会进入实际运行时。

### 3.2 Composer 模式覆盖优先

前端 `ModelSelector.vue` 发送 `select-composer-mode`，调用链为：

```text
ModelSelector.vue
  -> MainWindow.OnTranscriptWebMessageReceived()
  -> MainWindowViewModel.SelectComposerModeAsync()
  -> DesktopSettingsJsonStore.WriteNodeAsync("composer", ...)
```

每次发送时，`SendAsync()` 执行：

```csharp
runtimeAgent = runtimeAgent with
{
    Mode = ResolveComposerExecutionMode(runtimeAgent.Mode)
};
```

实际优先级为：

```text
composer 显式覆盖模式 > Agent markdown 的 mode
```

所以 Direct Agent 可以临时走 CLI，CLI Agent 也可以临时走 Direct，不需要修改 Agent 文件。

### 3.3 Direct 模型选择

前端读取 Direct 模型：

```text
ModelSelector.requestDirectModels()
  -> ai-providers/list-enabled-models
  -> MainWindow.OnTranscriptWebMessageReceived()
  -> AiProviderSettingsBridge.TryHandleAsync()
  -> IAiProviderSettingsService.ListEnabledModelsAsync()
  -> IAiProviderSettingsService.GetDefaultModelAsync("desktop-default")
  -> AiProviderSettingsBridge.ModelSelectionChanged
  -> MainWindow.OnModelSelectionChanged()
  -> MainWindowViewModel.SelectModelProfile()
```

用户改选模型时：

```text
ModelSelector.pickDirectModel()
  -> ai-providers/set-default-model
  -> AiProviderSettingsBridge.TryHandleAsync()
  -> AiProviderSettingsService.SetDefaultModelAsync()
  -> IAiProviderRepository.SetModelProfileSelectionAsync()
  -> ModelSelectionChanged(modelProfileId)
  -> MainWindowViewModel.SelectModelProfile(modelProfileId)
```

`SendAsync()` 把当前 `_selectedModelProfileId` 放进 `ChatTurnRequest.ModelProfileId`。Direct runtime 优先用该 id；为空时再读取 `desktop-default`。

### 3.4 CLI、模型和推理档位选择

CLI 的检测与选择由 `ProgrammingAssistantSettingsService` 管理：

- 启动时 `GetOrInitializeAsync()` -> `ScanCoreAsync()` -> `ScanDefinitionAsync()` 扫描 PATH 中的 Claude Code、Codex、OpenCode。
- `SelectCliAsync()` 保存当前 CLI；切换 CLI 时会清空之前的 model/reasoning 选择。
- `SelectModelAsync()` 与 `SelectReasoningLevelAsync()` 保存 CLI 参数覆盖值。
- “使用 CLI 默认值”最终归一化为 `null`。

每次发送时，`SendAsync()` 调用 `GetSelectedInvocationAsync()` 得到：

```csharp
CliInvocationSelection(
    CliAgentKind Kind,
    string? Model,
    string? ReasoningEffort)
```

并填入 `ChatTurnRequest.CliAgent/CliModel/CliReasoningEffort`。Direct 分支会忽略这三个字段。

## 4. 一次回合的公共入口

### 4.1 Vue 到 Desktop

`SelfClaw.TranscriptVue/src/views/ChatView.vue` 的提交逻辑发送：

```javascript
post({ type: 'send-prompt', prompt })
```

WebView2 消息进入 `MainWindow.OnTranscriptWebMessageReceived()`：

```text
case "send-prompt"
  -> 读取 prompt
  -> await _viewModel.SubmitPromptAsync(prompt)
```

`SubmitPromptAsync()` 记录 composer 文本和待发送图片，然后调用 `SendAsync()`。

### 4.2 `MainWindowViewModel.SendAsync()` 的公共编排

该方法是一次回合真正的 Desktop 编排入口，顺序如下：

1. 固化本轮 prompt、图片、模型、工作区、权限和当前 transcript 快照。
2. `EnsureConversationAsync()`：复用当前会话，或由 `CreateConversationRecord()` 创建新会话对象。
3. 防止同一 conversation 同时运行两个回合：`IsConversationRunning()`。
4. `PersistConversationAsync()` -> `IConversationRepository.UpsertConversationAsync()` 保存工作区、会话模式和工具权限。
5. `ResolveRuntimeAgent()` 生成运行时 Agent，并用 `ResolveComposerExecutionMode()` 应用模式覆盖。
6. `StartConversationRuntimeState()` 建立本轮内存状态和 `CancellationTokenSource`。
7. `PersistPromptImageAttachmentsAsync()` 把附件复制到应用附件目录并生成记录。
8. 创建 `MessageRole.User` 的 `MessageRecord`，调用 `UpsertMessageAsync()` 落库。
9. 新会话用 `CreateConversationTitle()` 从首条输入生成标题，再次持久化会话。
10. `ProgrammingAssistantSettingsService.GetSelectedInvocationAsync()` 读取 CLI 选择。
11. 创建 `AgentTurnState`，`EnsureAssistantMessage()` 立即放入 Streaming 状态的 assistant 占位消息。
12. 构造 `ChatTurnRequest`，调用 `_agentChatRuntime.StreamTurnAsync()`。
13. 对每个事件调用 `HandleAgentStreamEventAsync()`。
14. 流正常结束后调用 `PublishConversationCompletedNotification()`。
15. `finally` 中执行 `CompleteConversationRuntimeState()` 并释放本轮 CTS。

### 4.3 `ChatTurnRequest` 字段归属

| 字段 | 公共 | Direct 使用 | CLI 使用 |
|---|---:|---:|---:|
| `ConversationId` | 是 | 工具审批关联 | CLI session 关联 |
| `ModelProfileId` |  | 是 | 忽略 |
| `WorkspaceRoot` | 是 | 决定是否注入工作区工具 | 子进程工作目录 |
| `Agent` | 是 | Mode、Instructions | Mode、Instructions |
| `CliAgent` |  | 忽略 | 是 |
| `CliModel` |  | 忽略 | 可选命令行参数 |
| `CliReasoningEffort` |  | 忽略 | 可选命令行参数 |
| `ToolPermissionMode` |  | SelfClaw 工具审批 | 不控制 CLI 自身权限 |
| `ToolApprovalHandler` |  | 写文件/命令审批 | 忽略 |
| `Messages` | 是 | 构造完整消息历史 | 只抽取最新 user prompt |

### 4.4 统一分发

`DispatchingAgentChatRuntime.StreamTurnAsync()` 是唯一对 Desktop 暴露的 runtime interface。它先按
`request.Agent.Mode` 选择内部 adapter：

```text
AgentExecutionMode.Direct
  -> DirectAgentChatRuntime.StreamTurnAsync()

AgentExecutionMode.Cli
  -> CliAgentChatRuntime.StreamTurnAsync()
```

随后 dispatcher 统一执行终态纪律：成功或失败恰好一个 `RunCompletedEvent`，并且它总是最后一个事件；
重复终态和终态后的事件会被丢弃，缺失终态或非取消异常会补为失败。首个候选终态出现后结果即锁定，
dispatcher 在 adapter 结束和释放完成后才输出该终态；若 adapter 不配合，则有界 cleanup 到期后停止等待。
取消继续抛出 `OperationCanceledException`，不会转换为终态事件。

## 5. Direct 模式完整调用链

### 5.1 总调用链

```text
DispatchingAgentChatRuntime.StreamTurnAsync()
  -> DirectAgentChatRuntime.StreamTurnAsync()
  -> DirectAgentChatRuntime.StreamCoreAsync()
       -> Channel<AgentStreamEvent>
       -> DirectAgentChatRuntime.ProduceEventsAsync()
            |- WorkspaceAgentToolset.CreateTools()
            |- IAiChatClientFactory.CreateAsync(modelProfileId)
            |    或 CreateForScopeAsync("desktop-default")
            |- DirectAgentChatRuntime.BuildMessages()
            |- IChatClient.GetStreamingResponseAsync()
            `- 把 ChatResponseUpdate.Contents 转为 AgentStreamEvent
```

`StreamCoreAsync()` 使用无界 `Channel<AgentStreamEvent>` 隔离 provider 生产侧与 UI 消费侧。枚举器被放弃时，linked CTS 会取消 provider 流，并等待 producer 收尾。

### 5.2 工作区工具创建

`ProduceEventsAsync()` 先判断 `request.WorkspaceRoot`：

- 无工作区：`tools = Array.Empty<AITool>()`，Direct 模型没有 SelfClaw 工作区工具。
- 有工作区：调用 `WorkspaceAgentToolset.CreateTools()` 创建 5 个 M.E.AI function tools：

| 工具 | 绑定方法 | 审批 |
|---|---|---|
| `list_files` | `BoundWorkspaceTools.ListFilesAsync()` | 不需要 |
| `search_text` | `BoundWorkspaceTools.SearchTextAsync()` | 不需要 |
| `read_file` | `BoundWorkspaceTools.ReadFileAsync()` | 不需要 |
| `write_file` | `BoundWorkspaceTools.WriteFileAsync()` | `RequireApproval` 时需要 |
| `run_shell_command` | `BoundWorkspaceTools.RunShellCommandAsync()` | `RequireApproval` 时需要 |

底层全部进入 `WorkspaceToolService`，其中路径操作通过 `NormalizeRoot()` / `ResolvePath()` 限制在 workspace 内。

#### 工具审批链

`write_file` 和 `run_shell_command` 调用 `BoundWorkspaceTools.IsApprovedAsync()`：

```text
ToolPermissionMode.FullAccess
  -> 直接允许

ToolPermissionMode.RequireApproval
  -> DesktopToolApprovalHandler.RequestApprovalAsync()
  -> ApprovalRequested 事件
  -> MainWindow.OnToolApprovalRequested()
       |- 始终发送 Windows toast
       `- 窗口可见且非最小化时显示 WPF Yes/No 对话框
  -> DesktopToolApprovalHandler.TryResolve()
  -> 允许：调用 WorkspaceToolService
  -> 拒绝/超时：返回 "User denied this tool call."
```

审批默认 5 分钟超时；取消、订阅处理异常或窗口关闭均不会无限等待。窗口隐藏时依靠 toast 的 Confirm/Cancel，由 `DesktopNotificationActivationService.HandleActivationAsync()` 调用 `TryResolve()`。

### 5.3 模型 client 创建

Direct runtime 构造：

```csharp
var inputs = new AiChatRuntimeInputs(
    EnableReasoning: false,
    Tools: tools);
```

然后：

```text
有 request.ModelProfileId
  -> AiChatClientFactory.CreateAsync(modelProfileId, inputs)

无 request.ModelProfileId
  -> AiChatClientFactory.CreateForScopeAsync("desktop-default", inputs)
  -> IAiProviderRepository.GetModelProfileSelectionAsync()
  -> AiChatClientFactory.CreateAsync(selection.ModelProfileId, inputs)
```

`AiChatClientFactory.CreateAsync()` 的方法级流程：

1. `IAiProviderRepository.GetModelProfileAsync()` 读取 `ai_model_profiles`，检查存在且启用。
2. `GetProviderConnectionAsync()` 读取 `ai_provider_connections`，检查存在且启用。
3. `ResolveSecretsAsync()`：
   - `AuthKind.None` 返回空字典；
   - `AuthKind.ApiKey` 根据 `CredentialRefs` 调用 `ISecretProtector.RetrieveSecretAsync()`；
   - 明文密钥只在 Infrastructure 的本轮内存中出现，不进入 `ChatTurnRequest`。
4. `IAiProviderRegistry.GetRequiredAdapter(connection.ProviderKind)` 选择适配器。
5. `adapter.SupportsApiFormat(profile.ApiFormat)` 验证协议。
6. 创建 `AiProviderClientRequest(connection, profile, secrets, EnableReasoning, Tools)`。
7. `adapter.CreateChatOptions()` 创建协议选项。
8. `adapter.CreateChatClient()` 创建原生 `IChatClient`。
9. `new ChatClientBuilder(nativeClient)`：
   - `.UseFunctionInvocation()` 自动执行模型 function call，并把 function result 继续送回模型；
   - `.UseLogging()` 添加日志管道；
   - Trace 被 `NonSensitiveLoggerFactory` 永久屏蔽，避免原始消息和 options 进入日志。
10. 返回 `AiChatClientLease(Client, Options, Profile)`，本轮结束时由 runtime `Dispose()`。

### 5.4 当前 provider adapter 分流

| ProviderKind | Adapter | 当前实际聊天协议 |
|---|---|---|
| `OpenAI` | `OpenAiProviderAdapter` | Chat Completions、Responses |
| `OpenAICompatible` | `OpenAiProviderAdapter` | Chat Completions、Responses |
| `DeepSeek` | `OpenAiProviderAdapter` | OpenAI 兼容管道；目录配置使用 Chat Completions |
| `Anthropic` | `AnthropicProviderAdapter` | Anthropic Messages |
| `Ollama` | `OllamaProviderAdapter` | Ollama Native、OpenAI Chat Completions |
| `GoogleGemini` | `GeminiProviderAdapter` | 当前通过 Google 官方 OpenAI 兼容入口走 Chat Completions |
| `AzureOpenAI` | `AzureOpenAiProviderAdapter` | Azure OpenAI Chat Completions，模型字段作为 deployment 名 |

适配器只负责把统一的连接、档案、密钥、工具和模型参数转换成具体 SDK client/options；Direct runtime 不包含 provider 特例。

### 5.5 消息构造与模型请求

`DirectAgentChatRuntime.BuildMessages()`：

1. `request.Agent.Instructions` 非空时添加 `ChatRole.System`。
2. 遍历 `request.Messages`：
   - 跳过 `MessageStatus.Failed`；
   - 跳过空文本；
   - User -> `ChatRole.User`；
   - Assistant -> `ChatRole.Assistant`；
   - 其他角色跳过。
3. 把完整可用历史传给：

```csharp
lease.Client.GetStreamingResponseAsync(
    messages,
    lease.Options,
    cancellationToken)
```

### 5.6 M.E.AI 内容到统一事件的映射

`ProduceEventsAsync()` 先发：

```text
RunStartedEvent("direct-<guid>", lease.Profile.Model, AgentKind: null)
RunStatusEvent(Requesting)
```

随后逐个处理 `ChatResponseUpdate.Contents`：

| M.E.AI content | 输出事件 |
|---|---|
| `TextContent` | `AssistantTextDeltaEvent`，同时累积 `finalText` |
| `TextReasoningContent` | `AssistantThinkingDeltaEvent` |
| `FunctionCallContent` | 去重后输出 `ToolCallStartedEvent` |
| `FunctionResultContent` | `DescribeToolResult()` -> `ToolCallCompletedEvent` |
| `UsageContent` | 累加 input/output tokens，流末输出 `UsageReportedEvent` |

正常结束输出：

```text
RunCompletedEvent(Succeeded, finalText)
```

Direct adapter 只负责分类 mode-specific 结果：正常完成时产生成功候选终态，非取消异常产生失败候选终态；
`OperationCanceledException` 始终继续向上传播。dispatcher 锁定首个候选终态，在 adapter 结束并释放 client lease 后，
再把该终态作为最后一个事件输出；cleanup 超时只记录诊断，不改写已锁定结果。缺失终态或非取消异常也由 dispatcher
统一补成失败。

## 6. CLI 模式完整调用链

### 6.1 总调用链

```text
DispatchingAgentChatRuntime.StreamTurnAsync()
  -> CliAgentChatRuntime.StreamTurnAsync()
       |- CliAgentRegistry.Find()
       |- ExtractPrompt()
       |- CliSessionResolver.PrepareAsync()
       |- ComposeSystemPrompt()
       |- CliAgentDefinition.BuildArgs()
       |- CliCommandResolver.Resolve()
       |- ICliAgentProcessHost.Start()
       |- CliAgentDefinition.BuildStdinLines()
       |- ICliAgentProcessSession.WriteStdinLineAsync()
       |- ICliAgentProcessSession.CompleteStdinAsync()
       |- ICliAgentProcessSession.ReadOutputLinesAsync()
       |- IAgentStreamParser.Feed()/Flush()
       |- CliSessionResolver.CaptureAsync()
       `- ICliAgentProcessSession.WaitForExitAsync()
```

### 6.2 前置校验

`CliAgentChatRuntime.StreamTurnAsync()` 依次检查：

1. `request.CliAgent` 是否存在；没有已选 CLI 时直接返回可读的失败 `RunCompletedEvent`。
2. `CliAgentRegistry.Find(agentKind)` 是否能找到定义。
3. `ExtractPrompt(request.Messages)` 是否能找到最新一条 User 消息。

CLI 不重放 SelfClaw 数据库里的完整历史。`ExtractPrompt()` 只发送最新 user 文本，历史上下文由 CLI 的 resume session 维护。

### 6.3 会话恢复

`CliSessionResolver.PrepareAsync()` 先调用：

```text
ICliAgentSessionStore.GetSessionIdAsync(conversationId, agentKind)
```

`SqliteCliAgentSessionStore` 使用 `cli_agent_sessions` 表，以 `(conversation_id, agent_kind)` 为联合键。同一个 SelfClaw 会话可分别保存 Claude、Codex、OpenCode session id，互不覆盖。

两种 resume 策略：

| CLI | `ResumeStrategy` | 新会话 | 后续会话 |
|---|---|---|---|
| Claude Code | `Specified` | SelfClaw 生成 GUID，作为 `NewSessionId` | 读取已存 id 作为 `ResumeSessionId` |
| Codex | `CapturedFromStream` | CLI 自己创建 thread id | 使用流中捕获并已保存的 id |
| OpenCode | `CapturedFromStream` | CLI 自己创建 session id | 使用流中捕获并已保存的 id |

parser 输出 `RunStartedEvent` 后，runtime 调用：

```text
CliSessionResolver.CaptureAsync()
  -> ICliAgentSessionStore.SetSessionIdAsync()
```

只有 CLI 流确认 session id 后才持久化，避免启动失败留下无效 id。

### 6.4 `CliRunContext` 与参数生成

runtime 构造 `CliRunContext`：

- `WorkingDirectory`：优先 `WorkspaceRoot.RootPath`；否则 Desktop；再否则 UserProfile/Temp。
- `ResumeSessionId/NewSessionId`：来自 `CliSessionResolver`。
- `SystemPrompt`：`ComposeSystemPrompt()` 当前只返回 `AgentRuntimeDefinition.Instructions`。
- `Model/ReasoningEffort`：来自 `ProgrammingAssistantSettingsService`，空白归一化为 `null`。

各定义实际生成：

#### Claude Code

`ClaudeAgentDefinition.BuildArgs()`：

```text
claude -p
  --input-format stream-json
  --output-format stream-json
  --verbose
  --include-partial-messages
  [--resume <id> | --session-id <new-guid>]
  [--model <model>]
  [--effort <level>]
  [--append-system-prompt <instructions>]
```

`BuildStdinLines()` 把 prompt 包成一行 Anthropic user message JSONL。

#### Codex

`CodexAgentDefinition.BuildArgs()`：

```text
codex exec [resume <thread-id>]
  --json
  --skip-git-repo-check
  [--model <model>]
  [-c model_reasoning_effort="<level>"]
```

`BuildStdinLines()` 直接返回一行纯文本 prompt。当前 Codex 定义没有把 Agent Instructions 拼进参数。

#### OpenCode

`OpenCodeAgentDefinition.BuildArgs()`：

```text
opencode run --format json
  [-s <session-id>]
  [--model <provider/model>]
```

`BuildStdinLines()` 直接返回一行纯文本 prompt。当前 OpenCode 不接收 reasoning 覆盖，也没有把 Agent Instructions 拼进参数。

### 6.5 命令解析与子进程启动

`CliCommandResolver.Resolve()`：

1. 在 PATH/PATHEXT 中解析真实可执行文件。
2. Windows 下优先可启动扩展，避免误选 npm 同目录的无扩展 POSIX shim。
3. `.cmd/.bat` 通过 `cmd.exe /d /s /c` 包装并转义参数。
4. 原生 exe 直接生成 `CommandInvocation.ArgumentList`。

`CliAgentProcessHost.Start()` 创建 `ProcessStartInfo`：

- 指定 WorkingDirectory；
- 重定向 stdin/stdout/stderr；
- UTF-8 编码；
- `UseShellExecute = false`；
- `CreateNoWindow = true`。

`CliProcessStartInfo` 不注入 AI provider 环境变量。CLI 继续使用自己的登录状态、API Key、base URL 和本地配置。

### 6.6 stdin、stdout 与进程生命周期

启动后 runtime：

1. 遍历 `definition.BuildStdinLines(prompt)`，调用 `session.WriteStdinLineAsync()`。
2. `session.CompleteStdinAsync()` 关闭 stdin，以 EOF 表示本轮输入结束。
3. `session.ReadOutputLinesAsync()` 持续读取 stdout 行。
4. 每行补回 `\n`，调用 `parser.Feed(line + '\n')`，立即输出解析到的 `AgentStreamEvent`。
5. stdout 结束后调用 `parser.Flush()` 处理残余缓冲。
6. `session.WaitForExitAsync()` 得到 `CliProcessResult`。

`CliAgentProcessSession` 同时负责：

- 后台泵送 stdout/stderr；
- stderr 最多保留 64 KiB；
- stdout/stderr 活动刷新 watchdog；
- 无活动超时后 `Kill(entireProcessTree: true)`；
- 外部取消或 Dispose 时终止进程树；
- 按超时和 exit code 分类成功或失败；外部取消通过 `OperationCanceledException` 传播。

### 6.7 CLI 输出解析

parser 由 `CreateParser()` 决定：

```text
ClaudeStreamJson
  -> ClaudeStreamJsonParser

JsonEventStream (Codex / OpenCode)
  -> JsonEventStreamParser(kind)
```

#### `ClaudeStreamJsonParser`

主要映射：

- `system/init` -> `RunStartedEvent(sessionId, model, Claude)`；
- partial/full assistant text -> `AssistantTextDeltaEvent`；
- thinking delta -> `AssistantThinkingDeltaEvent`；
- `tool_use` -> `ToolCallStartedEvent`；
- user `tool_result` -> `ToolCallCompletedEvent`；
- `result.usage` -> `UsageReportedEvent`；
- `result` -> `RunCompletedEvent`。

#### `JsonEventStreamParser`

Codex 主要处理 `thread.* / turn.* / item.*`；OpenCode 主要处理 `step_start / text / reasoning / tool / step_finish`。两者都会映射：

- `RunStartedEvent`；
- `AssistantTextDeltaEvent` / `AssistantThinkingDeltaEvent`；
- `ToolCallStartedEvent` / `ToolCallCompletedEvent`；
- `UsageReportedEvent`。

Codex 的 `error` 事件会额外输出失败的 `RunCompletedEvent`。正常的 Codex `turn.completed` 和 OpenCode `step_finish` 只报告 usage，最终 `RunCompletedEvent` 由 `CliAgentChatRuntime` 根据进程退出结果补发。

非法 JSON 行会成为 `RawOutputEvent`；合法但不认识的事件类型被忽略。工具事件在 CLI 模式只是“观察记录”，实际工具由 CLI 子进程自行执行。

### 6.8 CLI 终态

如果 parser 已输出 `RunCompletedEvent`，CLI adapter 不重复输出；dispatcher 还会统一保证终态只出现一次并位于流末尾。

如果进程退出但流里没有终态，`CliAgentChatRuntime` 根据 `CliProcessResult` 补一个：

```text
RunCompletedEvent(
  result.Status,
  FinalText: null,
  ErrorMessage: writeError ?? BuildExitError(result))
```

`BuildExitError()` 的优先级是：无活动超时提示 > stderr > exit code > 未知异常。

外部 cancellation 在 CLI 读写/等待链上继续抛出 `OperationCanceledException`，由 Desktop 的 turn finalizer 统一处理。

## 7. 两种模式共用的事件消费、落库与渲染

### 7.1 `HandleAgentStreamEventAsync()`

`MainWindowViewModel.SendAsync()` 对 runtime 的每个事件调用该方法：

| `AgentStreamEvent` | Desktop 处理 |
|---|---|
| `RunStartedEvent` | `EnsureAssistantMessage()` |
| `AssistantTextDeltaEvent` | `ApplyAssistantDelta()` 追加 markdown |
| `AssistantThinkingDeltaEvent` | `AssistantMessageSegmenter.WrapThinking()` 后追加 |
| `ToolCallStartedEvent` | `StartToolRunAsync()` |
| `ToolCallCompletedEvent` | `CompleteToolRunAsync()` |
| `UsageReportedEvent` | 更新本轮 input/output token |
| `RunStatusEvent` | 更新 activity text 并发布 UI |
| `RunCompletedEvent` | `CompleteAssistantTurnAsync()` |
| `RawOutputEvent` | 当前不进入 transcript |
| `PermissionRequestedEvent` | 当前不进入 transcript |

### 7.2 Assistant 消息

`EnsureAssistantMessage()` 创建一次 `MessageStatus.Streaming` 的 assistant 消息，只在内存中更新。文本 delta 通过 `ApplyAssistantDelta()` 追加并节流发布到 UI。

收到终态后，Desktop turn finalizer：

1. 标记本轮完成并移出 `ActiveMessageIds`。
2. `AssistantMessageSegmenter.MergeFinalMarkdown()` 合并 runtime final text 与已流式内容，避免重复。
3. 成功 -> `MessageStatus.Completed`；失败 -> `MessageStatus.Failed`；用户取消 -> `MessageStatus.Cancelled`。
4. 写入 token、duration 和 error message。
5. 通过聚焦的原子写入一次持久化最终 assistant 消息与本轮未终结工具；重复收尾不会改写首个结果。
6. `PublishRuntimeStateNow()` 立即发布最终快照。

流式 assistant 文本不会每个 delta 都写 SQLite，只在收尾时写一次。runtime 的失败终态、Desktop 消费事件时的异常和用户取消
分别进入 turn finalizer 的失败或取消路径，由它原子落库 assistant 最终状态与本轮未终结工具状态。

### 7.3 工具运行记录

`StartToolRunAsync()`：

1. 创建 `ToolExecutionStatus.Running` 的 `ToolExecutionRecord`。
2. `CaptureToolRunAnchor()` 在 assistant markdown 中插入工具锚点。
3. 以 `ToolCallId` 记录关联。
4. `UpsertToolExecutionAsync()` 立即落库。

`CompleteToolRunAsync()`：

1. 按 `ToolCallId` 找到 started record。
2. 更新完成/失败/取消状态、摘要、完整结果和 duration。
3. 再次 `UpsertToolExecutionAsync()`。

这套逻辑不区分 Direct 工具和 CLI 工具。

### 7.4 Transcript 发布到 Vue

事件处理最终触发：

```text
PublishRuntimeState()
  -> RequestStreamingShellPublish()       // 75ms 节流
  -> PublishShell()
       |- BuildShellFingerprint()          // 相同快照去重
       |- TranscriptToolRunPresenter.BuildToolRunsByMessageId()
       |- BuildMessageItemCached()
       `- TranscriptChanged(TranscriptRenderState)
  -> MainWindow.OnTranscriptChanged()
  -> MainWindow.PostTranscript()
  -> CoreWebView2.PostWebMessageAsJson({ type: "replaceState", ... })
  -> App.vue.handleIncomingMessage()
  -> ChatView.replaceState()
```

`TranscriptRenderState` 包含 items、会话列表、选中会话、busy/activity 状态和当前 execution mode。Vue 不感知底层是 Direct 还是 CLI。

## 8. 停止、失败与资源释放

前端发送 `stop-generation` 后：

```text
MainWindow.OnTranscriptWebMessageReceived()
  -> MainWindowViewModel.StopSelectedConversation()
  -> ConversationRuntimeState.CancellationTokenSource.Cancel()
```

取消 token 贯穿 Desktop -> dispatcher -> Direct provider 或 CLI process session。

- **Direct**：取消继续抛出 `OperationCanceledException`，枚举释放时取消 provider stream 并释放 client lease。
- **CLI**：取消会中止读写/等待，并由 `CliAgentProcessSession` 杀掉进程树；异常返回 Desktop 的 cancellation catch。
- **Desktop**：turn finalizer 把 assistant 标记为 `MessageStatus.Cancelled`，保留 partial text、token 与 duration，并把本轮未终结工具标记为 `ToolExecutionStatus.Cancelled` 后原子落库。
- `SendAsync()` 的 `finally` 总会 `CompleteConversationRuntimeState()`，解除 busy 状态并释放 CTS。

dispatcher 统一保证成功/失败路径恰好产生一个 terminal event；Direct/CLI adapters 只负责把各自 implementation 的结果分类。取消不是 terminal event，而是始终重抛的控制流。

## 9. Direct 与 CLI 的关键差异

| 维度 | Direct | CLI |
|---|---|---|
| 执行位置 | SelfClaw 进程内 | 独立子进程 |
| 认证来源 | SQLite credential ref + DPAPI secret | CLI 本地登录/配置 |
| 模型来源 | `ai_model_profiles` / `desktop-default` | `ProgrammingAssistantSettingsService` |
| 请求协议 | M.E.AI + provider adapter | CLI 自己的协议 |
| 对话历史 | SelfClaw 组装完整有效消息历史 | 只发送最新 user 文本，通过 CLI session resume 保持历史 |
| Agent Instructions | 作为 system message | Claude 使用 `--append-system-prompt`；Codex/OpenCode 当前未注入 |
| 工作目录 | 工具绑定 workspace | 子进程 WorkingDirectory |
| 工具执行 | `WorkspaceToolService` | CLI 自己执行 |
| 工具审批 | SelfClaw 控制写文件/命令 | CLI 自己的权限策略，SelfClaw 仅观察事件 |
| 工具事件 | M.E.AI function call/result | stdout parser 解析 |
| 会话 id | 每轮临时 `direct-<guid>`，不用于恢复 | SQLite 持久化并恢复 |
| 取消 | 取消 provider stream | 杀掉 CLI 进程树 |
| 输出 | 统一 `AgentStreamEvent` | 统一 `AgentStreamEvent` |

## 10. 当前实现边界

以下是当前代码的实际行为，排查问题时需要特别注意：

1. **MCP 尚未接线**：`ResolveRuntimeAgent()` 把两类 MCP 列表都传为空；Direct 和 CLI 都不会从 Agent markdown 获得 MCP 配置。
2. **图片尚未进入当前 WebView 对话主链**：`SubmitPromptAsync()` 虽支持可选图片，当前 `MainWindow` 处理 `send-prompt` 时只传 prompt。即使其他调用方传入图片，`SendAsync()` 也只会持久化和展示附件；`DirectAgentChatRuntime.BuildMessages()` 只创建文本 `ChatMessage`，CLI 的 `ExtractPrompt()` 也只取文本。
3. **Direct reasoning 开关固定关闭**：`AiChatRuntimeInputs.EnableReasoning` 当前为 `false`。若 provider 流仍返回 `TextReasoningContent`，runtime 可以显示，但本轮不会主动启用 adapter reasoning options。
4. **Direct 工具依赖 workspace**：未选工作区时完全不创建 `AITool`。
5. **Agent ToolPolicy/Skills 当前不参与 runtime 组装**：字段被保留在 `AgentRuntimeDefinition`，但 Direct 的工具集合只由 workspace 和权限决定；CLI 只使用 Instructions（且仅 Claude 定义实际注入）。
6. **CLI 不使用 Direct provider 配置**：不会读取 `ModelProfileId`、provider connection 或 DPAPI 密钥，也不会注入到子进程环境。
7. **Direct 每轮读取当前档案**：client lease 不跨回合缓存，因此连接、模型启用状态和密钥变更会在下一轮生效。
8. **CLI session 按会话和 CLI 隔离**：切换 CLI 不会复用另一个 CLI 的 session；切回原 CLI 时仍可恢复其旧 session。
9. **`PermissionRequestedEvent` 是预留契约**：当前 CLI 第一版不发该事件，Desktop 的 switch 也不展示它。
10. **会话 UI 与运行解耦**：`ConversationRuntimeState` 按 conversation id 保存，切换会话不会取消后台回合；只有选中会话才持续同步到当前 transcript 显示。

## 11. 关键方法索引

### 公共入口与渲染

- `App.OnStartup()`：应用、数据库、CLI 设置初始化。
- `MainWindow.EnsureTranscriptHostAsync()`：WebView2 和 Vue 静态资源初始化。
- `MainWindow.OnTranscriptWebMessageReceived()`：所有 Vue -> Desktop 消息入口。
- `MainWindowViewModel.SubmitPromptAsync()`：prompt 入口。
- `MainWindowViewModel.SendAsync()`：单回合总编排。
- `MainWindowViewModel.HandleAgentStreamEventAsync()`：统一事件消费。
- `MainWindowViewModel.PublishShell()`：构造 `TranscriptRenderState`。
- `MainWindow.PostTranscript()`：向 Vue 发送 `replaceState`。

### 模式与请求

- `MainWindowViewModel.ResolveRuntimeAgent()`：Desktop Agent -> runtime Agent。
- `MainWindowViewModel.ResolveComposerExecutionMode()`：应用 composer 模式覆盖。
- `ProgrammingAssistantSettingsService.GetSelectedInvocationAsync()`：读取 CLI/model/reasoning。
- `MainWindowViewModel.SelectModelProfile()`：更新 Direct 本轮模型 id。
- `DispatchingAgentChatRuntime.StreamTurnAsync()`：Direct/CLI 分支点。

### Direct

- `DirectAgentChatRuntime.StreamTurnAsync()` / `StreamCoreAsync()`：Direct 事件枚举入口。
- `DirectAgentChatRuntime.ProduceEventsAsync()`：client、请求、内容翻译和终态。
- `DirectAgentChatRuntime.BuildMessages()`：组装 system + 历史消息。
- `AiChatClientFactory.CreateForScopeAsync()`：从 `desktop-default` 找模型。
- `AiChatClientFactory.CreateAsync()`：验证档案/连接、解密、adapter 和 M.E.AI pipeline。
- `WorkspaceAgentToolset.CreateTools()`：绑定 5 个工作区函数。
- `DesktopToolApprovalHandler.RequestApprovalAsync()`：Direct 写操作审批。

### CLI

- `CliAgentChatRuntime.StreamTurnAsync()`：CLI 单回合总编排。
- `CliSessionResolver.PrepareAsync()` / `CaptureAsync()`：session 恢复与持久化。
- `ClaudeAgentDefinition.BuildArgs()` / `BuildStdinLines()`：Claude 命令与 JSONL。
- `CodexAgentDefinition.BuildArgs()` / `BuildStdinLines()`：Codex 命令与文本输入。
- `OpenCodeAgentDefinition.BuildArgs()` / `BuildStdinLines()`：OpenCode 命令与文本输入。
- `CliCommandResolver.Resolve()`：PATH/PATHEXT 和 Windows batch 包装。
- `CliAgentProcessHost.Start()`：启动重定向子进程。
- `CliAgentProcessSession`：stdin/stdout/stderr、watchdog、kill-tree、退出分类。
- `ClaudeStreamJsonParser.Feed()` / `Flush()`：Claude 流解析。
- `JsonEventStreamParser.Feed()` / `Flush()`：Codex/OpenCode 流解析。
