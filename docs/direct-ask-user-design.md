# Direct `ask_user` 提问工具

模型在需要用户决策时主动调用 `ask_user`，框架拦截该调用并渲染输入框上方的问答 dock，把结构化答案作为这次调用的结果回灌模型。**它不是审批**：`ToolPermissionMode.FullAccess`（自动允许）下同样阻塞等待。

实现于 Direct 链路，CLI 模式不参与（CLI 自带交互界面）。

## 1. 为什么不是审批

| | 工具审批 | `ask_user` |
| --- | --- | --- |
| 决策 | `Approved` / `Denied` / `Superseded` 三元枚举 | 结构化答案（按题：选中项 + 自由文本 + 是否跳过） |
| `FullAccess` | 绕过（`DirectToolInvoker` 的既有语义） | **不绕过**，必须回答 |
| 宿主状态 | `AgentActivityPhase.AwaitingApproval` | `AgentActivityPhase.AwaitingAnswer` |
| 承载 | `IToolApprovalHandler` | `IUserQuestionHandler` |

把提问塞进审批通道必然要么被"自动允许"绕过、要么在两处特判，因此是独立通道。

## 2. 链路

```
FunctionCallContent(ask_user { questions: [...] })
  → ToolCallStartedEvent(kind = ToolCallKind.Ask)        转录立刻出现 "Ask N questions"
  → DirectToolInvoker.ExecuteAsync                       RequiresApproval=false，不读 ToolPermissionMode
  → AskUserToolset                                      AskUserRules 校验 + 每回合配额
  → IUserQuestionHandler.RequestAnswerAsync              宿主挂起 TaskCompletionSource
  → AgentActivityCoordinator 快照（AwaitingAnswer）
  → UserQuestionPresenter.push "user-question/state"     前端 dock
  → "resolve-user-question"                              宿主校验并 TryAnswer
  → DirectToolResult(Completed, Detail)                  → ToolCallCompletedEvent → 模型继续
```

关键结构性决定：**`DirectToolInvoker`、`DirectConversationLoop`、`ConversationTurnRecorder`、`DirectPromptComposer` 零改动**。提问就是一个"会阻塞很久"的普通内置工具，因此审批/执行 checkpoint/连续故障预算/转录落库/历史回放全部自动复用。

## 3. 契约（Core）

`SelfClaw.Core/Interfaces/Questions/IUserQuestionHandler.cs`

```csharp
Task<UserQuestionAnswer> RequestAnswerAsync(UserQuestionRequest request, CancellationToken cancellationToken = default);
bool TryAnswer(Guid requestId, UserQuestionAnswer answer);   // 原子取走；已回答/已取消返回 false
```

`SelfClaw.Core/Runtime/Questions/`

- `UserQuestionRequest(Guid RequestId, IReadOnlyList<UserQuestionPrompt> Prompts, Guid? ConversationId, Guid? TurnId)` —— **一次 tool call = 一个批次**，不是按题拆。
- `UserQuestionPrompt(Guid PromptId, string Question, string? Header, IReadOnlyList<UserQuestionOption> Options, bool MultiSelect)` —— 一个 prompt = dock 里一个 tab。
- `UserQuestionOption(string Label, string? Description)`
- `UserQuestionPromptAnswer(Guid PromptId, IReadOnlyList<string> SelectedLabels, string? OtherText, bool Skipped)`
- `UserQuestionAnswer(Guid RequestId, IReadOnlyList<UserQuestionPromptAnswer> Answers, bool Skipped)`
- `AskUserRules` —— 唯一规则源，见 §4。

## 4. `AskUserRules`：限额与校验的唯一来源

Core 不能引用 Infrastructure，所以规则集拥有自己的输入形状 `AskUserPromptSpec` / `AskUserOptionSpec`；`AskUserToolset` 只做一次机械映射（已 trim 的 `UserQuestionPrompt` → spec），**任何限制都不在 toolset 里重复**。

| 常量 | 值 | 理由 |
| --- | --- | --- |
| `MaximumPromptsPerCall` | 4 | dock 的 tabs 上限；再多用户会放弃阅读 |
| `MinimumOptionsPerPrompt` / `MaximumOptionsPerPrompt` | 2 / 4 | 少于 2 不构成选择；多于 4 与"其它"挤在一起 |
| `MaximumQuestionsPerTurn` | 16 | 按**累计问题数**（非调用次数）计，防"问卷机" |
| `MaximumQuestionLength` | 600 | |
| `MaximumHeaderLength` | 24 | tab 标签 |
| `MaximumOptionLabelLength` | 60 | |
| `MaximumOptionDescriptionLength` | 200 | |
| `MaximumOtherTextLength` | 2000 | 用户自由文本 |
| `MaximumArgumentsCharacters` | 4 KiB | 参数会随每次后续请求回放，是 prompt 预算的硬闸门 |

`Validate` 拒绝：空数组、超 4 题、每题选项不在 2–4、同题 label 重复（忽略大小写）、字段超长、参数序列化超 4 KiB，以及**模型自己写 `Other`/`其它`/`其他` 选项**（dock 总会追加一个，重复会让标签集歧义）。

## 5. 绑定规则

`DirectTurnPolicy.AllowsUserQuestion => Origin is DirectTurnOrigin.Interactive`

| 回合来源 | 绑定 `ask_user` | 原因 |
| --- | --- | --- |
| Interactive（含队列/steer 跟进回合） | 是 | 用户在看着 |
| Subagent / Continuation | 否 | 后台无界面，绑上即死锁 |
| Automation | 否 | 无头运行，`FullAccess`，绑上即永久挂起 |

`DirectCapabilityRules.Allows` 把 `ToolCallKind.Ask` 放进 `ReadOnlyToolPolicy` 白名单：提问不改工作区，只读代理也该能问。

`DirectChatTurnRequest` 末尾的可选 `IUserQuestionHandler? UserQuestionHandler = null`；只有 `ConversationTurnEngine` 传真实 handler。

## 6. 结果映射

| 情形 | Status | Summary | Detail（落库并回放） |
| --- | --- | --- | --- |
| 已答（含部分作答） | `Completed` | `Answered 1 of 2` | `User answered:` + 逐题 `问题 → 答案`，未答标 `(not answered)` |
| 整批跳过 | `Completed` | `Question skipped` | `User skipped the questions. Continue with your best judgment and state the assumption you made.` |
| 参数非法 | `Failed` | `Invalid question: …` | 具体原因，模型可修正重发 |
| 超配额 | `Failed` | `Question limit reached` | 说明本回合不再提问 |

**跳过必须是 `Completed`**：`DirectPromptComposer.CreateToolResultMessage` 会把 `Canceled`/`Failed` 回放成 `Exception`，模型会读成"工具坏了"而不是"用户决定不答"。超配额与非法参数同理不抛异常，不消耗 `DirectToolInvoker.MaximumConsecutiveToolFaults`。

**两条路径必须信息一致**：`DirectToolResult.Detail` 带 `[JsonIgnore]`，活模型读的是 `Content`（`{ skipped, answers: [{ question, selected[], other }] }`）；而落库/回放的 `ResultContent` 存的是 `Detail`（`DirectEventTranslator.DescribeToolResult`）。两者由同一个循环产出，不能各说一套。

## 7. WebView 协议

推送与 `get-state` 响应同形：

```jsonc
{ "type": "user-question/state", "requestId": "<transport id，可省>", "revision": 7,
  "conversationTitle": "…",
  "question": { "requestId": "<批次 id>", "conversationId": "…",
                "prompts": [{ "promptId": "…", "header": "…", "question": "…", "multiSelect": false,
                              "options": [{ "label": "…", "description": "…" }] }] } | null }
```

前端回传：

```jsonc
{ "type": "resolve-user-question", "requestId": "<transport id>",
  "questionRequestId": "<批次 id>", "skipped": true }

{ "type": "resolve-user-question", "requestId": "<transport id>",
  "questionRequestId": "<批次 id>",
  "answers": [{ "promptId": "…", "selected": ["label"], "other": "自由文本" }] }
```

**两个 id 必须分开**：`requestId` 是 `hostBridge.js` 生成的传输关联键（宿主原样回显成 `operation-result`），`questionRequestId` 是提问批次 id。混用会让状态推送与回包抢同一个槽位。

命令：`user-question/get-state`（`Capture` 自己带 `requestId` 回包）、`resolve-user-question`（走通用 `operation-result`，与 `resolve-tool-approval` 一致）。

宿主是唯一权威，`ResolveUserQuestion` 拒绝：`questionRequestId` 缺失或已结清、`skipped` 与 `answers` 同给、`promptId` 不属于该批次、`promptId` 重复、`selected` 含该题未提供的 label、`other` 超长、某题既无选中也无文本（须显式跳过）。**没收到的题按跳过补齐**，这样模型仍能看到它问过的每一道题。

## 8. 前端

- `useChatQuestions(bridge)` —— `revision` 单调、丢弃旧包、`on('user-question/state')`、`onMounted` 拉 `get-state`；暴露 `pending` / `resolving` / `answer` / `skip`。
- `useAskUserAnswers(questionRef)` —— 草稿模型：单选换选覆盖且与"其它"互斥；多选取并集且"其它"可共存；"其它"由 UI 追加为最后一题选项；批次 id 变化即清空草稿。
  - **多选答案按选项下标排序，不按点击顺序**：点 D、C、A 应答复 `A, C, D`。草稿按点击顺序累积是自然的（增删最省事），但产出载荷时必须折回选项顺序——否则模型读到的顺序和界面读到的顺序不一致，回放出来的转录也会显示成一个用户没做过的顺序。排序只在 `toAnswer` 一处做，取消再选回中间项也不会把该项挤到队尾。
- `AskUserDock.vue`（`components/chat/question/`）—— 渲染在 composer 上方（`ComposerPanel` 的 `#question` 插槽，位于 `#plan` 之前）。**单题不渲染 tab 栏**；多题用 `role="tablist"`，未答带圆点、已答打勾。提交按钮的条件是"至少一题有答案"（允许部分作答）。
- 待答期间 `ComposerPanel`：`canSend` 加 `!questionPending`、`submit()` 首行早退、placeholder 改为"请先回答上方的问题"；**停止按钮保持可用**（唯一逃生口）。

## 9. 转录

沿用通用工具卡，**不新增 `MessageSegmentKind`、不改 projection、不改 Vue renderer**：

- `TranscriptToolRunPresenter.BuildInlineToolSummary`：`ask_user` → 1 题显示 `Ask <问题>`，多题显示 `Ask N questions`。
- `BuildToolDetailText` 的 `ask_user` 专用分支**必须在 `if (ResultContent != null) return …` 之前**，否则完成态只会显示裸内容。待答态列出问题与 `1) label — description`；完成态直接展示 `ResultContent`（本身就是问答对照文本）。

## 10. 取消与关停

- 停止生成 → run token 取消 → handler 移除待答 → 工具抛 `OperationCanceledException` → `DirectToolInvoker` 不吞 → 回合 `Cancelled`，`ConversationTurnFinalizationBuilder` 把 `Running` 的 `ask_user` 记录改为 `Cancelled`。
- `App.StopServicesAsync` 与 `_emergencyCleanup` 都调 `DesktopUserQuestionHandler.DismissAll()`（待答全部按 Skipped 结清），与审批的 `RejectAll()` 并列。
- 订阅者（presenter）抛异常 → 按 Skipped 结清，不能让回合永久挂起。
- **无超时**：与审批一致，只能由用户停止或关停结束。不跨重启恢复。

## 11. 已知风险

- **历史回放与本回合绑定无关**（既有性质，非 `ask_user` 引入）：`DirectPromptComposer.BuildRequest` / `BuildAssistantUnit` 的签名里没有任何 binding 参数——它只遍历 `MessageSegmentKind.ToolCall` 与 `ToolExecutionRecord`，因此会回放**每一次**已完成/失败/取消的工具调用，不检查该工具本轮是否提供给了模型。`tools` 列表则由 `DirectTurnCapabilityResolver.BindTools` 独立按 origin 与 ToolPolicy 过滤。两件事各算各的，于是 assistant 消息里可以出现"本轮没提供的工具"的 tool_call，其后跟着对应的 result。

  `ask_user` 只是**新增了一条触发路径**：`SubagentContinuationExecutor.CreateRequest` 用 `PrepareRuntimeStateAsync(parentConversation)` 加载**父会话**历史并以 `Continuation` 为 origin，而 `AllowsUserQuestion => Origin is Interactive` 使该回合不绑定 `ask_user`；父会话里若有已完成的提问，就会被回放成未绑定的调用。既有的触发路径其实更常见：切换 agent 使 `ToolPolicy` 收窄（System → ReadOnly 会让 `write_file` / `edit_file` / `run_shell_command` 从 `tools` 消失）、禁用 MCP server、卸载 Skill、清空工作区。

  **实测无碍**：主流 provider 校验的是"assistant 的 tool_call 与紧随其后的 tool_result 一一配对"，不校验"tool_call 的名字必须出现在本次 `tools` 里"；否则上述既有功能早就会报错。

  **危险在于"顺手修好"**：若某天遇到 strict provider 拒绝这种载荷，最直觉的改法是把 `ask_user` 也绑到后台回合——那是致命错误。目前 `SubagentContinuationExecutor` / `SubagentTaskExecutor` / `AutomationRunExecutor` 都**不传** `UserQuestionHandler`（传了也绑不上，因为绑定条件还要求 handler 非 null），这层意外保护很容易被"让逻辑一致"顺手拆掉。真绑上之后：后台回合没有界面、dock 不出现、用户不知道有问题在等，`RequestAnswerAsync` 挂起的 `TaskCompletionSource` 永不完成 → 回合永久阻塞 → 该会话的 run handle 一直被占用 → 用户连发新消息都会被挡住。

  **正确修法**：在 `DirectPromptComposer` 里对未绑定的工具做成对省略——call 与其 result 必须一起跳过。拆开任一侧都是非法载荷（只有 call 没有 result，或只有 result 没有 call）。现有代码已在同一 `switch` 分支里同时压入两者，扩展点就在那里。
- **提示词膨胀**：工具描述常驻每次请求（约 200–250 tokens）。
- **挂起**：无超时 + 后台窗口 → 回合可能长时间空转；靠"停止生成"与活动面板/宠物的"等待你的回答"缓解。
- **两套待决队列**（审批/提问）的展示优先级固定为"提问优先"（问题已把回合彻底卡住）。

## 12. 关键位置

| 关注点 | 位置 |
| --- | --- |
| 规则唯一源 | `SelfClaw.Core/Runtime/Questions/AskUserRules.cs` |
| 阻塞调用与配额 | `SelfClaw.Infrastructure/Agents/Direct/Tools/AskUserToolset.cs` |
| 绑定闸门 | `DirectTurnCapabilityResolver.cs` + `DirectTurnPolicy.AllowsUserQuestion` |
| 宿主待答队列 | `SelfClaw.Desktop/Services/Questions/DesktopUserQuestionHandler.cs` |
| 协议校验 | `WebViewMessageRouter.ResolveUserQuestion` |
| 快照优先级 | `AgentActivityCoordinator.BuildSnapshotLocked` |
| 转录文案 | `TranscriptToolRunPresenter.BuildAskUserDetails` |
