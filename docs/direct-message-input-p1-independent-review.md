# Direct 消息输入：P1 独立审核

日期：2026-10-02。审核对象：基线 `0b8c509c37ac1831b51643e19fd8429af84e522a` 上的当前未提交 P1 工作区。实施者报告见 [P1 实施报告](direct-message-input-p1-implementation-review.md)。

当前状态（2026-10-02）：IR01／IH01／IH02 已关闭；IR02 已按用户要求由原 reviewer 直接修复，正式全量 1329 通过／0 失败／4 门控跳过，见 §9。§1–§8 保留历史审核证据，最新修复记录不冒充另一位 reviewer 的独立复审。

## 1. IR01：增量用量丢弃已经上报的 total（需修复，中等优先级）

位置：[RequestUsageAccumulator.cs](../SelfClaw.Infrastructure/AiProviders/RequestUsageAccumulator.cs)，`Observe` 的 Incremental 分支与 `Build` 中 `_incompleteIncrementalTotal` 处理（审核时第 18–23、30–35 行）。

确定性输入：

```csharp
var usage = new RequestUsageAccumulator(AiUsageUpdateKind.Incremental);
usage.Observe(new UsageDetails { TotalTokenCount = 10 });
usage.Observe(new UsageDetails { InputTokenCount = 3, OutputTokenCount = 4 });
// 两个增量片段的 total 分别是 10 与 3 + 4，因此本请求 total 应为 17。
```

实际 `Build().TotalTokenCount == 7`，独立回归断言期望 17，失败。第二个片段将 `_incompleteIncrementalTotal` 置为 true；Build 清空已经累计的 total，然后仅从已知 input/output 求和，丢掉第一个只有 total 的增量 10。

影响范围：本次新增的 Incremental 契约及其未来消费者。当前工厂的四种 adapter lease 使用 Cumulative，本次没有发现该案例直接改变它们的正常用量；不能把影响扩大为“所有 provider 都少计”。不过代码与 P1 声明支持的增量归一化契约不一致，现有 L08 未覆盖 optional counter 组合。

修复方向：对每个增量片段先确定其可用 total（显式 total 优先，否则仅在该片段 input/output 均已知时推导），再累加这些值；无法推导的片段单独跟踪完整性。不能因为后一个片段省略 total 就删除前一个明确上报的 total，也不能从不完整的分项累计值反推完整总量。

复审要求：增加上述 total-only + derived-total 的回归，并覆盖顺序反转、全部显式 total、全部可推导 total、确实缺少分项无法推导的情况；重新运行 L08 与受影响测试。生产代码及正式测试此次未修改。

## 2. IH01：过滤终态仍可执行工具（继承缺口，非本次新增回归）

位置：[DirectConversationLoop.cs](../SelfClaw.Infrastructure/Agents/Direct/DirectConversationLoop.cs)，RunAsync 第 25–39 行。

输入为一个包含完整客户端 function call 的响应，`FinishReason = content_filter`。当前循环只先判断 Length，其余都交给 AppendResponse；发现调用后执行工具、请求下一响应，最终可报告成功。

独立探针设置一个仅递增计数的 `write_probe`：期望该过滤响应不执行工具，实际执行计数为 1；第二次模拟响应正常结束。探针没有修改真实文件。

为区分新增回归和继承行为，另用纯 SDK `UseFunctionInvocation` 对同样的输入做对照，确认旧 SDK 同样执行了该调用并取得下一响应。该对照测试通过，因此不能把本问题归因于“移除 SDK 循环导致”。

建议：在显式循环的终态分类中明确 ContentFilter 等拒绝／非正常终态，不从这些响应执行工具；保留可展示的拒绝内容和已知用量，返回明确结果。不要只用“不是 Length 且含调用”作为执行条件。该完善不需要新增审批系统或重构另一条调用链。

## 3. IH02：无有效响应被标记成功（继承缺口，非本次新增回归）

位置：[DirectConversationLoop.cs](../SelfClaw.Infrastructure/Agents/Direct/DirectConversationLoop.cs) 第 27–28 行，以及 [DirectConversationContext.cs](../SelfClaw.Infrastructure/Agents/Direct/Context/DirectConversationContext.cs) 的 AppendResponse。

两个独立输入分别是：

- provider 立即结束枚举，零 update。
- provider 只发送 input usage，随后结束；没有 assistant 内容、工具或终止标记。

两者实际均产生 `RunCompletedEvent(Succeeded)`，期望失败的断言均失败。`calls.Length == 0` 无法单独证明一个有效响应已经完成；AppendResponse 目前只拒绝“ToolCalls 但没有调用”。

旧 Direct runtime 在 finish reason 为空时同样落入默认成功分支，故本项是继承行为，不声称新回归。它在未来 Queue 中会放大为“本轮未得到有效回答却自动推进下一项”。

建议：定义无有效响应的判定并输出可诊断失败，同时保留 usage。不要简单要求所有成功响应都必须有正文：有明确完成信息的合法无正文响应、reasoning／其他受支持内容应按协议处理。上述两个无完成信息的输入必须单独回归。

## 4. 已核实的实现与架构

- P0 G01 的三次请求／两次工具往返已经使用实际 AIFunction 调用验证；G02 捕获第二次 HTTP 请求，覆盖 Anthropic 签名、Chat Completions reasoning_content 和 Responses 原生 item；G03 早／晚注册与终结状态用明确同步点验证。原 P0 的关键补证缺口已关闭。
- Direct 工厂已移除 UseFunctionInvocation 与 FunctionInvocationContext 路径；旧 SDK 调用仅保留在纯 SDK 基线测试，不套在显式循环外。
- 128 次逻辑请求硬上限、最后一批工具不执行并发布 BudgetExhausted、Stop + 完整调用、响应尾部调用、先验证整批再执行等已读码并复跑契约用例。
- 正常工具仍集中通过 invoker 的 hooks／审批／awaited checkpoint／连续故障预算。取消及消费者放弃时的 provider-before-capability 释放顺序已验证。
- 原生响应通过 SDK 聚合，显示 DTO 不回流；两个 OpenAI 修补器只处理已通过 fixture 证明的 SDK replay 缺口，没有重写整套协议。
- 本轮没有发现 P1 越界实现 Queue／Steer UI、P2 schema 或 turn/message 迁移。Core／Desktop 产品代码未改，child／continuation 正常回归通过。
- MCP PID fixture 原子发布属于测试准备信号修复；没有吞异常、放松进程退出断言或跳过失败用例。

这些结论只覆盖本次实际检查与测试。没有以全量绿色替代 IR01 的失败证据，也没有把架构门槛解释为必须新增更多服务。

## 5. 独立验证与证据

| 执行 | 通过 | 失败 | 跳过 | 结果文件 |
| --- | ---: | ---: | ---: | --- |
| 循环／协议／屏障／invoker 定向测试 | 65 | 0 | 0 | p1-independent-targeted.trx |
| 未注入独立探针的现有 .NET 全量 | 1283 | 0 | 4 | p1-independent-full.trx |
| 新增独立边界探针 | 0 | 4 | 0 | p1-independent-probes.trx |
| 旧 SDK 对过滤响应的对照 | 1 | 0 | 0 | p1-legacy-comparison.trx |
| 恢复正常构建入口后的 build | 成功 | 0 错误 | — | 0 警告；无额外测试注入 |

四个失败分别是 IR01 一项、IH01 一项、IH02 两项。现有套件的四个跳过仍为 Activity provider／process recovery／WebView smoke；未设置相应环境变量，未运行真实 provider 或桌面 smoke。本次未运行 Vue 测试，Vue 产品代码无变更。

原始结果和临时探针位于 `.scratch/direct-message-input/p1-independent-review/`，被 Git 忽略；本报告保存问题输入、实际输出、代码位置及命令，不依赖仅本机可见的附件解释结论。

全量结果 SHA-256：`f284224fe659b3887bd8293a9e0e6938b4e823f9be69ebc56978590da3085794`。

```powershell
$env:DOTNET_CLI_HOME='D:\Repositories\SelfClaw\.dotnet'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'
$env:DOTNET_NOLOGO='1'
dotnet test SelfClaw.Tests/SelfClaw.Tests.csproj --no-restore --filter 'FullyQualifiedName~DirectConversationLoop|FullyQualifiedName~ProtocolBaselineProbeTests|FullyQualifiedName~InputBoundaryBarrierBaselineTests|FullyQualifiedName~DirectToolInvoker' --logger 'trx;LogFileName=p1-independent-targeted.trx' --results-directory .scratch/direct-message-input/p1-independent-review
dotnet test SelfClaw.slnx --no-restore --logger 'trx;LogFileName=p1-independent-full.trx' --results-directory .scratch/direct-message-input/p1-independent-review
```

边界探针通过临时 MSBuild targets 仅添加到测试编译，没有修改项目文件或正式测试源码：

```powershell
dotnet test SelfClaw.Tests/SelfClaw.Tests.csproj --no-restore -p:CustomAfterMicrosoftCommonTargets=D:\Repositories\SelfClaw\.scratch\direct-message-input\p1-independent-review\review-probes.targets --filter 'FullyQualifiedName~DirectP1IndependentReviewProbes' --logger 'trx;LogFileName=p1-independent-probes.trx' --results-directory .scratch/direct-message-input/p1-independent-review
dotnet build SelfClaw.Tests/SelfClaw.Tests.csproj --no-restore
```

第一轮探针为四项；其后增加了旧 SDK 对照，并使用 `FullyQualifiedName~DirectP1IndependentReviewProbes.Legacy_sdk` 单独运行。最终普通 build 已去掉额外 Compile 注入，后续正常测试不应混入这些预期失败探针。

## 6. 复审入口

1. 修复 IR01，提交正式回归测试及新的用量验证结果；不要删除 Incremental 测试来换取绿色。
2. 明确 IH01／IH02 的处理阶段。建议在仍集中改造循环的 P1 一并解决；若单独安排，必须保留可追踪任务并在 Queue 自动推进开放前处理，不算作本次新增回归。
3. 更新实施报告及任务状态，再进行独立复审。现有 P0 补证不需要重做；有变化的 adapter／协议／生命周期用例应重新运行。

本次只进行了审核、临时复现及文档记录，没有修复生产实现或清理用户数据。

## 7. 修复后的独立复审（2026-10-02）

### 7.1 已关闭的问题

- IR01：total-only=10 与可推导 input=3/output=4 正序／逆序均为 17；累加器不修改 adapter 对象，重复 Build 不改变结果。新增正式测试和原样独立探针均通过。
- IH01：ContentFilter／Refusal 在工具执行前分类为非成功终态，不触发工具、checkpoint 或下一请求；过滤调用有取消结果。未知 finish reason／provider error 也明确失败。原样过滤探针通过。
- IH02：零 update、仅 usage、仅元数据或空内容且无完成信息不再成功；工具轮次之后的空响应也失败。显式 Stop、有效 reasoning 等合法无正文输入仍被允许。原样两个空响应探针通过。

本次没有发现上述修复重新引入双 SDK 循环、第二套审批或 P2 schema 改造。新的终态分类仍在显式循环内完成，修复方向正确。

### 7.2 IR02：归一化后的未知 total 在记录器中再次被推导（待修复，中等优先级）

定位：

- [DirectEventTranslator.cs](../SelfClaw.Infrastructure/Agents/Direct/DirectEventTranslator.cs)：BuildUsage 用 `_hasIncompleteTotal` 保持事件中的 TotalTokens=null。
- [ConversationTurnRecorder.cs](../SelfClaw.Desktop/Services/Runtime/ConversationTurnRecorder.cs)：收到 UsageReportedEvent 后再次 `turn.Usage.Observe(usage.Usage)`，终结时调用 `turn.Usage.Build()`。
- [TurnUsageAccumulator.cs](../SelfClaw.Core/Runtime/TurnUsageAccumulator.cs)：当累计 total 为 null，但 input/output 均非 null 时，自动推导两者之和。

当前修复只覆盖到 runtime 事件边界。共享 UsageReportedEvent 无法让消费者区分“还没推导 total”和“已归一化且 total 明确未知”；因此 translator 清空的值又被下游推导回来。原来只断言 `events.OfType<UsageReportedEvent>()` 的测试看不到这一层。

此次使用真实 Direct runtime → ConversationTurnRecorder → DesktopTurnFinalizer → SQLite repository → ListMessagesAsync 验证，没有只模拟 accumulator：

| 场景 | Runtime 事件 TotalTokens | SQLite 重读 TotalTokens | 预期 |
| --- | --- | --- | --- |
| Incremental：total=10 → input=3 → output=4，最后正常完成 | null | 7 | 保持 null；各片段缺失分项不能相互补齐 |
| Cumulative：第一请求 input=10/output=2/total=12 并执行工具；第二请求只观察 input=7 后 provider 抛 IOException | null | 19 | 保持 null；第二请求 output 未知，整个回合完整 total 未知 |

两个探针均先断言 runtime 报告为 null（通过），再断言 SQLite 重读为 null（失败）。第二个案例使用当前生产默认 Cumulative 模式，因此影响不再仅限于预留的 Incremental 分支：多次调用后出现部分用量和错误时也会发生。

修复要求：明确已经归一化的 Direct 回合快照与需要累计／推导的 usage observation 的契约，统一 total 完整性语义并贯穿 recorder 与持久化。可以重构相关 Core 契约／共享累加器／recorder 的必要边界；不要继续只在 translator 用 `with { TotalTokens = null }` 补偿，也不要用 0 或负数冒充未知。CLI 仍需正常累加，必须保留它的回归。

验收必须落到真实 SQLite 重读；同时确认完整输入的 total=17 仍为 17，以及普通 CLI、child、continuation 的用量归属和累计没有回归。本项不需要提前实施 P2 的 conversation_turns schema，但不能因为“不改 Desktop/Core”而留下语义断层。

### 7.3 本次测试结果与全量异常

| 执行 | 通过 | 失败 | 跳过 | 文件 |
| --- | ---: | ---: | ---: | --- |
| 修复相关正常套件 | 88 | 0 | 0 | p1-rereview-targeted.trx |
| 上轮原样独立探针（含旧 SDK 对照） | 5 | 0 | 0 | p1-rereview-original-probes.trx |
| 未注入临时探针的全量 | 1306 | 2 | 4 | p1-rereview-full.trx |
| 两项 Activity 失败用例单独复跑 | 2 | 0 | 0 | p1-rereview-activity-isolated.trx |
| 新增 runtime→recorder→SQLite 探针 | 0 | 2 | 0 | p1-rereview-persistence.trx |

本次不能声称独立复跑全量全绿。两个全量失败分别是：

1. `ActivityPanelBridgeTests.Content_page_checks_scope_selection_version_and_serialized_budget`：使用 Messages.Last 读取 error 时取得了不含该字段的其他消息，JsonElement.GetProperty 抛异常。
2. `ActivityPanelLifecycleTests.Timeout_keeps_partial_content_and_terminalizes_the_task_and_open_tool`：读取 detail 时最新 state 尚无 detail，GetProperty 抛异常；输出中 detail 请求仍为 queued。

两者单独复跑通过，症状与异步消息／状态选择时序有关；没有证据证明是本次 Direct 修复引起，也不能据单独通过就把全量失败抹掉。应在最终全量验收时复核测试是否按 requestId／revision／detail 就绪条件等待，而非假定“最后一条消息”就是请求结果。本次不修改无关 Activity 产品代码或放宽断言。

四项 smoke 跳过与前次一致；没有运行真实 provider、WebView2 手动 smoke 或 Vue 测试。

### 7.4 可复核命令与复现位置

环境及包版本沿用本报告前文。结果、targets 和新增临时探针保存在 `.scratch/direct-message-input/p1-rereview/`；探针名为 `DirectP1RereviewPersistenceProbes.Unknown_total_survives_runtime_recorder_and_sqlite`（两项）。该目录被忽略，以上表格和输入／输出是持久证据摘要。

```powershell
$env:DOTNET_CLI_HOME='D:\Repositories\SelfClaw\.dotnet'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'
$env:DOTNET_NOLOGO='1'
dotnet test SelfClaw.Tests/SelfClaw.Tests.csproj --no-restore --filter 'FullyQualifiedName~RequestUsageAccumulatorTests|FullyQualifiedName~DirectConversationLoop|FullyQualifiedName~ProtocolBaselineProbeTests|FullyQualifiedName~InputBoundaryBarrierBaselineTests|FullyQualifiedName~DirectToolInvoker' --logger 'trx;LogFileName=p1-rereview-targeted.trx' --results-directory .scratch/direct-message-input/p1-rereview
dotnet test SelfClaw.slnx --no-restore --logger 'trx;LogFileName=p1-rereview-full.trx' --results-directory .scratch/direct-message-input/p1-rereview
dotnet test SelfClaw.Tests/SelfClaw.Tests.csproj --no-build --no-restore --filter 'FullyQualifiedName~Content_page_checks_scope_selection_version_and_serialized_budget|FullyQualifiedName~Timeout_keeps_partial_content_and_terminalizes_the_task_and_open_tool' --logger 'trx;LogFileName=p1-rereview-activity-isolated.trx' --results-directory .scratch/direct-message-input/p1-rereview
dotnet test SelfClaw.Tests/SelfClaw.Tests.csproj --no-restore -p:CustomAfterMicrosoftCommonTargets=D:\Repositories\SelfClaw\.scratch\direct-message-input\p1-rereview\persistence-probes.targets --filter 'FullyQualifiedName~DirectP1RereviewPersistenceProbes' --logger 'trx;LogFileName=p1-rereview-persistence.trx' --results-directory .scratch/direct-message-input/p1-rereview
dotnet build SelfClaw.Tests/SelfClaw.Tests.csproj --no-restore
```

最后恢复普通 build，去掉临时测试 Compile 注入。此次未修改生产代码或正式测试，只更新审核文档和阶段状态。结论：关闭 IR01／IH01／IH02，保留 IR02 作为 P1 待修复复审项。

## 8. 再次提交后的核验（2026-10-02）

用户再次通知修复完成后，检查当前工作区并运行正式回归。结论：IR02 仍未修复，当前新增的是复现测试，尚未看到修复该语义断层的生产代码变更。

本次 `git diff --name-only -- SelfClaw.Core SelfClaw.Desktop` 无输出；Core `TurnUsageAccumulator.Build()` 仍在 total 为 null 时用 input + output 推导，recorder 仍再次调用该累加器。新增 `DirectUsagePersistenceTests` 覆盖 Interactive、child 和 continuation 三条链路，测试方向正确，但未知 total 场景均失败。

```powershell
$env:DOTNET_CLI_HOME='D:\Repositories\SelfClaw\.dotnet'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'
$env:DOTNET_NOLOGO='1'
dotnet test SelfClaw.Tests/SelfClaw.Tests.csproj --no-restore --filter 'FullyQualifiedName~DirectUsagePersistenceTests|FullyQualifiedName~TurnUsageAccumulatorTests|FullyQualifiedName~RequestUsageAccumulatorTests' --logger 'trx;LogFileName=ir02-final-review-targeted.trx' --results-directory .scratch/direct-message-input/ir02-final-review
```

结果：15 通过、9 失败、0 跳过。

- Core 新增三项断言失败：应为未知的 total 被算成 7，或只保留已知贡献的 12。
- 三条 Direct 消费链各有两项失败，共六项：`incremental-unknown` 变成 7，`cumulative-partial-error` 变成 19。
- 完整 total=17 的三条链路通过，说明新增测试能区分完整与不完整用量，而不是全部运行环境失败。

此次未注入临时测试、未修改生产或正式测试代码。相关用例已明确失败，因此没有继续重复全量与 Activity 检查，也没有将 IR02 或 P1 标为通过。应先确认生产修复已保存到当前工作区，再按 §7.2 的端到端契约完成修复和验证。

## 9. 按用户要求直接修复 IR02（2026-10-02）

用户明确要求直接修复后，原 reviewer 接手实现。详细代码职责、命令及证据见 [实施报告 §8](direct-message-input-p1-implementation-review.md)。

- Core 累加器只合并已经归一化的 total，未知值具有传递性，删除 input/output 回退；Direct translator 的局部补偿也已删除。
- 三种 CLI parser 在生成事件前完成 total 推导，保持正常 CLI 累计；没有给 recorder 或数据库增加特殊分支。
- 九项正式 Direct 持久化用例覆盖 Interactive／child／continuation，各自验证未知 SQL NULL 及完整 17。九项新增 CLI 归一化用例和既有 Core 用例通过。
- 两项 Activity 测试改为按 requestId 匹配响应并确认在途 state，不再用消息到达时序代替协议条件；没有修改 Activity 产品代码或放宽断言。

定向测试 94 通过；最终 .NET 全量 1329 通过、0 失败、4 项门控跳过，包括之前失败的两项 Activity 用例。结果存于 `.scratch/direct-message-input/ir02-fix/`，持久摘要与 SHA-256 见实施报告。

IR02 已修复验证，P1 已发现问题全部关闭。此结论是本次实现与验证结果；此前独立审核的失败记录保留，不覆盖历史。未执行真实 provider／桌面手动 smoke，没有清理用户数据或实施 P2。
