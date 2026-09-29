# SelfClaw 全链路架构图：Input → Direct / CLI → Output

更新：2026-09-29。以当前源码为准；逐项证据见文末「证据文件」。运行顺序与契约细节另见 [运行流程与 Direct / CLI 调用链](runtime-execution-flow.md)、[Direct 架构审查](direct-agent-architecture-review.md)、[Desktop 架构整改](desktop-architecture-review.md)。

本文只描述一条交互回合从输入到渲染的端到端主干，以及两条独立执行分支和它们的旁路通道。

## 0. 总览

```mermaid
flowchart TB
    subgraph FE["① 前端 WebView2 / Vue 3"]
        Composer["ComposerPanel + useChatComposer"]
        HostBridge["hostBridge.js<br/>(唯一 chrome.webview 出口)"]
        TranscriptStore["transcriptBridge + useChatTranscript"]
        Render["TranscriptPanel → MessageBlocks / ToolCard"]
        Side["useChatApprovals / useChatTerminal / useActivityPanel"]
    end

    subgraph ROUTE["② 宿主路由与准入 (SelfClaw.Desktop)"]
        WVR["WebViewMessageRouter.RouteAsync<br/>应用 origin 守卫 → 协议分派"]
        VM["MainWindowViewModel.SubmitPromptAsync<br/>UI 线程捕获选择快照"]
        WS["ConversationWorkspaceService.PrepareAsync<br/>可选 Managed Worktree"]
        ENGINE["ConversationTurnEngine<br/>TryAdmitAsync → ExecuteAsync"]
    end

    subgraph DISPATCH["③ 运行时分派 (SelfClaw.Infrastructure)"]
        DISP["DispatchingAgentChatRuntime<br/>终态协议 + 5s adapter 清理"]
        DIRECT["DirectAgentChatRuntime<br/>Microsoft.Extensions.AI 进程内"]
        CLI["CliAgentChatRuntime<br/>Claude/Codex/OpenCode 子进程"]
    end

    subgraph REDUCE["④ 事件归约与持久化 (Desktop)"]
        REC["ConversationTurnRecorder.ApplyEventAsync"]
        FIN["DesktopTurnFinalizer.TryCommitAsync<br/>原子终态提交"]
        STATE["ConversationRuntimeState<br/>串行录制 + 不可变快照"]
    end

    subgraph PUBLISH["⑤ 输出发布"]
        COORD["ConversationSessionCoordinator"]
        PUB["TranscriptPublisher<br/>Dispatcher 封送 + 120ms 合流"]
        PROJ["TranscriptProjection.Build"]
        DEL["TranscriptDelivery<br/>revision / diff / ACK / 重试"]
        CHAN["WebViewHostChannel.PostPush<br/>→ PostWebMessageAsJson"]
    end

    Composer --> HostBridge --> WVR --> VM --> WS --> ENGINE --> DISP
    DISP --> DIRECT
    DISP --> CLI
    DIRECT --> REC
    CLI --> REC
    REC --> FIN
    REC --> STATE --> COORD --> PUB --> PROJ --> DEL --> CHAN
    CHAN --> HostBridge
    HostBridge --> TranscriptStore --> Render
    FE --- Side
    WVR -.->|"tool-approval / terminal- / activity-panel / plugin-host"| Side
```

Direct 与 CLI 是两条完全独立的执行分支，但在 `AgentStreamEvent` 之后共用同一套 recorder、投影和发布管道。CLI 自带认证、模型与工具策略；Direct 走 SelfClaw 的能力快照。

## 1. Input：从 composer 到准入

```mermaid
flowchart LR
    A["用户回车<br/>useChatComposer"] --> B["hostBridge.post({type:'send-prompt',<br/>prompt, workspaceMode, modelProfileId})"]
    B --> C["WebView2 WebMessageReceived"]
    C --> D{"WebViewMessageRouter<br/>Source == 应用 origin ?"}
    D -->|否| DROP["直接丢弃<br/>(iframe / 插件面板消息)"]
    D -->|是| E{"type 前缀分派"}
    E -->|"terminal-*"| T["TerminalHostController"]
    E -->|"plugin-host/*"| P["PluginPanelBridge"]
    E -->|"activity-panel/*"| AC["ActivityPanelBridge"]
    E -->|"send-prompt"| F["MainWindowViewModel.SubmitPromptAsync"]
    F --> G["PromptSubmissionSnapshot<br/>prompt / conversation / workspaceRoot /<br/>toolPermissionMode / modelProfileId / agentId /<br/>composerModeOverride / selectionVersion"]
    G --> H["SendAsync"]
    H --> I{"selectionVersion<br/>仍一致?"}
    I -->|否| I2["拒绝：当前选择已改变"]
    I -->|是| J["ResolveRuntimeAgent<br/>agent.Mode ← composer 覆盖"]
    J --> K["ConversationWorkspaceService.PrepareAsync<br/>需要时创建 Managed Worktree"]
    K --> L["ConversationTurnEngine.TryAdmitAsync"]
    L --> M{"准入成功?"}
    M -->|否| M2["提示：会话正在执行 / 应用退出中"]
    M -->|是| N["_ = ExecuteAcceptedTurnAsync(admission)<br/>立即回 prompt-submission 关联响应"]
```

准入点 `TryAdmitAsync` 的约束：`SemaphoreSlim` 单飞行、`_deletingConversations` 删除墓碑、`_shutdownCancellation` 全局停机；成功后创建/更新会话记录并绑定 `ConversationRuntimeState` 与 `ConversationSessionCoordinator`。

`ExecuteAsync` 的固定顺序：持久化用户消息 → `BuildChatTurnRequestAsync` 按模式产出 `DirectChatTurnRequest` / `CliChatTurnRequest` → 开始 Agent 活动 → `BeginTurn` → 消费事件流 → 终态提交 → `CompleteExecution`。

## 2. Direct 分支：进程内 M.E.AI 全链路

```mermaid
flowchart TB
    START["DirectChatTurnRequest<br/>{TurnId, ConversationId, WorkspaceRoot, Agent,<br/>Messages, ModelProfileId, ToolPermissionMode,<br/>ToolApprovalHandler, ExecutionContext, ToolExecutions}"]

    START --> S1["① 续写守卫<br/>Origin=Continuation 必须有<br/>IToolExecutionCheckpoint，否则抛错"]

    S1 --> S2["② AiChatClientFactory.PrepareAsync(ModelProfileId)<br/>显式 id 或 desktop-default<br/>校验 profile/connection 启用 → 解析 API Format<br/>DPAPI 解密凭据（仅 Infrastructure 内）"]
    S2 --> S3["③ DirectTurnCapabilityResolver.ResolveAsync<br/>用已确定的具体模型 id"]

    subgraph CAP["能力组装（单一 DirectToolBinding 列表）"]
        direction TB
        C1["CreateEffectiveRequest<br/>Continuation 只收缩 ceiling /<br/>Subagent 校验捕获 ceiling"]
        C2["WorkspaceAgentToolset<br/>读/写/编辑/glob/grep/shell"]
        C3["PluginCapabilitySource<br/>版本 + hash + lease"]
        C4["SkillCapabilitySource<br/>Skill token / 运行时工具"]
        C5["McpCapabilitySource<br/>连接池 + revision + 结果 64KiB 上限"]
        C6["SubagentCapabilitySource<br/>delegate/get/cancel/retry"]
        C7["BindTools<br/>名称冲突 + descriptor 对齐 + ToolPolicy 过滤"]
        C8["system instructions<br/>Plugin/Skill/Policy/Degradation"]
        C1 --> C2 --> C3 --> C4 --> C5 --> C6 --> C7 --> C8
    end

    S3 --> CAP
    CAP --> S4["④ DirectTurnHooksFactory.Create<br/>runStarting hooks<br/>→ Blocked 则直接终态"]
    S4 --> S5["⑤ DirectToolInvoker<br/>M.E.AI FunctionInvoker 唯一调用缝"]
    S5 --> S6["⑥ AiChatClientFactory.Create<br/>按回合 HttpClient (共享 pooled handler)<br/>provider adapter → CreateChatOptions/Client<br/>ChatClientBuilder: UseFunctionInvocation(128)<br/>+ UseLogging → AiChatClientLease"]
    S6 --> S7["⑦ RunStartedEvent(direct-{guid}, model)<br/>+ RunStatusEvent(Requesting)"]
    S7 --> S8["⑧ DirectPromptComposer.BuildMessages<br/>System(agent+扩展+policy)<br/>→ History 反向预算裁剪(工具调用/结果不可拆)<br/>→ HookContext → 续写提示 → CompletionBatch"]
    S8 --> S9["⑨ GetStreamingResponseAsync<br/>TurnOutputStream.TranslateUpdate"]
    S9 --> S10["⑩ 终态判定<br/>Length+文本=Truncated / Length 空=Failed<br/>ToolCalls=Failed(循环耗尽) / 其余=Succeeded"]
    S10 --> S11["⑪ runCompleted hook<br/>释放 provider lease → 释放 capability lease"]

    subgraph TOOL["工具调用一次经过的全部关卡"]
        direction TB
        T1["toolExecuting hook<br/>可改参数 / 强制审批 / 阻断"]
        T2["审批判定<br/>RequiresApproval &&!=FullAccess<br/>→ DesktopToolApprovalHandler"]
        T3["ToolExecutionCheckpoint<br/>BeforeExecutionAsync（续写屏障）"]
        T4["AIFunction.InvokeAsync<br/>Workspace / Skill / MCP / Subagent"]
        T5["toolExecuted hook + HookFeedback<br/>→ DirectToolResult(Status/Summary/Content/Detail)"]
        T1 --> T2 --> T3 --> T4 --> T5
    end
    S5 -.-> TOOL
```

要点：

- `DirectToolResult.Detail` 只用于展示，不参与 provider JSON 序列化；MCP 模型结果序列化后硬上限 **65,536 字节**。
- 审批拒绝返回 `Canceled` + 拒绝摘要 → recorder 落库为 `Cancelled`，不抛异常。
- provider pipeline **先于** capability lease 释放，避免 HTTP 流仍在消费时回收 MCP 连接。
- `PrepareAsync` 与 `ResolveAsync` 之间有顺序依赖：能力解析使用已确定的具体模型 id，禁用或无效模型不会先触发 MCP 连接。

## 3. CLI 分支：子进程 JSONL 全链路

```mermaid
flowchart TB
    CR["CliChatTurnRequest<br/>{..., CliAgent, CliModel, CliReasoningEffort}"]

    CR --> K1{"会话选中的 CLI ?"}
    K1 -->|无| K2["RunCompletedEvent(Failed)<br/>提示去设置里选择 CLI"]
    K1 -->|有| K3["CliAgentAdapterRegistry.Find(kind)<br/>ClaudeCliAgentAdapter / CodexCliAgentAdapter /<br/>OpenCodeCliAgentAdapter"]

    K3 --> K4["ExtractPrompt<br/>取最后一条 User 消息；历史靠会话恢复"]
    K4 --> K5["SqliteCliAgentSessionStore<br/>GetSessionIdAsync(conversationId × cliKind)"]
    K5 --> K6["adapter.PrepareTurn(CliTurnPreparation)<br/>→ Command / Arguments / StandardInputLines / CliStreamParser"]
    K6 --> K7["CliCommandResolver.Resolve<br/>必要时 shell 包装"]
    K7 --> K8["CliAgentProcessHost.Start<br/>WorkingDirectory = WorkspaceRoot ?? Desktop ?? UserProfile<br/>不注入任何 SelfClaw 环境变量/凭据"]
    K8 --> K9["CliAgentProcessSession<br/>写 stdin → CloseStdin(EOF)<br/>stdout 逐行 / stderr<br/>inactivity watchdog + kill-tree"]
    K9 --> K10["parser.ParseLine(line) → AgentStreamEvent"]
    K10 --> K11{"RunStartedEvent.SessionId ?"}
    K11 -->|有| K12["持久化会话 id 供下一回合 resume"]
    K10 --> K13["WaitForExitAsync → CliProcessResult(Status/ExitCode/TimedOut/StdErr)"]
    K13 --> K14{"流内已发 RunCompleted ?"}
    K14 -->|否| K15["合成终态：超时/退出码/Stderr"]
    K14 -->|是| K16["沿用 parser 终态"]
```

解析器：`ClaudeStreamJsonParser`、`CodexJsonEventStreamParser`、`OpenCodeJsonEventStreamParser`（均实现 `CliStreamParser`）。CLI 不消费 Direct 能力快照，也不接受 SelfClaw 的审批与工具策略。

## 4. 事件契约与归约（两条分支在此合流）

```mermaid
flowchart LR
    subgraph RUNTIME["Runtime 侧"]
        D["DirectAgentChatRuntime"]
        C["CliAgentChatRuntime"]
    end
    D --> EV
    C --> EV
    EV["AgentStreamEvent 基类"]

    subgraph TYPES["事件类型"]
        direction TB
        E1["RunStartedEvent"]
        E2["RunStatusEvent"]
        E3["AssistantTextDeltaEvent"]
        E4["AssistantThinkingDeltaEvent"]
        E5["ToolCallStartedEvent / ToolCallCompletedEvent"]
        E6["UsageReportedEvent"]
        E7["RunNoticeEvent"]
        E8["PermissionRequestedEvent / RawOutputEvent"]
        E9["RunCompletedEvent (唯一终态)"]
    end
    EV --> TYPES

    TYPES --> GATE["DispatchingAgentChatRuntime 协议强制<br/>终态只发一次；终态后事件丢弃<br/>5s 内未收尾则取消 adapter"]
    GATE --> REC["ConversationTurnRecorder.ApplyEventAsync<br/>文本/思考增量 → StreamingAssistantContent<br/>工具事件 → ToolExecutionRecord<br/>usage / notice / status / 终态"]
    REC --> TERM["CompleteAssistantTurnAsync<br/>MessageSegment 分块(Text/Thinking/ToolCall 有序)"]
    TERM --> FIN["DesktopTurnFinalizer.TryCommitAsync<br/>ITurnFinalizationRepository<br/>2 次尝试 / 5s 超时"]
    REC --> ACT["AgentActivityCoordinator.ApplyEvent<br/>Pet / 托盘 / Agent 活动态"]
```

终态映射：`Succeeded / Cancelled / Failed / Truncated / Blocked`。异常与用户取消分别走 `FinalizeInterruptedAsync`，保证「流结束但无终态」不会挂起 UI。

## 5. Output：快照 → 差分 → Vue 渲染

```mermaid
flowchart TB
    MUT["ConversationRuntimeState 内容变更<br/>串行化写入 + 不可变快照"]
    MUT --> SIG["TranscriptChanged(immediate)"]
    SIG --> COORD["ConversationSessionCoordinator<br/>运行态 / 取消 / 选中会话同步"]
    COORD --> PUB["TranscriptPublisher<br/>Dispatcher 封送<br/>120ms 合流窗口 + autoScroll 合并"]
    PUB --> PROJ["TranscriptProjection.Build<br/>Messages + ToolRuns + Shell → TranscriptRenderState"]
    PROJ --> DEL["TranscriptDelivery"]
    DEL --> D1{"与已 ACK 快照比较"}
    D1 -->|首帧| D2["replaceState<br/>{items, conversations, isBusy,<br/>agentMode, toolPermissionMode, revision}"]
    D1 -->|增量| D3["patchState<br/>{upsertItems, removedItemIds, itemOrder, baseRevision}"]
    D2 --> CH["WebViewHostChannel.PostPush<br/>camelCase JSON"]
    D3 --> CH
    CH --> WV["CoreWebView2.PostWebMessageAsJson"]

    WV --> HB["Vue hostBridge<br/>唯一 addEventListener('message')"]
    HB --> TB["transcriptBridge.reduceTranscriptPush<br/>baseRevision 校验失败 → transcript-rejected"]
    TB -->|成功| ACK["post transcript-applied(revision)"]
    ACK --> DEL
    TB --> UCT["useChatTranscript 响应式 state"]
    UCT --> TP["TranscriptPanel.vue"]
    TP --> MB["MessageBlocks / MessageContent<br/>BodySegment / ThinkingBlock / ToolGroup / ToolCard"]

    DEL -.->|"2s 定时重试，最多 3 次"| DEL
    DEL -.->|"耗尽 → transcript-unavailable"| HB
    HB -.->|"transcript-resync"| DEL
```

可靠性边界：`TranscriptDelivery` 只保留「1 个 in-flight + 1 个 latest pending」；ACK 驱动下一帧；`ReadyChanged`（WebView 导航/重载）触发全量重放；重试 3 次后进入等待恢复，而不是无限刷。

## 6. 旁路通道（与主 transcript 相互独立）

```mermaid
flowchart LR
    subgraph APPROVAL["工具审批"]
        A1["DirectToolInvoker 请求审批"] --> A2["DesktopToolApprovalHandler<br/>唯一 pending 决策所有 / 5 分钟超时"]
        A2 --> A3["ToolApprovalPresenter<br/>tool-approval/state 版本化推送<br/>重载可恢复"]
        A3 --> A4["useChatApprovals"]
        A4 -->|"resolve-tool-approval"| A5["TryResolve(id, approved)"]
        A2 -.->|"窗口隐藏/最小化"| A6["Windows Toast Confirm/Cancel"]
        A2 -.->|"Pet 观察同一 activity 状态"| A7["PetHost"]
    end
    subgraph TERMINAL["终端 (ConPTY)"]
        T1["useChatTerminal"] -->|"terminal-*"| T2["TerminalHostController"]
        T2 --> T3["ConPtyTerminalSession<br/>连续 UTF-8 解码 + 有界尾部批次"]
        T3 -->|"terminal-output push"| T1
    end
    subgraph ACTIVITY["子代理活动面板"]
        S1["SubagentActivityService<br/>活跃 session + SQLite 历史"] --> S2["ActivityPanelSnapshotBuilder<br/>≤50 摘要 + 1 个 64 块详情窗口 / 256KiB"]
        S2 --> S3["ActivityPanelPublisher / Delivery<br/>1 in-flight + 1 pending，2s×3 重试"]
        S3 --> S4["useActivityPanel / useActivityDetail"]
    end
    subgraph SHELL["Shell 投影"]
        H1["PublishShell(autoScroll)"] --> H2["会话列表 / 选中 / busy / 能力版本"]
    end
```

插件面板走同一 origin 守卫之后的 `plugin-host/*`，但每个面板来自独立子域 `https://<plugin-id>.plugin.selfclaw.local`，身份由 `event.origin` + `event.source` 双重匹配得到，payload 里的 `pluginId` 不可信。

## 7. 后台子代理链路（Direct 的函数调用延伸）

```mermaid
flowchart TB
    P1["父 Direct 回合调用<br/>delegate_to_subagent"] --> P2["ISubagentTaskCoordinator<br/>SubagentTaskPreflight 受理前校验"]
    P2 --> P3["subagent_tasks 持久化<br/>独立 child conversation + 冻结能力 ceiling"]
    P3 --> P4["SubagentTaskBackgroundHost → SubagentTaskExecutor"]
    P4 --> P5["DirectAgentChatRuntime<br/>Origin=Subagent<br/>detached recorder (不进父 transcript)"]
    P5 --> P6["子任务终态 → subagent_deliveries(pending)"]
    P6 --> P7["SubagentDeliveryDispatcher (BackgroundService)<br/>250ms 扫描 / 最多 4 并发 /<br/>跳过 busy 或删除中的父会话"]
    P7 --> P8["ConversationTurnEngine.TryAdmitContinuationAsync<br/>租约 45s，心跳 15s"]
    P8 --> P9["SubagentContinuationExecutor<br/>Origin=Continuation，completion batch 仅入 prompt"]
    P9 --> P10["SubagentContinuationTurnCommitter.BeforeExecutionAsync<br/>SQLite 校验 lease → 持久化整批 checkpoint"]
    P10 --> P11["实际 AIFunction 执行（checkpoint 之后）"]
    P11 --> P12["父终态 + Delivered 原子提交"]
    P12 -->|"失败且无执行证据"| P13["≤3 次重试，10/30s 退避"]
    P12 -->|"执行证据不确定"| P14["DeadLetter + 通知"]
```

关键纪律：`ToolCallStartedEvent` 入队 ≠ 已落库，因此执行屏障是 **checkpoint 的同步提交**，不是事件消费；续写分批发布，父 transcript 只在原子终态时更新一次。checkpoint 只表示「工具可能已开始」，不承诺外部文件、Shell 或 MCP 的 exactly-once。

## 8. 关键所有权与边界速查

| 资源 / 状态 | 所有者 | 边界 |
|---|---|---|
| UI 选择与导航 | `MainWindowViewModel` | 仅 UI 线程；提交时快照 selectionVersion |
| 会话准入 / 删除墓碑 | `ConversationTurnEngine` | 单飞行闸门 + tombstone + 停机取消 |
| Workspace Root / Worktree | `ConversationWorkspaceService` | 准备与释放；提交回合自带捕获值 |
| Direct 能力与 lease | `DirectTurnCapabilityResolver` → `DirectTurnCapabilityLease` | 每回合；`DirectTurnLeaseScope` 统一回收 Plugin/MCP |
| Provider pipeline | `AiChatClientFactory` → `AiChatClientLease` | 每回合 HttpClient；共享 pooled handler 长存 |
| 工具调用唯一缝 | `DirectToolInvoker`（M.E.AI `FunctionInvoker`） | hook → 审批 → checkpoint → 执行 → 反馈 |
| 事件契约与终态 | `DispatchingAgentChatRuntime` | 终态只发一次；终态后事件丢弃 |
| 内容录制与快照 | `ConversationRuntimeState` / `SubagentExecutionSession` | 串行写入；取消后等终态再释放 |
| 持久化终态 | `DesktopTurnFinalizer` | 原子提交，2 次尝试，5s 超时 |
| 输出传输 | `TranscriptPublisher` → `TranscriptDelivery` → `WebViewHostChannel` | 合流 / diff / ACK / 有界恢复；只做传输与 feature 状态 |
| 前端状态 | `transcriptBridge` + `useChatTranscript` | patch 必须匹配 baseRevision，否则请求 resync |

## 9. 验证入口

```powershell
dotnet restore SelfClaw.slnx --force-evaluate
dotnet build SelfClaw.slnx
dotnet test SelfClaw.Tests/SelfClaw.Tests.csproj

cd SelfClaw.TranscriptVue
npm test
npm run test:e2e
```

桌面/进程强杀验证需要 `SELFCLAW_DESKTOP_SMOKE=1`；真实 provider 验证需要 `SELFCLAW_PROVIDER_SMOKE=1`，并启用一个具备推理能力的模型。

## 证据文件

| 层 | 文件 |
|---|---|
| 前端入口 | `SelfClaw.TranscriptVue/src/composables/hostBridge.js`、`useChatComposer.js`、`views/ChatView.vue` |
| 消息路由 | `SelfClaw.Desktop/Services/WebView/WebViewMessageRouter.cs`、`WebViewHostChannel.cs` |
| VM 与准入 | `SelfClaw.Desktop/ViewModels/MainWindowViewModel.cs`、`Services/Workspace/ConversationWorkspaceService.cs` |
| 回合引擎 | `SelfClaw.Desktop/Services/Runtime/ConversationTurnEngine.cs`、`ConversationTurnRecorder.cs`、`DesktopTurnFinalizer.cs`、`ConversationRuntimeState.cs`、`ConversationSessionCoordinator.cs` |
| 分派 | `SelfClaw.Infrastructure/Agents/Runtime/DispatchingAgentChatRuntime.cs`、`Core/Runtime/Requests/ChatTurnRequest.cs` |
| Direct | `SelfClaw.Infrastructure/Agents/Direct/DirectAgentChatRuntime.cs`、`Capabilities/DirectTurnCapabilityResolver.cs`、`Context/DirectPromptComposer.cs`、`Tools/DirectToolInvoker.cs` |
| CLI | `SelfClaw.Infrastructure/Agents/Cli/CliAgentChatRuntime.cs`、`Adapters/`、`Parsers/`、`Process/`、`Session/` |
| Provider | `SelfClaw.Infrastructure/AiProviders/AiChatClientFactory.cs`、`AiProviders/{OpenAi,Anthropic}/` |
| 发布 | `SelfClaw.Desktop/Services/Transcript/{TranscriptPublisher,TranscriptDelivery,TranscriptProjection}.cs`、`SelfClaw.TranscriptVue/src/composables/transcriptBridge.js` |
| 子代理 | `SelfClaw.Desktop/Services/Subagents/{SubagentDeliveryDispatcher,SubagentContinuationExecutor,SubagentTaskExecutor,SubagentContinuationTurnCommitter}.cs`、`SelfClaw.Infrastructure/Agents/Subagents/Runtime/SubagentTaskPreflight.cs` |
