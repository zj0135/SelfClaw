# Direct 消息输入：P0 补证与 P1 实施审查报告

日期：2026-10-02。当前状态：IR01/IH01/IH02 已关闭，IR02 已按用户要求直接修复并完成端到端验证；最新记录见 §8，全量 1329 通过／0 失败／4 门控跳过。§1–§7 保留历史实施及审核结果。基线 HEAD 为 `0b8c509c37ac1831b51643e19fd8429af84e522a`。

前次独立复审发现 IR02 及两项 Activity 测试时序问题，详见 [审核报告 §7–§9](direct-message-input-p1-independent-review.md)。用户随后要求原 reviewer 直接修复；§8 是这次实际修复和验证记录，不声称又进行了一次不同实现者的独立审核。

范围：仅 P0 补证、显式 Direct 循环及其必要 adapter 修复。没有实现 P2–P6、Queue/Steer 用户入口或数据库结构变更；schema 仍为 v29，没有清理实际数据。开始时已有的三份设计/报告和三份 P0 测试为未跟踪文件，本次在它们基础上继续工作。

独立审核原始结论（2026-10-02）：reviewer 复跑当时全量 1283 通过／4 跳过，但额外探针发现 IR01，暂不签署通过；另外两项异常响应缺口属于继承行为。[P1 独立审核报告](direct-message-input-p1-independent-review.md) 保留原文。下面 §7 是实施者后续修复记录，仍需 reviewer 复审。

## 1. P0 补证与发现

P1 替换前，G01–G04 的 23 项测试全部通过（0 失败、0 跳过）；详见 [P0 报告 §10](direct-message-input-p0-baseline.md)。这是实际 provider adapter + 离线 HTTP handler 的结果，不是真实服务验证。

| 要求 | 实际测试名称及当前归属 | 可核对结果 |
| --- | --- | --- |
| G01 两轮工具往返 | `DirectConversationLoopTests.Two_tool_round_trips_capture_history_events_terminal_and_usage`（P0 时在 `DirectLoopBaselineTests`） | 3 次请求、2 次实际函数执行；逐请求历史的 callId/result/结果值；工具开始→结束；唯一 RunStarted/RunCompleted；input=60、output=6、ProviderCalls=3 |
| G02 第二次请求 | `ProtocolBaselineProbeTests.Native_response_and_tool_results_serialize_in_second_http_request`，3 种协议 | Anthropic thinking/signature/tool_use/tool_result；Chat Completions reasoning_content/双调用/双结果；Responses reasoning id/summary/encrypted_content、function item id/call_id/arguments/output |
| G03 屏障 | `InputBoundaryBarrierBaselineTests.Ordered_reduction_and_commit_block_next_request_for_early_and_late_waiter`（2 项） | 实际归约前序文本为 ab；提交 gate 阻塞下一请求；ConcurrentQueue 记录完整顺序 |
| G03 终结竞争 | `Consumer_terminal_state_releases_early_and_late_waiters`（6 项） | failure/cancel/close × waiter 早/晚注册；晚注册发生在 consumer 任务完成后 |
| G03 单 waiter/晚成功 | `A_second_distinct_boundary_waiter_is_rejected_and_cancellation_releases_the_first`；`Reconfirming_the_same_boundary_id_reuses_the_committed_result` | 不同 boundary 同时注册被拒；取消不泄漏；等待 CommitPublished 后再注册也取得相同已提交结果 |
| G04 时序 | `ProtocolBaselineProbeTests.OpenAi_chat_completions_fragmented_tool_arguments_probe`；`Ollama_native_tool_call_probe` | 明确断言完整 call 晚于 finish；Ollama Stop + 完整调用、参数和非空 callId |
| G04 旧 SDK 行为 | `DirectLoopBaselineTests.Sdk_128_iterations_issue_129_requests_and_execute_128_tools`；`Function_invoking_loop_processes_multiple_tool_calls_serially_by_default` | 129 请求/128 执行；TCS 与完整 a-start→a-end→b 顺序，删除 2 秒超时探测 |

补证暴露并解决三项问题：

1. 原屏障原型只拒绝同 boundary 重复 waiter，允许不同 boundary 并存；改为每回合至多一个待确认 waiter。它仍是测试原型，P2 落地生产输入会话后应删除。
2. M.E.AI 10.10 在 Chat Completions 出站时丢弃入站解析得到的 reasoning_content。`OpenAiChatReasoningReplayClient` 调用 SDK 公共 `AsOpenAIChatMessages` 转换，仅补回该字段，继续由 SDK 序列化其他内容。
3. Responses 流式 function content 丢失 completed native item。`OpenAiResponsesReplayClient` 将匹配 callId 的原生 item 保存在 `RawRepresentation`，保留 item id/status；reasoning 的 ProtectedData、rs id、summary 继续由 SDK 聚合。

上述出站断言没有放宽。初轮补证 3 失败/20 通过：两项 adapter 缺口和一个普通 string 工具 fixture 不符合 DirectToolResult。工具 fixture 改为正式结果契约；一次中间 options patch 尝试因尚无 messages 失败，最终使用 SDK 原生消息转换。旧 `Legacy_translator_counts_two_usage_fragments_as_two_provider_calls` 的 13/6/2 结果保存在 P0 TRX 和报告中；P1 删除该错误 runtime 行为测试，改为正确的按请求计数测试。

## 2. P1 实现及职责迁移

| 原职责/路径 | 当前 owner | 删除或替换 |
| --- | --- | --- |
| factory 内 SDK 自动循环 | `DirectConversationLoop` | 删除 `AiChatClientFactory.UseFunctionInvocation`、MaximumIterations/MaximumConsecutiveErrors 设置 |
| pipeline 工具执行委托 | `DirectToolInvoker.InvokeAsync(FunctionCallContent, requestIndex, token)` | 删除 `AiChatClientPipelineOptions.FunctionInvoker` 和全部 `FunctionInvocationContext` 入口；没有过渡适配层 |
| runtime 一次枚举 SDK 整回合、凭最后 finish 推测耗尽 | loop 返回 `DirectLoopOutcome` | 删除 `DirectAgentChatRuntime.StreamResponseAsync`；终态映射使用 Completed/Length/BudgetExhausted |
| SDK 循环保存原生历史 | `DirectConversationContext` | 保存本回合原生 ChatMessage，校验批次中重复/损坏/未绑定客户端调用；不经显示 DTO 重建 |
| 每个 UsageContent 算一次请求 | `RequestUsageAccumulator` → `DirectEventTranslator.ObserveUsage` → `TurnUsageAccumulator` | 删除 translator 对 usage 碎片直接记账的分支 |
| 测试 fake factory 包装 SDK 循环 | 直接提供单次 IChatClient | 删除 FakeChatClientFactory.pipelineBuilder；迁移 DirectToolPipelineTests 与 ContinuationRecoveryTests |

`DirectAgentChatRuntime` 仍唯一管理准备、runStarting/runCompleted、终态及释放；provider lease 先于 capability lease 释放。HTTP hooks、安全 logging 和 shared pooled handler 机制保留。

`DirectConversationLoop` 每次完整枚举到响应结束，同时把显示事件流式发布，并通过 `ToChatResponseAsync` 聚合原生内容。它没有另存整份原始 update 列表。Length 先收尾；其余响应按完整客户端调用决定是否继续，Stop + 调用不会提前成功。完整调用可以晚于 finish reason；工具执行必须等响应尾部结束。provider 内建工具的其他 AIContent 原样进入原生历史，不被当成本地 AIFunction 执行。

工具仍只经过同一个 invoker：toolExecuting → 审批 → awaited checkpoint → 函数调用 → toolExecuted。Shell 非零等普通 Failed 不计故障预算；实际异常前两次转为模型可见 Failed，第三次终止；审批拒绝为 Canceled；OperationCanceledException 继续传播。未知/损坏客户端工具批次在任何实际工具执行前失败，不隐式重试。

取消前已观察到的 usage 现在会在取消异常前发布一次；终态仍由原有消费者的取消处理完成，runtime 不把取消改成 Failed。消费者主动放弃枚举时，linked cancellation 停止 provider/tool 并释放资源。

## 3. 128/129 差异与用量

| 项 | 旧 SDK 基线 | 当前显式循环 |
| --- | --- | --- |
| 配置含义 | 128 iterations，实际 129 次请求 | `MaximumProviderRequestsPerTurn = 128`，包含首次请求 |
| 持续单工具调用 | 128 次执行后第 129 请求停止 | 127 次执行；第 128 提议不执行 |
| 第 128 次有两个工具 | 不符合新硬上限 | 两个都补 Canceled/BudgetExhausted 结果，回合 Failed，无第 129 请求 |
| 第 128 次纯最终回答 | — | 正常成功 |
| Stop + 完整调用 | finish 本身不能证明成功 | 与 ToolCalls 同样进入循环和预算裁决 |
| 一个请求两个增量 usage 碎片 | 误计 ProviderCalls=2 | 合并为一次请求，ProviderCalls=1 |

请求预占发生在本地 for 循环开始，失败/取消不再发起后续请求；SDK 自身 HTTP 重试不等同于新的逻辑 IChatClient 请求。Steer 尚未实现，也没有单独预算入口。

lease 携带 `AiUsageUpdateKind`。当前四个 adapter 的 IChatClient 输出都是请求累计值，使用 Cumulative；其中 Anthropic 已自行归并 wire usage。Incremental 契约有明确测试。归一化先发生于单请求，然后才跨请求累加；无 usage 的请求不生成零报告，也不增加用量里的 ProviderCalls。实际请求计数和有用量观测请求数不同。

缓存/成本测试的两次有观测请求加一次无观测最终响应：实际请求=3，ProviderCalls=2，input=30、output=12、total=42、reasoning=4、cache read=6、cache write=4、uncached=20，最后有效 context=21；指定输入/输出/缓存单价下估算成本为 106 微美元。测试混合一次上报 total 和一次只上报 input/output 的请求。total 在单请求阶段按两侧已知数据推导，不能留到跨请求累加后再补；累计快照更新计数时不会沿用失效旧 total。缺 input/output 时 total/context 保持未知，reasoning 不重复加入 output。

## 4. 验收映射

下列名称除显式说明外位于 `SelfClaw.Tests.Infrastructure.Agents.Direct`；协议测试位于 `SelfClaw.Tests.Infrastructure.AiProviders`。

| ID | 测试与观测 | 状态 |
| --- | --- | --- |
| L01 | `DirectAgentChatRuntimeTests.StreamTurnAsync_streams_text_and_reasoning_and_normalizes_cumulative_usage`；`DirectConversationLoopTests.Logical_turn_delivers_one_lifecycle_and_one_hook_pair_per_executed_tool`（0/2 工具轮次）；Desktop `ConversationTurnEngineTests.ExecuteAsync_runs_a_successful_turn_through_persistence_projection_and_notification` 与 `ExecuteAsync_ignores_a_late_duplicate_terminal_event` | 通过；runtime 验证生命周期，Desktop 独立验证通知归属 |
| L02 | `DirectConversationLoopContractTests.Calls_after_finish_wait_for_response_end_and_execute_once`（Stop/ToolCalls）；`DirectConversationLoopTests.Explicit_loop_processes_multiple_tool_calls_serially`；G01 历史断言 | 通过 |
| L03 | `DirectToolPipelineTests.Real_function_marshalling_preserves_results_through_runtime_and_recorder`（shell/拒绝/write/read）；`A_faulting_tool_reaches_the_model_as_a_failed_result_and_the_turn_still_ends`；`A_tool_that_fails_without_throwing_never_spends_the_fault_budget`；`DirectToolInvokerHookTests` | 通过 |
| L04 | `DirectConversationLoopContractTests.Invalid_batch_or_checkpoint_failure_executes_no_tools_and_does_not_retry`（4 项）；`DirectToolInvokerTests` 审批/checkpoint 顺序 | 通过 |
| L05 | `Request_128_closes_all_unexecuted_calls_or_accepts_a_final_answer`（4 项，Stop/ToolCalls × 工具/纯回答）；`Length_with_complete_calls_never_executes_them`（2 项）；既有 Length 有/无正文测试 | 通过 |
| L06 | `Cancellation_and_abandoned_consumer_release_provider_then_capability`（provider/tool/consumer）；`Cancellation_after_usage_reports_observed_usage_before_propagating_cancellation`；既有 setup/流失败及释放测试 | 通过 |
| L07 | `ProtocolBaselineProbeTests.Explicit_loop_replays_native_content_in_second_http_request`（3 协议）；共享 G02 全部出站字段断言 | 通过，离线 fixture |
| L08 | `Usage_normalizes_each_request_before_accumulating_cache_context_and_cost`（Incremental/Cumulative，混合 reported/derived total）；`Cumulative_snapshot_without_total_does_not_keep_a_stale_earlier_total`；`Provider_failure_after_usage_preserves_one_observed_request`；`Incremental_usage_fragments_count_as_one_provider_call`；`A_response_without_usage_reports_no_usage_event` | 通过 |
| L09 | CLI runtime/parser/session、SubagentTaskCoordinator、SubagentExecutionSession、ContinuationRecovery、SubagentContinuationExecutor/Committer/Delivery 全量回归 | 通过；原生门控 smoke 单列，未宣称其通过 |
| A03 | 生产代码搜索 `UseFunctionInvocation|FunctionInvocationContext|FunctionInvoker`；fake factory 无 pipelineBuilder；只在纯 SDK DirectLoopBaselineTests 中保留两个 UseFunctionInvocation | 代码核查通过，无双层执行 |
| A05 | 新循环/原生上下文/usage 服务均在 Infrastructure；Core 无改动；新增 enum 独立文件；没有新增通用接口、工作流框架或重复审批规则 | 本阶段代码核查通过；不代表独立审核 |

## 5. 环境、命令和结果

执行目录 `D:\Repositories\SelfClaw`，Windows，SDK 10.0.400 / .NET runtime 10.0.11；M.E.AI / M.E.AI.OpenAI 10.10.0、OpenAI 2.13.0、Anthropic 12.49.0、OllamaSharp 5.4.30、MCP.Core 2.2.0，包缓存 `E:\.nuget\packages`。无包版本变更，无在线 restore。

```powershell
$env:DOTNET_CLI_HOME='D:\Repositories\SelfClaw\.dotnet'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'
$env:DOTNET_NOLOGO='1'
dotnet build SelfClaw.slnx --no-restore
dotnet test SelfClaw.Tests/SelfClaw.Tests.csproj --no-restore --filter 'FullyQualifiedName~DirectConversationLoop|FullyQualifiedName~ProtocolBaselineProbeTests|FullyQualifiedName~InputBoundaryBarrierBaselineTests' --logger 'trx;LogFileName=p1-contracts.trx' --results-directory .scratch/direct-message-input/p1-results
dotnet test SelfClaw.Tests/SelfClaw.Tests.csproj --no-restore --filter 'FullyQualifiedName~Direct|FullyQualifiedName~AiProviders|FullyQualifiedName~ContinuationRecoveryTests|FullyQualifiedName~McpStdioProcessTreeSpikeTests|FullyQualifiedName~InputBoundaryBarrierBaselineTests' --logger 'trx;LogFileName=p1-targeted-complete.trx' --results-directory .scratch/direct-message-input/p1-results
dotnet test SelfClaw.slnx --no-restore --logger 'trx;LogFileName=p1-delivery.trx' --results-directory .scratch/direct-message-input/p1-results
git diff --check
rg -n 'UseFunctionInvocation|FunctionInvocationContext|FunctionInvoker|pipelineBuilder' SelfClaw.Infrastructure SelfClaw.Tests
```

| 执行 | 通过 | 失败 | 跳过 | 证据/说明 |
| --- | ---: | ---: | ---: | --- |
| P0 补证最终 | 23 | 0 | 0 | `p0-supplement/p0-supplement.trx`，在 P1 替换前运行 |
| 初轮 P1 受影响测试 | 389 | 0 | 0 | `p1-results/p1-targeted.trx` |
| P1 契约 + 屏障（total 补强前） | 46 | 0 | 0 | `p1-results/p1-contracts.trx` |
| 受影响测试（total 补强前） | 570 | 0 | 0 | `p1-results/p1-targeted-final.trx` |
| 第一次全量 | 1281 | 1 | 4 | `p1-results/p1-full.trx`；MCP PID 文件发布竞争，见下文 |
| PID fixture 修复后全量 | 1282 | 0 | 4 | `p1-results/p1-full-final.trx` |
| 新 total 回归修复前 | 0 | 3 | 0 | `p1-results/p1-total-regression-before.trx`；实际断言 42→21、16→12 |
| total 修复后受影响测试 | 571 | 0 | 0 | `p1-results/p1-targeted-complete.trx` |
| total/context 修复后全量 | 1283 | 0 | 4 | `p1-results/p1-full-complete.trx` |
| 交付全量 | 1283 | 0 | 4 | `p1-results/p1-delivery.trx`，43s；包含最终 HTTP fixture 参数 schema 与 function status 断言 |
| build | 成功 | 0 错误 | — | 0 警告；最终源码再次由最终 test 命令构建 |

TRX 均在 `.scratch/direct-message-input/` 下。该目录忽略 Git；本报告保存持久结论，fixture、断言和命令可重跑。第二次请求原始 JSON 位于 `SelfClaw.Tests/bin/Debug/net10.0-windows10.0.19041.0/p0-probes/`，P0 为 `second-request-*.json`，P1 为 `p1-second-request-*.json`，只含离线合成输入和测试凭据占位。

第一次全量失败是 `McpStdioProcessTreeSpikeTests.ConnectAsync_CancellationDuringInitialize_TerminatesStdioProcessTree`：读到了 Node 尚未关闭的 PID 文件，发生 IOException。修复仅涉及 `mcp-process-tree-fixture.js`，先写同目录临时文件再 rename，文件存在成为完整发布信号；没有吞掉 IO 异常、删除进程退出断言或跳过测试。后续 targeted 包含两项进程树测试并通过。

开发中另一次 HTTP 契约失败来自 Anthropic final fixture 缺少协议必填 usage；已补完整真实协议形状（input=5/output=2），因此它观测 2 次用量；OpenAI final fixtures 故意无 usage，仍只观测 1 次。不是调整生产逻辑去接受损坏协议。编译过程修复了测试事件字段名、构造参数及 SDK 实验属性诊断，最终构建无新增警告。

最终核查另外补出了 total 的真实计数漏洞：一条请求上报 total，另一条仅有 input/output 时，旧跨请求 accumulator 会只保留显式 total 的子集；累计快照缺新 total 也可能保留过期 total。先增加严格测试，3 项全部复现失败，再在单请求归一化层修复，没有修改 Core/CLI 累加器。input-only 失败/取消的 total/context 也断言为 null。以上失败记录和后续通过记录都保留。

total 漏洞复现命令使用同一 `dotnet test SelfClaw.Tests/SelfClaw.Tests.csproj --no-restore`，filter 为 `FullyQualifiedName~Usage_normalizes_each_request|FullyQualifiedName~Cumulative_snapshot_without_total`，logger 为 `trx;LogFileName=p1-total-regression-before.trx`，results-directory 同上。

交付源码核查：`git diff --check` 无空白错误（Git 提示部分文件提交时 LF→CRLF）；旧 SDK 搜索仅命中 `DirectLoopBaselineTests.cs` 两处。`git diff --name-only -- SelfClaw.Core SelfClaw.Desktop SelfClaw.Infrastructure/Data SelfClaw.TranscriptVue` 无输出，确认这些产品层没有变化。最终全量中的循环契约/行为测试共 27 项、协议测试 10 项、屏障 10 项全部通过；按名称包含 Cli/Subagent/Continuation 分组分别为 87/111/10 项通过，分组可能交叉，不能相加当作总量。

交付 TRX SHA-256：`p1-delivery.trx` = `7ea54330aa845bd005e1364903f9f343024fa5d3ed530f7ef9e5c5cb38abe119`。XML 的 total=1287、executed=1283、passed=1283、failed=0；另有四条结果的 outcome=NotExecuted（即下节门控跳过项）。

## 6. 未验证项、偏差和审核入口

4 个跳过用例：

- `ActivityProviderSmokeTests.Configured_provider_reasoning_reaches_the_live_activity_channel`：未设置 SELFCLAW_PROVIDER_SMOKE=1。
- `ActivityProcessRecoveryTests.Child_process_waits_with_uncommitted_text_and_recorded_tool`。
- `ActivityProcessRecoveryTests.Forced_process_exit_recovers_only_persisted_history_as_interrupted`。
- `DesktopActivityWebViewTests.Real_WebView_renders_three_live_children_and_restores_after_navigation`。后三项需 SELFCLAW_DESKTOP_SMOKE=1。

未运行：真实 provider 服务、真实 Ollama 实例、WPF/WebView2 手动 smoke、Vue 测试/build（无 Vue 产品代码变化）。fixture 不证明服务器会接受所有真实 reasoning 内容或全部 provider 内建工具事件族。SDK 升级时应重跑原生 replay 测试。

设计细化：P0 必须先修复两个 OpenAI adapter 缺口后才能进入 P1；取消时增加已知用量发布；total 推导必须先按请求进行，失效累计 total 必须清除；全量回归触发的 PID fixture 原子发布修复属于验证可靠性修复。未改变产品功能范围。`DirectConversationContext` 只接管本回合原生历史及客户端调用校验；运行中输入预算裁剪、turn/message 分离和持久化输入会话仍留在 P2 及以后。

建议 reviewer 首先审查 `DirectConversationLoop`、`DirectConversationContext`、`RequestUsageAccumulator` 与两个 OpenAI replay client，再核对 P1 的 128 次预算、第二次 HTTP 请求及唯一 invoker 调用点。最后检查 factory 和测试工厂中的旧 SDK 包装已删除、CLI/child/continuation 无产品行为改造。自测与实施者代码核查完成后，仍需另一位 reviewer 独立审核。

## 7. 独立审核问题修复（2026-10-02）

### 7.1 IR01：Incremental 丢失 total-only 片段

审核结论属实。原实现一旦遇到没有 total 的增量片段，就在 Build 清空已累加 total，改用整个请求的 input/output 和；这会把 `total=10` 后的 `input=3/output=4` 算成 7。

修复限定在归一化职责内：Incremental 对每个片段先取显式 total，或者在该片段 input/output **都已知**时推导 total，再交给 UsageDetails.Add 累加。片段复制后归一化，不修改 adapter 提供的对象，Build 重复调用也不改变结果。Cumulative 的快照合并路径保留原行为。

| 增量输入 | 修复后 total | 原则 |
| --- | ---: | --- |
| total=10 → input=3/output=4 | 17 | 保留 total-only 观测并加上可推导的 7 |
| input=3/output=4 → total=10 | 17 | 顺序反转同样成立 |
| input=3/output=4/total=8 → input=5/output=6/total=12 | 20 | 显式 total 优先，不能重算为 18 |
| input=3/output=4 → input=5/output=6 | 18 | 两个可推导 total 相加 |
| total=10 → input=3 → output=4 | null（完整 total 未知） | 后两个增量片段各自缺一侧，不能互相补齐或宣称完整 total=7 |

对最后一类，累加器内部保留已有计数，另外跟踪完整性；返回的 input/output 仍是已观察到的 3/4，完整 total 不可确定。DirectEventTranslator 也保留该未知状态，避免在发布 UsageReportedEvent 时由通用 turn accumulator 又从不完整分项推导出 7。没有修改 Core/CLI 累加器或数据库结构。

正式回归：

- `RequestUsageAccumulatorTests.Incremental_total_only_and_derived_total_preserve_both_fragments`（正序/逆序）。
- `Incremental_all_reported_or_all_derived_totals_are_added_once`（全部显式/全部推导）。
- `Incremental_missing_components_do_not_invent_a_complete_total_from_other_fragments`。
- `DirectConversationLoopContractTests.Incremental_total_only_fragments_survive_the_runtime_usage_report`（17/未知两项），验证运行时发出的报告，不止单测 normalizer。

### 7.2 IH01：过滤终态仍执行工具（继承问题）

显式循环仍完整枚举响应，再统一分类。ContentFilter 或原生 `ErrorContent(ErrorCode="Refusal")` 返回新的 ContentFiltered outcome，由 runtime 映射为 `RunCompletionStatus.Blocked`。本响应提出的调用闭合为 `Canceled`，reason=`ProviderContentFilter`；它们不进入 invoker，不触发审批、checkpoint、工具执行或下一请求。此前已收到的正文、拒绝内容及 usage 保留；Refusal 的文字通过现有正文事件展示。

其他 provider ErrorContent 和未支持的 finish reason 明确失败，也不会执行工具。Length 与正常 Stop + 完整工具调用的既有行为保留。没有新增审批系统或第二个工具调用入口。

`DirectConversationLoopContractTests.Abnormal_response_never_executes_tools_or_requests_a_followup` 覆盖 filter/refusal/unknown-finish/error 四种输入：finish 之后仍到达两个完整调用，断言实际执行=0、checkpoint=0、请求=1、唯一非成功终态、内容/usage 保留，过滤调用结果逐个闭合。

### 7.3 IH02：没有有效响应却成功（继承问题）

每次请求都单独检查，不能拿前一工具请求的正文来证明后一空响应成功：

- 零 update、仅 usage、仅 ResponseId/ModelId 元数据、仅空 text/thinking，且没有完成信息：Failed，错误为 `The provider returned no response content or completion information.`；已知 usage 仍上报。
- 明确 Stop 的合法无正文响应、Stop + usage：仍允许成功。
- 没有 finish 但有有效 text、thinking、ProtectedData 或受支持的原生内容：保持可完成行为；不简单强制所有成功都必须有正文。

正式回归 `No_content_or_completion_is_a_failure_even_after_a_successful_tool_batch`（6 项，含首请求/工具后请求）和 `Explicit_completion_or_supported_response_content_can_succeed_without_body_text`（6 项）。另有 `DirectAgentChatRuntimeTests.Filtered_or_empty_response_delivers_one_non_success_lifecycle`（2 项）核对唯一终态、runCompleted 的 blocked/failed 状态、零工具计数及 provider 释放。

三个旧测试用于验证默认模型、工具绑定、忽略 hook 失败，原来用零响应 fixture 却期待成功。本次把这些 fixture 改成明确 Stop，保留其成功断言；零响应真实失败行为由独立的新测试覆盖，未改为跳过或降低断言。

### 7.4 验证与复审状态

环境仍为 Windows / .NET SDK 10.0.400 / runtime 10.0.11，依赖版本未变。新增正式用例 25 项。结果目录 `.scratch/direct-message-input/p1-review-fixes/`：

| 执行 | 通过 | 失败 | 跳过 | TRX |
| --- | ---: | ---: | ---: | --- |
| 修复前新增边界用例 | 8 | 15 | 0 | `review-fixes-before.trx` |
| 初次定向（含旧零响应 fixture） | 86 | 3 | 0 | `review-fixes-targeted.trx` |
| 修正 fixture 后受影响回归 | 592 | 0 | 0 | `review-fixes-targeted-final.trx` |
| 原样独立审核探针重跑 | 5 | 0 | 0 | `reviewer-probes-after-fix.trx` |
| 普通正式全量 | 1308 | 0 | 4 | `review-fixes-full.trx` |

独立探针重跑使用 reviewer 原始文件和 targets，含旧 SDK 过滤响应对照；**此次由修复者执行，不等于 reviewer 已独立复审通过**。之后恢复普通 build，0 警告/0 错误；正式全量 TRX 确认不含 `DirectP1IndependentReviewProbes`。

```powershell
$env:DOTNET_CLI_HOME='D:\Repositories\SelfClaw\.dotnet'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'
$env:DOTNET_NOLOGO='1'
# 修复前正式边界测试
dotnet test SelfClaw.Tests/SelfClaw.Tests.csproj --no-restore --filter 'FullyQualifiedName~RequestUsageAccumulatorTests|FullyQualifiedName~Incremental_total_only_fragments|FullyQualifiedName~Abnormal_response|FullyQualifiedName~No_content_or_completion|FullyQualifiedName~Explicit_completion_or_supported' --logger 'trx;LogFileName=review-fixes-before.trx' --results-directory .scratch/direct-message-input/p1-review-fixes
# 修复后受影响回归
dotnet test SelfClaw.Tests/SelfClaw.Tests.csproj --no-restore --filter 'FullyQualifiedName~Direct|FullyQualifiedName~AiProviders|FullyQualifiedName~ContinuationRecoveryTests' --logger 'trx;LogFileName=review-fixes-targeted-final.trx' --results-directory .scratch/direct-message-input/p1-review-fixes
# reviewer 原始临时探针，仅此命令注入
dotnet test SelfClaw.Tests/SelfClaw.Tests.csproj --no-restore -p:CustomAfterMicrosoftCommonTargets=D:\Repositories\SelfClaw\.scratch\direct-message-input\p1-independent-review\review-probes.targets --filter 'FullyQualifiedName~DirectP1IndependentReviewProbes' --logger 'trx;LogFileName=reviewer-probes-after-fix.trx' --results-directory .scratch/direct-message-input/p1-review-fixes
# 恢复普通构建和正式全量
dotnet build SelfClaw.slnx --no-restore
dotnet test SelfClaw.slnx --no-restore --logger 'trx;LogFileName=review-fixes-full.trx' --results-directory .scratch/direct-message-input/p1-review-fixes
```

全量 TRX SHA-256：`5cdf0741de5d3ecb7007d4d6967f902561aa05d0ad14216816b9577205c7bffa`；原样探针修复后结果 SHA-256：`de8d0f1ff6fd684688da5b39cc6ad99c1f21e5121dd0de76f4dba292f8875247`。

四个门控 smoke 仍与 §6 一致；没有运行真实 provider、桌面手动 smoke 或 Vue 验证。P2–P6、Queue 入口、数据库及实际数据没有改动。当前状态：IR01/IH01/IH02 修复与自测完成，待独立复审；原独立审核报告保持原文。

## 8. IR02 直接修复与最终验证（2026-10-02）

用户要求直接修复后，改造的是用量契约的所有权，没有在每一层继续追加 total 清零逻辑。

### 8.1 实现与职责

- `TurnUsage` / `UsageReportedEvent` 明确表达已经归一化的贡献；TotalTokens=null 是未知值，不是消费者可以重新计算的占位符。
- `TurnUsageAccumulator` 删除 Build 时的 input + output 回退。任一贡献的 total 未知，合并后也未知；完整 total 正常相加，多次累加器传递保持相同结果。
- `DirectEventTranslator` 删除 `_hasIncompleteTotal` 和 BuildUsage 中覆盖 total 的补偿。请求级原始片段仍由 RequestUsageAccumulator 归一化，之后只经过统一 Core 累加规则。
- Claude／Codex／OpenCode parser 在发出 UsageReportedEvent 前处理 total：显式总量优先，否则只对同一观测中均已知的 input/output 求和。缓存口径和 CLI 成本高水位保持原规则。
- recorder、SQLite writer 不需要增加运行模式分支或特殊写入代码；它们自然保留规范化后的 null。没有新增 DTO 字段、数据库迁移或 P2 功能。

这次允许修改 Core 契约及 CLI 生产方，以修复所有消费者共享的语义边界。原来的“尽量不改 Core”不再作为保留错误回退的理由。

### 8.2 回归范围

既有 `DirectUsagePersistenceTests` 九项均通过：Interactive、child、continuation 各覆盖 incremental-unknown、cumulative-partial-error、incremental-complete。SQL 查询直接断言未知为 DBNull、完整值为 17；continuation 仍在终态前无持久化，失败的工具后续请求仍正确 DeadLetter。

既有 Core 三项未知值测试从失败变为通过。新增 `CliUsageNormalizationTests` 九项覆盖三种 CLI 的完整观测求和、部分观测不能互相补齐、cache-inclusive input 和显式 total 优先，验证改变共享累加器没有丢掉 CLI 正常总量。

上次全量失败的两个 Activity 测试也做了测试协议修复：

- `ActivityPanelBridgeTests` 为各请求提供独立 requestId 并按 ID 查响应，避免异步 state push 被误当成最后一条错误／内容响应。
- 详情选择完成后确认当前在途 state，使排队的 detail snapshot 得以发送；`ActivityPanelLifecycleTests` 不再把 queued 当作详情已经投递。

没有修改 Activity 产品逻辑，没有添加延时重试、跳过用例或放宽内容／状态断言。

### 8.3 实际验证结果

| 执行 | 通过 | 失败 | 跳过 | TRX |
| --- | ---: | ---: | ---: | --- |
| Direct 持久化、Core、请求归一化、CLI parser／归一化及循环定向 | 94 | 0 | 0 | ir02-fix-targeted.trx |
| 最终普通 .NET 全量（含两个 Activity 用例） | 1329 | 0 | 4 | ir02-fix-full.trx |

结果目录 `.scratch/direct-message-input/ir02-fix/`；全量 SHA-256 为 `8a6747e8b1fe862549ad7713b4777671583997a49a2c0fef8fb0963cc37b8dd2`。测试直接构建当前生产及正式测试源，没有注入临时 review probes。`git diff --check` 通过，Git 的 LF→CRLF 提示不是空白错误。

```powershell
$env:DOTNET_CLI_HOME='D:\Repositories\SelfClaw\.dotnet'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'
$env:DOTNET_NOLOGO='1'
dotnet test SelfClaw.Tests/SelfClaw.Tests.csproj --no-restore --filter 'FullyQualifiedName~DirectUsagePersistenceTests|FullyQualifiedName~TurnUsageAccumulatorTests|FullyQualifiedName~RequestUsageAccumulatorTests|FullyQualifiedName~CliUsageNormalizationTests|FullyQualifiedName~StreamJsonParserTests|FullyQualifiedName~JsonEventStreamParserTests|FullyQualifiedName~DirectConversationLoop' --logger 'trx;LogFileName=ir02-fix-targeted.trx' --results-directory .scratch/direct-message-input/ir02-fix
dotnet test SelfClaw.slnx --no-restore --logger 'trx;LogFileName=ir02-fix-full.trx' --results-directory .scratch/direct-message-input/ir02-fix
git diff --check
```

结论：IR02 修复完成，已发现的 P1 审核问题全部解决，P1 实现与验证完成。四项原生／真实 provider smoke 仍为门控跳过，未宣称通过；P2–P6、Queue／Steer 入口、schema 和用户数据均未改动。修复未提交 Git，保留用户原有工作区变更。
