# Direct 消息队列与 Steer：P0 基线与技术验证报告

状态：P0 G01–G04 补证通过；复审历史见 §9，补证及缺口修复见 §10。Queue / Steer 均未开放。日期：2026-10-02。配套实施文档：[direct-message-input-implementation.md](direct-message-input-implementation.md)；配套设计：[direct-message-input-design.md](direct-message-input-design.md)。

> **开关状态更新（2026-10-08）：** 本文所记 Queue／Steer 功能开关已在 `8ad27da` 删除、两者始终开启；下文保持当时原文。权威记录见 [分阶段实施与验收](direct-message-input-implementation.md) 文末「2026-10-08 功能开关移除」。

本报告是 P0 的验收证据，不含 P1 及以后的实现。结论只对本文记录的命令、提交和工具版本成立。2026-10-02 复审重新运行了新增的 13 项测试并核对 TRX、resolved packages 与源码；通过数量成立，但测试通过不代表每项原定验收范围都已覆盖。

## 1. 基线快照（P0.1）

| 项 | 值 |
| --- | --- |
| 基线提交 | `0b8c509c37ac1831b51643e19fd8429af84e522a` |
| 工作树 | 仅新增 `docs/direct-message-input-design.md`、`docs/direct-message-input-implementation.md` 与本次 P0 测试文件；无既有源码改动 |
| 操作系统 | Windows |
| .NET SDK | 10.0.400；运行时 Microsoft.NETCore.App 10.0.11 |
| 目标框架 | Infrastructure `net10.0-windows`；Tests `net10.0-windows10.0.19041.0` |
| Microsoft.Extensions.AI | 10.10.0 |
| Microsoft.Extensions.AI.Abstractions | 10.10.0（Directory.Packages.props 声明 10.4.0，实际解析 10.10.0） |
| Microsoft.Extensions.AI.OpenAI | 10.10.0 |
| OpenAI SDK | 2.13.0（`OpenAI`） |
| Anthropic SDK | 12.49.0 |
| OllamaSharp | 5.4.30 |
| ModelContextProtocol.Core | 2.2.0 |
| SQLite schema | 29（`SqliteDatabase.CurrentSchemaVersion = 29`） |
| Node / npm | v22.19.0 / 10.2.5 |
| 包缓存 | `E:\.nuget\packages`（`DOTNET_CLI_HOME=<repo>/.dotnet`） |

### 基线回归（新增测试之前）

| 命令 | 结果 |
| --- | --- |
| `dotnet build SelfClaw.slnx` | 成功，0 警告，0 错误（20.7s） |
| `dotnet test ... --filter DirectAgentChatRuntimeTests\|DirectToolInvokerTests\|DirectToolPipelineTests\|AiChatClientFactoryTests\|OpenAiProviderAdapterTests\|AnthropicProviderAdapterTests\|OllamaProviderAdapterTests\|ConversationTurnRecorderTests\|ConversationTurnEngineTests\|SqliteUsageStatisticsReaderTests\|SqliteTurnFinalizationRepositoryTests` | 通过 114，失败 0，跳过 0 |
| `dotnet test SelfClaw.Tests/SelfClaw.Tests.csproj`（全量） | 通过 1233，失败 0，跳过 4（均为 smoke/进程恢复门控用例：`ActivityProcessRecoveryTests` 2 项、`ActivityProviderSmokeTests` 1 项、`DesktopActivityWebViewTests` 1 项） |
| `npm test`（SelfClaw.TranscriptVue） | 22 个文件 / 89 个用例全部通过 |
| `npm run build` | 成功（6.22s） |

证据位置：`.scratch/direct-message-input/test-results/baseline-direct.trx`、`baseline-full.trx`。

### P0 新增验证后的结果

| 命令 | 结果 |
| --- | --- |
| `dotnet test ... --filter ProtocolBaselineProbeTests` | 通过 4 |
| `dotnet test ... --filter DirectLoopBaselineTests` | 通过 5 |
| `dotnet test ... --filter InputBoundaryBarrierBaselineTests` | 通过 4 |
| `dotnet test SelfClaw.Tests/SelfClaw.Tests.csproj`（全量） | 通过 1246，失败 0，跳过 4 |

新增测试文件：

- `SelfClaw.Tests/Infrastructure/AiProviders/ProtocolBaselineProbeTests.cs`（B02、B04、B05）
- `SelfClaw.Tests/Infrastructure/Agents/Direct/DirectLoopBaselineTests.cs`（B03、B05）
- `SelfClaw.Tests/Infrastructure/Agents/Direct/InputBoundaryBarrierBaselineTests.cs`（B06，P2 落地生产实现后删除）
- 复用既有 `SelfClaw.Tests/Infrastructure/Agents/Direct/DirectToolPipelineTests.cs`（工具故障预算）

## 2. 验收证据

### B01 纯文本、thinking、两次工具往返

既有 `DirectAgentChatRuntimeTests.StreamTurnAsync_translates_all_content_and_aggregates_usage` 证明事件翻译与用量归约，基线 114 项用例全通过。不过该用例在一次 scripted 枚举中直接提供 call/result 和两条 usage，并非两次实际工具往返；本节标题要求的“连续两轮工具→再请求”仍需独立补证。已有证据范围如下：

- 事件序列（无工具文本回合）：`RunStartedEvent` → `RunStatusEvent(Requesting)` → `AssistantThinkingDeltaEvent`/`AssistantTextDeltaEvent` → `UsageReportedEvent`（单条）→ `RunCompletedEvent`（唯一终态）。
- provider 调用数与用量：`DirectEventTranslator` 对每个 `UsageContent` 记 `ProviderCalls: 1`，`TurnUsageAccumulator` 累加 token、取最后一次 context（`TurnUsageAccumulator.Observe` / `Build`）。

### B02 一响应多工具、arguments 分多片到达

探针：`ProtocolBaselineProbeTests.OpenAi_chat_completions_fragmented_tool_arguments_probe`。

固定事实（原始输出见附录 A）：

- 本 fixture 的 `arguments` 分片经 M.E.AI/OpenAI 适配层处理后，在响应枚举尾部产出完整 `FunctionCallContent`；探针证明最终参数完整，不能据此推断适配器必然在“JSON 首次可解析”时立刻产出调用。
- 该完整 `FunctionCallContent` 的到达位置可能**晚于** `finish_reason` 分片（本 fixture 中出现在 `finish_reason=tool_calls` 之后）。
- 一个响应的两个调用 `call_a`/`call_b` 各产出一个 `FunctionCallContent`，`callId` 无重复。

结论：显式循环必须**完整枚举本次响应并合并**（`ChatResponseUpdate.ToChatResponse()` 语义）后再决定工具调用与终态；可以增量聚合，无需长期保留每一个原始 delta。四个协议 fixture 均观测到完整调用，但仅 Chat Completions / Anthropic / Responses 人工构造了参数分片；Ollama fixture 的参数一次给全，不能据此声称验证了 Ollama 分片行为。调用晚于 finish reason 的先后关系目前来自输出摘录，应补为断言。

### B03 128 次迭代边界及持续故障工具

探针：`DirectLoopBaselineTests`。

M.E.AI `FunctionInvokingChatClient` 默认值（XML 文档 + 运行时读取，已断言）：

| 属性 | 默认 | Direct 工厂配置 |
| --- | --- | --- |
| `MaximumIterationsPerRequest` | 40 | 128 |
| `AllowConcurrentInvocation` | `false` | 未改（串行） |
| `MaximumConsecutiveErrorsPerRequest` | 3 | 0 |

实测基线与 P1 目标对照（P1 尚未实现，目标列不是测试结果）：

| 观测 | 替换前（SDK 循环） | 替换后（P1 显式循环，目标） |
| --- | --- | --- |
| 一直是工具调用的响应 | 129 次 provider 请求、128 次真实工具执行后终止（每响应只有一个调用） | 明确改为硬上限 MaximumProviderRequestsPerTurn=128，含首次请求；这是比实测基线少一次请求的有意变更 |
| 终止终态 | `Failed`，消息含 "iteration limit" | 保持 `Failed`，剩余输入 Held |
| 一次响应多调用 | 串行执行（`AllowConcurrentInvocation=false`） | 设计 §7.1 要求串行；行为不变 |
| 持续故障工具 | 3 次执行、3 次请求后终止（`MaximumConsecutiveToolFaults=2`，第 3 次抛出） | 故障预算与调用预算独立，行为不变 |

既有用例 `DirectToolPipelineTests.A_faulting_tool_reaches_the_model_as_a_failed_result_and_the_turn_still_ends` 已断言连续故障 3 次后终止；`A_tool_that_fails_without_throwing_never_spends_the_fault_budget` 断言非抛出失败不消耗故障预算。

SDK 10.10.0 XML 文档写着迭代“包含初始请求”，但运行探针得到 129 次底层请求；两者必须分别记录，不能用该文档句子解释成“配置值等于实测请求数”。128 次工具执行仅针对一响应一工具的 fixture，不是所有请求允许的工具总数。

**必要设计修订**：Ollama fixture 明确包含 tool call 和 `done_reason=stop`，adapter 输出也保留 stop。因此显式循环要根据合并后的完整客户端调用决定工具分支，不能仅凭 finish reason。当前 `WriteTerminalOutcome` 只据 ToolCalls 识别循环耗尽，存在 stop + 残留调用被误报成功的风险；当前探针没有把连续 Ollama-style stop 响应跑过 SDK 耗尽路径，尚不能声称所有 Ollama 模型已复现这个缺陷。P1 应增加对应端到端离线回归。

### B04 Anthropic 签名内容及 OpenAI 两种 API 格式

探针：`ProtocolBaselineProbeTests`（Anthropic / OpenAI Chat Completions / OpenAI Responses）。

关键事实：

- **Anthropic**：thinking 文本与签名在流中各占一条 update，签名落在 `TextReasoningContent.ProtectedData`；把 update 合并成 `ChatResponse` 后，单个 `TextReasoningContent` 同时具备 `Text` 与 `ProtectedData`（附录 A `anthropic-messages.txt` 的 COALESCED 段，`protected=sig-abc`）。
- 用显示文本重建 thinking 会丢失签名。显式循环必须保留原生 `ChatResponse`/`AIContent`（含 `ProtectedData`、`ResponseId`、`MessageId`）用于下一请求，不得经显示 DTO 往返。本探针只验证响应解析与合并；handler 未捕获后续请求，`sig-abc` 是合成签名，因此尚未证明工具结果追加后的下一请求序列化正确，更未证明真实服务验签通过。
- **OpenAI Chat Completions**：`reasoning_content` → `TextReasoningContent.Text`，`ProtectedData=null`；`ResponseId`/`MessageId` 均为 `chatcmpl-*`。
- **OpenAI Responses**：`MessageId=null`，`ResponseId=resp_*`；`reasoning.summary` → `TextReasoningContent.Text`；工具参数同样聚合为单个完整 `FunctionCallContent`。
- **Ollama native**：`FunctionCallContent.CallId` 由 OllamaSharp 生成（GUID），非 provider 提供；`TextReasoningContent` 来自 `message.thinking`。

B04 仍需补齐：把本次原生 assistant 消息与匹配 FunctionResultContent 送入第二次请求，捕获 HTTP body，断言 Anthropic 的 thinking text/signature/tool_use/tool_result，以及 OpenAI 两种 API 的 callId／结果／必要 reasoning 字段没有丢失或重复。该项可完全离线完成，不要求提前运行 P6 真实 provider smoke。

### B05 多个 usage 碎片和没有 usage 的响应

探针：`DirectLoopBaselineTests.Multiple_usage_fragments_in_one_response_are_normalized_as_one_provider_call_per_request` 与 `A_response_without_usage_reports_no_usage_event`。

现状（替换前）固定行为：

- 一个响应携带两个 `UsageContent` 时，`DirectEventTranslator.ObserveUsage` 被调用两次，`TurnUsageAccumulator` 把 `ProviderCalls` 累计为 **2**，尽管 provider 只被调用 1 次。这正是 L08 必须修正的"碎片数≠调用数"。
- 无 usage 的响应：`BuildUsage()` 返回 `null`，不产出 `UsageReportedEvent`，回合用量为 null —— **不伪造零用量**。
- Anthropic 的 cache token：`CachedInputTokenCount` 可为 0，`cache_creation_input_tokens` 仅在非零时才经 `AdditionalCounts` 暴露；`DirectEventTranslator.ReadCacheWriteTokens` 已兼容多种拼写。
- 适配器返回的 `UsageDetails.AdditionalCounts` 可能包含零值诊断计数（如 `OutputTokenDetails.AudioTokenCount`），normalization 需保持原样透传、不得据此推断调用数。

### B06 producer 等 ACK，consumer 正常／异常／被取消

探针：`InputBoundaryBarrierBaselineTests`（P2 落地生产实现后删除该测试与内嵌 harness）。

现有四项测试覆盖的路径（不等于全部并发顺序已被确定性验证）：

| 场景 | 观测 |
| --- | --- |
| 正常 | 断言 commit 记录在 acked 记录之前；consumer 忽略 TextDelta，尚未断言前序正文归约完成；observed List 跨任务写入未同步 |
| consumer 提交异常 | 抛同类型／消息的异常，5s 内退出；注册早于失败或晚于失败由实际调度决定，没有分别强制两种顺序 |
| 取消 waiter | `OperationCanceledException` 释放 producer；consumer 取消后退出；无遗留 waiter |
| 重确认同一 boundaryId | 对一次已提交边界重复等待返回同结果（提交计数 1）；未测试重复边界事件、SQLite 提交重试或 ACK 传递异常 |

**必要设计修订**：若 consumer 先失败、producer 后注册 waiter，只清理已有 waiter 会产生永久等待风险。harness 已用 `_failure` 保存失败状态；测试尚需用同步点分别固定早注册、晚注册、consumer 先取消、consumer 先关闭等顺序。生产 `DirectTurnInputSession` 必须记住失败／取消／关闭状态，使迟到 waiter 立即结束；也必须拒绝第二个不同 boundary 的同时等待。当前 harness 的字典只限制“同 ID 一个 waiter”，并不证明全回合只有一个 waiter，不可直接当作生产实现复用。

## 3. P0.4 `TurnId == MessageId` 消费者审计

当前 Direct、Subagent child、continuation 三类回合都把 assistant 消息 id 直接取为逻辑 TurnId；CLI 也使用同一 recorder。已定位的主要耦合点如下（行号对应基线提交，不能替代 P2 改造时的全量调用点核查）。

| 归属 | 位置 | 现有假设 | P2 目标 |
| --- | --- | --- | --- |
| recorder | `ConversationTurnRecorder.EnsureAssistantMessage`（L196） | `new MessageRecord(turn.TurnId, ...)`：assistant id = TurnId | 回合与消息 id 独立；仅在首个正文/thinking/工具/notice 时创建 assistant |
| recorder | `ApplyAssistantDelta/Thinking/Notice(turn.TurnId, …)`（L90/L99/L131） | 用 TurnId 定位 assistant | 改用 `AgentTurnState.CurrentAssistantMessageId` |
| recorder | `CompleteAssistantStream(turn.TurnId)`、`Messages.First(item => item.Id == turn.TurnId)`（L167/L168/L290/L291） | 同上 | 同上；片段封存后清空当前 id |
| recorder | `StartToolRunAsync` `MessageId: turn.TurnId`（L231） | 工具锚点 = TurnId | 工具锚点 = 当前 assistant 片段 id |
| recorder | `ReloadCommittedTurnAsync`（L378/L385） | 按 `message.Id == TurnId`、`tool.MessageId == TurnId` 重读 | 按 turn_id + 片段查询 |
| finalizer | `DesktopTurnFinalizer` 日志（L45/L53） | 用 `AssistantMessage.Id` 当 TurnId 打日志 | 日志改用真实 turn_id |
| usage SQL | `SqliteTurnFinalizationWriter.UpsertTurnUsageAsync` | `turn_usage.message_id` PK | `turn_usage.turn_id` PK |
| usage SQL | `SqliteUsageStatisticsReader.GetSubagentUsageAsync` | `t.child_turn_id = u.message_id` | `t.child_turn_id = u.turn_id` |
| usage 读取 | `SqliteConversationRepository.ListMessagesAsync` L108 | `LEFT JOIN turn_usage u ON u.message_id = m.id` | 回合用量不随消息读取 |
| 排序 | `SqliteConversationRepository.ListMessagesAsync` L110 | `ORDER BY m.created_at_utc` | `ORDER BY sequence`（新增 `messages.sequence`） |
| child | `SubagentExecutionSession` L41 | `ProviderMessages = Messages.Where(m => m.Id != task.ChildTurnId)`：用 TurnId 排除自己的 assistant 占位 | 随空占位删除该过滤；准备期历史按真实回合／片段归属定义 |
| child | `SubagentExecutionSession` L91/L103/L272/L274/L276 | 快照/工具/`MessageCreated` 全部按 `Id == ChildTurnId` | 按片段 id；回合状态另存 |
| child 校验 | `SqliteSubagentTaskRepository.ValidateFinalizationOwnership` L665 | 强制 `AssistantMessage.Id == ChildTurnId` 且工具同会话 | 改为校验 turn 归属与片段归属 |
| child 用量 | `SqliteSubagentTaskRepository` L258/L294/L618/L619 | `completion.TurnFinalization.AssistantMessage.Usage` 写入 task 用量与 delivery envelope | 从 turn usage 读取 |
| continuation | `SubagentDeliveryRepository.ValidateResolution` L857/L862 | 强制 `AssistantMessage.Id == ContinuationTurnId`、`tool.MessageId == ContinuationTurnId` | 改为校验 turn 归属 |
| continuation | `SubagentContinuationExecutor` L107；`SubagentDeliveryDispatcher` L195/L244 | `AgentTurnState(lease.ContinuationTurnId, …)` | 逻辑 TurnId 保留，assistant 片段独立 |
| 内容归约 | `AgentTurnState.MessageCreated`、`ConversationRuntimeState._messageStreams` | 前者约束每回合一条消息；后者已经是按 messageId 管理的字典 | 改造当前片段选择，复用现有按消息归约字典，不重造内容流系统 |
| 消息上的回合状态 | `MessageRecord.Usage/DurationMs/ErrorMessage`；`MessageStatus.{Failed,Cancelled,Truncated,Blocked}` | 回合状态/用量/错误挂在 assistant 消息 | 迁入 `ConversationTurnRecord` + `turn_usage.turn_id` |
| 通知 | `ConversationCompletionNotifier` L40–L91 | 按 assistant `MessageStatus` 与 `ErrorMessage` 判定终态 | 按 turn record 判定 |
| transcript 投影 | `TranscriptMessageProjector` L65 | `toolRun.MessageId == message.Id`（本身正确） | 保持：工具仍锚定片段 |
| 活动面板 | `AgentActivityCoordinator`（`_turns[context.TurnId]`）、`AgentActivitySnapshot.TurnId`、`ActivityPanelProjection` L174、`ActivityPanelWireTask.ParentTurnId`、`SqliteSubagentActivityQueries` L34/L82/L109 | 以 TurnId 为活动/父回合键 | 逻辑 TurnId 保留（与消息无等值约定），仅需去除与消息 id 的隐含绑定 |
| hooks | `DirectHookTurnContext.TurnId` 及所有 payload `TurnId` | TurnId 语义正确 | 保持 TurnId，不改为消息 id |

审计结论：不存在"只有一处改 id 即可"的局部修改。`TurnId == MessageId` 是一个跨 recorder、finalizer、SQL、child、continuation、活动面板、通知的隐含约定；P2 必须一次性引入 `conversation_turns` 与独立 message id，并同步替换上述全部读写，不能保留"首消息承载回合"的特殊规则（对应 A02 / I13）。

## 4. P0.6 运行准入职责表（A01 目标）

现状两套并发控制：

| 现 owner | 现职责 | 证据 |
| --- | --- | --- |
| `ConversationTurnEngine` | `_turnAdmissionGate`（SemaphoreSlim 1,1）、`_deletingConversations`、`_pendingInteractiveAdmissions`、`_stopping`、`_shutdownCancellation`；`TryAdmitAsync`/`TryAdmitContinuationAsync`/`CompleteContinuationAsync` | `ConversationTurnEngine.cs` L30–L150 |
| `ConversationSessionCoordinator` | `_startTurnGate`（第二把启动锁）、`_runtimeStates` 活动字典、`IsRunning`/`RunningConversationIds`、`StartTurnAsync`/`StartDetachedTurnAsync`/`CompleteTurn`/`AbandonTurn`/`StopAndRemoveAsync` | `ConversationSessionCoordinator.cs` L22–L200 |
| `ConversationRuntimeState` | 运行标志、取消源、完成 TCS、transcript 快照 | `ConversationRuntimeState.cs` |
| `SubagentTaskCoordinator` | child 专用生命周期（保留） | `SubagentTaskCoordinator.cs` |
| `SubagentDeliveryDispatcher` | continuation 准入的调用方（使用 Engine 的 `GetUnavailableContinuationParents`/`TryAdmitContinuationAsync`） | `SubagentDeliveryDispatcher.cs` L195/L244 |

问题：`TryAdmitAsync` 在 Engine gate 内调用 `_conversationSessions.StartTurnAsync`，后者又取 `_startTurnGate`，准入检查与运行注册分散在两个 owner。`IsRunning` 方法本身是只读查询，真正的可写源是 `_runtimeStates` 内的 ConversationRuntimeState.IsRunning；不能把查询方法计作另一张状态表。`_pendingInteractiveAdmissions` 是跨会话瞬时计数，被 continuation 准入当作用户优先级依据。统一 owner 的设计仍然成立，理由是减少分散裁决与生命周期所有权，而不是现有代码已经持有两份活动字典。

目标职责表（P2 交付）：

| 目标 owner | 唯一职责 | 从谁接管 | 删除项 |
| --- | --- | --- | --- |
| `ConversationRunCoordinator` | 每会话唯一运行注册、准入/关闭、取消与完成句柄、删除 tombstone、continuation 准入、用户优先级判定 | Engine + SessionCoordinator | Engine `_turnAdmissionGate`/`_deletingConversations`/`_pendingInteractiveAdmissions`/`_stopping`/`_shutdownCancellation`；SessionCoordinator `_startTurnGate`/`_runtimeStates` 可写字典 |
| `ConversationSessionCoordinator` | 仅选择、历史加载合并、选中快照展示 | — | 启动锁、活动运行字典、`IsRunning` 写入 |
| `ConversationTurnEngine` | 只执行已准入 handle、归约事件、调用提交策略 | — | 全部准入判断 |
| `ConversationInputDispatcher` | 发现候选、请求准入、执行、据真实结果唤醒下一项 | 新 | VM 空闲直连执行旁路 |
| child `SubagentTaskCoordinator` | child 生命周期（保留原 owner） | — | 不再登记 Interactive 调度状态 |
| continuation `SubagentDeliveryDispatcher` | 使用 coordinator 准入契约（保留调用方） | — | Engine 内对应规则实现 |

注意：SessionCoordinator 的"选择锁/加载合并"与 Application-origin guard、transcript ACK、Subagent 生命周期一样属于**保留**机制，不得当作"重复"删除（对应 A01 的说明）。

## 5. P0.7 事务边界与写入方审计

当前事务边界（`BeginTransaction` 调用点）：

| 写入用例 | 位置 | 事务内容 | 现状问题 |
| --- | --- | --- | --- |
| 交互回合终结 | `SqliteConversationRepository.TryFinalizeTurnAsync` L368 | 开连接+事务 → `SqliteTurnFinalizationWriter.TryWriteAsync`（message + segments + tool_runs + `turn_usage`） | 只有"终结"一个事务；没有开始/边界事务；`turn_usage` 依附 message |
| 用户消息写入 | `SqliteConversationRepository.UpsertMessageAsync` L140 | 无事务；`transaction: null` 直接写 message + attachments + segments | `AddUserMessageAsync` 独立写入，终结时不再写 user；P2 用"开始回合事务"取代 |
| child 完成 | `SqliteSubagentTaskRepository.TryCompleteAsync` L211 / `CompleteWithinTransaction` L282 | 事务内 `SqliteTurnFinalizationWriter` + task 终态 + delivery envelope | 已复用 writer；但用量取自 `AssistantMessage.Usage`，校验 `AssistantMessage.Id == ChildTurnId` |
| continuation 交付 | `SqliteSubagentDeliveryRepository.TryResolveAsync` L283 | 事务内 `SqliteTurnFinalizationWriter` + delivery 状态转换 | 同上，校验 `AssistantMessage.Id == ContinuationTurnId` |
| mailbox 领取/续租/心跳 | `SqliteSubagentDeliveryRepository` L107/L187/L254/L293/L357 | 各自独立事务 | 与用户输入队列语义不同，**不得合并**（设计 §4） |
| CLI 会话 | `SqliteCliAgentSessionStore` L97 | 独立事务 | 保留；CLI 采用新 turn/message 模型但不产生 boundary |
| Activity 读取 | `SqliteSubagentActivityReader` L32/L49 | 只读事务 | 保留 |

结论：`SqliteTurnFinalizationWriter` 已是三个用例共享的**事务内**写入器，说明设计 §5.5 的重构是扩展而非新造。P2 将其重构为 `SqliteConversationTurnWriter`，新增"开始回合"和"消费输入边界"两个事务用例，并把 `turn_usage` 关联从 `message_id` 改为 `turn_id`。

必须替换的查询与写入方清单：

1. `messages` DDL：新增必填 `turn_id`、`sequence`；回合级 `duration_ms`/`error_message` 从 messages 及 MessageRecord 中移除，归 conversation_turns／turn snapshot；不能因投影方便继续保留双写。独立的片段错误需求若确实存在须另行定义，不复用回合字段。
2. 新增 `conversation_turns`、`conversation_message_sequences`、`conversation_inputs`、`conversation_input_state`。
3. `turn_usage` PK/FK 改 `turn_id`。
4. `SqliteConversationRepository.ListMessagesAsync` 改按 `sequence`，去掉 `turn_usage` join。
5. `SqliteUsageStatisticsReader.GetSubagentUsageAsync` join 改 `turn_id`。
6. `SqliteTurnFinalizationWriter` → `SqliteConversationTurnWriter`；`UpsertMessageAsync` 的 `onlyIfStreaming` 语义改为"片段封存"。
7. child/continuation 校验与用量读取改 turn 归属。
8. `SubagentExecutionSession`/`ConversationTurnRecorder` 的 message id 假设。
9. `AddUserMessageAsync` 删除，由开始回合事务取代。

### 设计 §13 删除清单的 P0 定位（P2/P3/P6 执行）

| §13 项 | 当前位置 | P0 结论 |
| --- | --- | --- |
| Engine 准入/tombstone/pending 计数 | `ConversationTurnEngine.cs` L30–L150 | 迁移到 `ConversationRunCoordinator`；与 SessionCoordinator 启动锁一并删除 |
| SessionCoordinator 启动锁/运行字典 | `ConversationSessionCoordinator.cs` L22–L200 | 同上 |
| VM 空闲直连执行 | `MainWindowViewModel*.cs` → `ConversationTurnEngine.TryAdmitAsync` | P3 统一走输入受理/dispatcher |
| `AddUserMessageAsync` | `ConversationTurnEngine.cs` L298 | P2 由开始回合事务取代 |
| `UseFunctionInvocation` 装配 | `AiChatClientFactory.Create` L92–L104 | P1 删除无消费者装配 |
| `MessageId = TurnId`、消息回合状态/usage | 见 §3 审计表 | P2 迁移 |
| `SqliteTurnFinalizationWriter` 单 assistant 假设 | `SqliteTurnFinalizationWriter.cs` | 重构为 `SqliteConversationTurnWriter` |
| 按单条 assistant 推断历史/结束 | `IsReplayable`、`TerminalBlockAligner`、`ConversationCompletionNotifier` | P2/P3 统一为 turn record + 片段 |

搜索证据：`grep -rn "TurnId" --include=*.cs SelfClaw.Desktop SelfClaw.Infrastructure`、`grep -rn "BeginTransaction" ...`、`grep -rn "MessageId ==|message.Id ==|AssistantMessage.Id" ...`（本报告 §3/§5 各表均来自这些搜索，已逐条确认其真实职责，非仅同名匹配）。

## 6. 需要写回设计的调整

以下为 P0 实测发现、必须在实现前并入设计/实施文档的调整：

1. **终止判定不能只看 `finish_reason`**（B03、B04）。Ollama native 有工具调用时仍报 `stop`。显式循环应按"是否存在完整且未闭合的工具调用"决定继续，`finish_reason` 仅辅助；`Length` 的 Truncated/Failed 语义仍按既有规则。
2. **必须按响应合并后再决策**（B02、B04）。完整调用可能晚于 finish reason；Anthropic 单条 update 已携带 ProtectedData，合并后才与相应 thinking text 位于同一完整内容中。后续请求序列化仍待 B04 补证。
3. **usage 先按请求归一化**（B05）。一次响应多个 UsageContent 不能增加多次 ProviderCalls；先依据 adapter 的增量／累计契约形成该次请求用量，再跨请求累加，context 取最后一次。不能把所有 raw usage 一律相加。
4. **边界 waiter 必须继承既有失败/取消状态**（B06）。迟到的 waiter 立即失败，禁止永久等待。
5. **固定新循环的请求预算**；实测基线为 129 次请求／128 次工具执行，新目标为每逻辑回合最多 128 次 provider 请求（含首次和 steer）。第 128 次若仍提出客户端工具，则不执行它们、以 BudgetExhausted 终结调用记录并 Failed；纯最终回答仍可成功。不保留“迭代”含义不明的常量名。
6. **child/continuation 的 finalization 归属校验必须重写**（§3）。不能保留 `AssistantMessage.Id == ChildTurnId/ContinuationTurnId`。
7. **`SubagentExecutionSession` 的 provider 历史排除规则**必须按回合归属及准备时快照定义；首个请求前不再创建空 assistant，应删除为排除该占位而存在的过滤。确需排除当前输出时按真实片段／turn 归属处理，不能仅把 ChildTurnId 替换成另一种 ID 而保留无意义的占位分支。
8. **`MessageRecord` 上的 `Usage/DurationMs/ErrorMessage` 与 `MessageStatus` 的回合语义**要随投影一起迁移，不能为省调用点双写（对应 I13/A02）。

## 7. 未覆盖项

| 项 | 状态 | 说明 |
| --- | --- | --- |
| 真实 provider 端到端 | 未执行 | 需 `SELFCLAW_PROVIDER_SMOKE=1` 与已启用 reasoning 模型；P0 用离线 fixture 固定协议形状 |
| WPF/WebView2 desktop smoke | 未执行 | 需 `SELFCLAW_DESKTOP_SMOKE=1`；P0 不涉及 UI 行为变更 |
| 进程崩溃恢复 | 未执行 | 属 P6（R01–R12） |
| 本机 Ollama 实例 | 未执行 | 探针用 NDJSON fixture；未验证真实 Ollama 版本差异 |
| OpenAI Responses 的完整事件族 | 部分覆盖 | fixture 覆盖 reasoning summary、function_call、usage、completed；未覆盖 web_search 等 provider 侧内置工具事件（设计已声明只执行客户端 bindings 内工具） |
| 生产 `DirectTurnInputSession` | 未实现 | P0 只验证最小屏障模式；生产实现属 P2，届时删除 B06 测试 harness |

## 8. 结论

- P0 基线、消费者审计与目标职责表有可复核证据；B01、B04、B06 尚未覆盖任务文档要求的全部退出条件，补证清单见 §9。
- A01（目标职责图）、A02（Turn/Message 分离改造清单）已形成，见 §3、§4。
- 新增 13 项测试，归档全量回归 1246 通过 / 0 失败 / 4 跳过；复审定向重跑 13 通过。部分并发测试仍依赖调度或超时探测，不能统称全部确定性验证。
- §6 共 8 项设计调整已在复审时同步到设计／实施文档；第 1、3、5 项直接影响 P1 循环契约与预算证据。
- P0 不开放任何新功能：Queue / Steer 开关未实现、默认关闭；CLI、child、continuation 行为未改变。
- 可据已证实事实准备 P1，但不能标记 P0 全部通过；进入正式 P1 替换前先补齐 §9 的证据，尤其是下一请求序列化与确定性屏障验证。

## 9. 复审结果与补证清单（2026-10-02）

复审核对了当前 HEAD、resolved packages、三份归档 TRX 和 13 项测试源码。TRX 计数与 §1 一致；本次命令如下，未重复运行全量、Vue 或原生 smoke：

```powershell
$env:DOTNET_CLI_HOME = 'D:\Repositories\SelfClaw\.dotnet'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_NOLOGO = '1'
dotnet test SelfClaw.Tests/SelfClaw.Tests.csproj --no-restore --filter 'FullyQualifiedName~ProtocolBaselineProbeTests|FullyQualifiedName~DirectLoopBaselineTests|FullyQualifiedName~InputBoundaryBarrierBaselineTests' --logger 'trx;LogFileName=p0-review.trx' --results-directory .scratch/direct-message-input/review-results
```

结果：13 通过、0 失败、0 跳过。原始结果在 `.scratch/direct-message-input/review-results/p0-review.trx`；本节保存持久的结论摘要。现有测试文件此次未修改，以下是待执行项：

| ID | 关联验收 | 尚缺的证据及具体修订 |
| --- | --- | --- |
| G01 | B01 | 增加成功的两轮工具往返：3 次 provider 请求、2 次工具批次，捕获每次请求历史、事件序列、唯一终态、hooks／通知适当层级和总用量；不能把 scripted call/result 当成真实往返 |
| G02 | B04 | 真实 adapter + 离线捕获 HTTP handler，验证 assistant 原生内容及 tool result 的第二次请求序列化；覆盖 Anthropic 签名和 OpenAI 两种格式，必要 reasoning 元数据不丢失 |
| G03 | B06 | 分别强制 waiter 先注册／consumer 先失败／先取消／先关闭；真实归约前序 TextDelta 后才提交，提交阻塞期间不能发下一请求；用线程安全事件记录替换跨任务 List，并验证第二个不同 boundary 被拒绝 |
| G04 | B02 / B03 / B05 | 增加 finish reason 后调用到达的顺序断言、Ollama stop + 完整调用断言；串行工具测试以同步点代替 2 秒超时探测；修正两个测试名称（当前含 configured_request_count 的测试断言 129；含 normalized_as_one 的测试实际断言 ProviderCalls=2） |

P1 还须新增 Ollama-style stop 的预算耗尽回归，不能仅凭单次 fixture 声称旧管线耗尽路径已复现。补证完成后更新对应测试全名、结果与 P0 状态，不能只将本表改为完成。

## 10. G01–G04 补证（2026-10-02，P1 替换前）

Windows / .NET SDK 10.0.400 / 包版本同 §1。执行：

```powershell
$env:DOTNET_CLI_HOME = 'D:\Repositories\SelfClaw\.dotnet'
dotnet test SelfClaw.Tests/SelfClaw.Tests.csproj --no-restore --filter 'FullyQualifiedName~ProtocolBaselineProbeTests|FullyQualifiedName~DirectLoopBaselineTests|FullyQualifiedName~InputBoundaryBarrierBaselineTests' --logger 'trx;LogFileName=p0-supplement.trx' --results-directory .scratch/direct-message-input/p0-supplement
```

结果：23 通过 / 0 失败 / 0 跳过。TRX：`.scratch/direct-message-input/p0-supplement/p0-supplement.trx`；HTTP body：测试输出目录 `p0-probes/second-request-*.json`。这是实际 adapter 的离线 fixture 验证，没有连接真实 provider。

| 项 | 实际测试名称（前缀为本节命令中的三个类） | 证据 |
| --- | --- | --- |
| G01 | DirectLoopBaselineTests.Two_tool_round_trips_capture_history_events_terminal_and_usage | 实际执行 2 次工具、3 次请求；逐请求 call/result 历史和结果值、thinking/文本、工具开始/结束、唯一 RunStarted/终态；input=60、output=6、ProviderCalls=3 |
| G02 | ProtocolBaselineProbeTests.Native_response_and_tool_results_serialize_in_second_http_request（3 种格式） | 第二次 HTTP body：Anthropic thinking/signature/tool_use/result；Chat Completions reasoning_content/双调用/结果；Responses reasoning id/summary/encrypted_content、function item id、call_id、arguments/output |
| G03 | InputBoundaryBarrierBaselineTests.Ordered_reduction_and_commit_block_next_request_for_early_and_late_waiter（2 种） | 文本实际归约为 ab；TCS 提交 gate 阻塞下一请求；ConcurrentQueue 记录 commit-start→commit-end→next-request |
| G03 | Consumer_terminal_state_releases_early_and_late_waiters（6 种） | failure/cancel/close × 早/晚注册；等待 consumer 完成后才允许晚注册 |
| G03 | A_second_distinct_boundary_waiter_is_rejected_and_cancellation_releases_the_first；Reconfirming_the_same_boundary_id_reuses_the_committed_result | 不同 boundary 单 waiter、取消清理、已提交结果复用 |
| G04 | OpenAi_chat_completions_fragmented_tool_arguments_probe；Ollama_native_tool_call_probe | call index > finish index；Stop + 完整调用/参数/callId |
| G04 | Function_invoking_loop_processes_multiple_tool_calls_serially_by_default | TCS 释放首工具，完整顺序 a-start→a-end→b；移除 2 秒顺序探测 |
| G04 | Sdk_128_iterations_issue_129_requests_and_execute_128_tools；Legacy_translator_counts_two_usage_fragments_as_two_provider_calls | 名称与旧断言一致：129 请求/128 执行；13 input、6 output、2 ProviderCalls |

发现并修复：

1. 屏障原型原来只拒绝相同 boundary 的重复 waiter，允许不同 boundary 并存。改为每回合最多一个待确认 waiter；仍仅为 P0 原型，P2 后删除。
2. M.E.AI 10.10 Chat Completions 入站读取 reasoning_content，出站却丢弃。新增 adapter 内 `OpenAiChatReasoningReplayClient`：使用 SDK 公共原生消息转换，只补缺失字段。
3. Responses 流式 FunctionCallContent 未保留 completed native item，出站丢失 item id/status。`OpenAiResponsesReplayClient` 关联同 callId 的完整原生 item；reasoning 的 ProtectedData、rs id 和 summary 由 SDK 聚合保留。
4. 首轮新增用例 3 失败 / 20 通过，暴露上述两项及工具 fixture 使用普通 string 而非 DirectToolResult。修正 fixture 和 adapter 后通过全部原断言。中间一次 options patch 因 messages 尚未建立失败，已改用 SDK 公共消息转换；没有降低出站断言或跳过用例。

P0 退出结论：B01–B06 补证通过，A01/A02 目标职责清单保留。旧 §2/§8/§9 的待补证描述为历史状态，本节覆盖当前结论。P1 必须分离纯 SDK 基线与新 runtime 测试，并验证逻辑回合生命周期。

P1 接管后的可复核位置：G01 方法迁到 `DirectConversationLoopTests`，并新增 `Logical_turn_delivers_one_lifecycle_and_one_hook_pair_per_executed_tool`（0/2 工具轮次）；G02 在 `Explicit_loop_replays_native_content_in_second_http_request` 中通过新循环再次执行。`DirectLoopBaselineTests` 仅保留纯 SDK 默认值、129/128 计数和串行探针。旧碎片 ProviderCalls=2 的测试在 P1 删除，其历史证据仍是本节替换前 TRX；新断言为 `Incremental_usage_fragments_count_as_one_provider_call`。G03 已提交结果的晚注册还增加 CommitPublished 同步点。详见 [P1 报告](direct-message-input-p1-implementation-review.md)。

## 附录 A：协议探针原始输出

原始文件位于 `.scratch/direct-message-input/p0-probes/`（Git 忽略）；下表为其中的关键结论摘录。

### OpenAI Chat Completions（`openai-chat-completions.txt`）

```text
--- update[0] messageId=chatcmpl-1 responseId=chatcmpl-1 role=assistant finish=<null>
    Reasoning text="think " protected=null
--- update[1] ... Reasoning text="more" protected=null
--- update[2] ... （工具参数首个分片：无 content）
--- update[3] ... （参数第二个分片：无 content）
--- update[4] ... finish=tool_calls
--- update[5] ... Usage {"InputTokenCount":11,"OutputTokenCount":7,"CachedInputTokenCount":null,
                          "ReasoningTokenCount":3,"TotalTokenCount":18}
--- update[6] ...
    FunctionCall id=call_a name=read_file args={"relativePath":"a.txt"}
    FunctionCall id=call_b name=list_files args={}
=== COALESCED ===
finish=tool_calls
```

### Anthropic Messages（`anthropic-messages.txt`）

```text
--- update[2] ... Reasoning text="let me think" protected=null
--- update[3] ... Reasoning text=""           protected="sig-abc"
=== COALESCED ===
    Reasoning text="let me think" protected="sig-abc"
    FunctionCall id=toolu_1 name=read_file args={"relativePath":"a.txt"}
finish=tool_calls
usage={"InputTokenCount":10,"OutputTokenCount":15,"CachedInputTokenCount":0,"TotalTokenCount":25}
```

### OpenAI Responses（`openai-responses.txt`）

```text
--- update[2] ... Reasoning text="think " protected=null
--- update[8] ... FunctionCall id=call_a name=read_file args={"relativePath":"a.txt"}
finish=tool_calls
usage={"InputTokenCount":11,"OutputTokenCount":7,"ReasoningTokenCount":3,"TotalTokenCount":18}
```

### Ollama native（`ollama-native.txt`）

```text
--- update[0] ... Reasoning text="think " protected=null
--- update[1] ... FunctionCall id=20dc0de1 name=read_file args={"relativePath":"a.txt"}
--- update[2] ... finish=stop
    Usage {"InputTokenCount":11,"OutputTokenCount":7,"TotalTokenCount":18}
    additional={"load_duration":"00:00:00","total_duration":"00:00:00",...}
```

注：`CallId=20dc0de1` 为适配器生成的 GUID 前缀；`finish=stop` 是 Ollama 在工具调用时的实际上报值。
