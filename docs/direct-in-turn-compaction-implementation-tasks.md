# Direct 回合内上下文压缩 —— 分步实施任务文档

> 配套设计：`docs/direct-in-turn-compaction-design.md`（v1 设计冻结，2026-10-18 批准，决策 A–E 按建议落定）。
> 引用格式：D§n 指设计文档章节；文件路径均相对仓根。设计文档的 I1–I8 在本文件收拢为 T1–T4（映射见任务总览）。

## 使用约定

- **完成定义（每个任务通用 DoD）**：`dotnet build SelfClaw.slnx` 零新增警告；任务相关的定向 `dotnet test` 全绿（全量测试在报告里给出通过/失败/跳过数）。本特性零前端改动；若意外触及前端，另跑 `npm test` 与 `npm run build`。
- **提交粒度**：一个任务一次提交（时机由人工决定）；实施代理**不得** `git commit` / `git add`，改动留在工作区；不得切换分支（当前 `feat/plugin-system-baseline`）。
- **文件边界**：T1–T3 只改本文件列出的源码与测试文件；`docs/` 与 `.scratch/` **只读**；T4 按其自身清单修改文档。
- **构建隔离**：若遇到文件锁错误（MSB3027/MSB3021，通常来自运行中的 Desktop 应用），改用 `-p:BaseOutputPath=bin/pi-itc-<任务号>/` 重跑，不要杀进程；**不要并发跑两个 dotnet 构建**。
- 标注 ⚠ 的是设计核对中确认的易错点，实施时优先核对；发现任务书与代码或设计冲突时**停下并在报告里说明**，不要自行扩大范围。

## 决策现状（2026-10-18，随方案一并批准）

| # | 决策 | 值 |
| --- | --- | --- |
| A | 压缩后仍超窗 | 可读错误（回合 Failed；消息与转录保留） |
| B | 回合内压缩上限 | `MaximumInTurnCompactionsPerTurn = 3`，失败尝试计入 |
| C | 可见性 | 活动文案 + 状态栏读数；不写转录、不落库 |
| D | 触发水位 | 复用回合开始 threshold |
| E | 完成文案 | 不新增 |

## 实施进度（由 pi 实施、Claude 审核）

| 任务 | 状态 | 审核结论 |
| --- | --- | --- |
| T1 单元模型与结构化构建 | 已完成 | 通过（我独立复跑 `bin/review-itc-t1/` 两次均 0 警告 0 错误、Direct 425 通过 / 0 失败 / 0 跳过；既有测试零修改，7 个文件改动全部在任务书范围内。审核裁定并修正一处接口：`ApplyInTurnCompaction` 入参与 `InTurnSummary` 统一为**裸摘要**口径，包裹只发生在合成 system 文本时（避免 T2 链式压缩嵌套外壳）。pi 报告的四点偏离（`DirectPromptBuild` 独立成文件、`CreateSummarySection`、`Compose` 装配入口、`coveredUnitCount` 范围校验）经审核全部接受） |
| T2 回合内压缩执行 | 已完成 | 通过（我独立复跑 `bin/review-itc-t2/`：0 警告 0 错误、Direct 439 通过 / 0 失败 / 0 跳过、Compaction 42 通过 / 0 失败；D§11 十一条逐条核对用例映射无误——检查点位于 steer 排空之前、判定顺序与规范一致、尝试计数挂在每回合局部对象（loop 单例安全）、链式压缩传裸摘要（用例断言压缩器只包一层）、超窗可读错误不泄正文。审核裁定：① 既有 `DirectCompactionRuntimeTests` 两处断言的最小适配接受（旧断言编码的是"回合内不压缩"，行为已按设计改变；适配保留回合开始路径覆盖）；② 规划被拒时每迭代回填单元 Tokens 接受现状（开销受 keepTarget 规模与消息缓存双重约束，有界）；③ 阈值低于 system+工具+预留时的立即再压缩由 3 次上限兜底，接受；④ 取消路径活动文案残留属既有模式，接受。其余偏离（CreateRuntime 工厂签名、`PublishRunStatus` 带 detail、`RecordAttempt` 位置、`PublishInputBoundary` 走共享 `Write`）均机械/等价，接受） |
| T3 录制分支 | 已完成 | 通过（我独立复跑 `bin/review-itc-t3/`：0 警告 0 错误、Recorder 24 通过 / 0 失败 / 0 跳过、Direct 439 通过；审核确认 recorder 分支仅 `LogInformation`、零状态变更（不写 `PendingCompactions`、不调 `ReplaceCompaction`、不动活动文案、不触发 `TranscriptChanged`）；新增用例 `In_turn_compaction_completion_is_observation_only` 覆盖"转录不动 + 终态提交不落库"并对任务书三项断言做了加强；日志额外附加 `TurnId` 字段为增强，接受。pi 提出的"用例捕获日志正文"建议不采纳——fixture 为 NullLogger 无需为此扩范围，且日志调用点本身不传摘要正文） |
| T4 文档同步与全量回归 | 已完成 | 通过（审核方逐项核对：提升的两份 docs 文档与 .scratch 原文 diff 仅声明处（状态行、指针、§4.3、§10）；父设计文档四处修订、`runtime-execution-flow.md` 两处、`AGENTS.md` 深入阅读均按任务书落地；`rg` 清理符合基准（`MaximumInTurnCompactionsPerTurn` 仅 loop 定义 + 测试引用；`ThresholdRatio` 仅 composer 单一公式）；`git diff --check` 干净。审核方补正：提升版设计文档残留的「待确认/推荐采纳/决策点」措辞归一为定稿表述，任务书手工验收清单由草案替换为最终 7 项；提升任务书仅改 1 行配套设计指针（避免死链），接受。全量回归：pi 复跑全绿（1560 通过 / 0 失败 / 4 跳过）；审核方独立全量复跑共三次——前两次各命中一个既有并发抖动（`ConversationInputReviewRegressionTests.Concurrent_acceptance_enforces_20_and_utf8_64KiB`，SQLite 事务竞态，隔离复跑 3/3 通过），第三次全绿；对照实验：HEAD 干净工作树全量首跑同样失败（既有 TickerQ 抖动）、复跑通过——判定为满负载下的既有 flaky，与本特性无关） |
| F1 摘要调用改流式（实机缺陷） | 已完成 | 通过（用户实机报错根因：端点对摘要的**非流式**请求返回 `choices: []` 畸形响应，OpenAI .NET SDK `ChatCompletion.Role` 无保护取 `Choices[0]` → `ArgumentOutOfRangeException`；正常对话回合走流式均正常。修复：`DirectContextCompressor` 摘要改 `GetStreamingResponseAsync + ToChatResponseAsync` 流式聚合（与主循环一致），空流/失败/取消语义不变；三个测试替身的非流式路径改为 `NotSupportedException` 杜绝回归，新增 2 条用例（空流降级不泄正文、分段聚合与单段一致）。我独立复跑 `bin/review-f1/`：0 警告 0 错误、Compaction 43 通过 / Direct 441 通过 / 0 失败。用户实机复测确认：压缩成功落库（120.3K→1.6K tokens）、转录分隔项可见、无失败告警） |

全部改动（T1–T4 + F1）已提交：4e2a0ff（22 文件）。

## 任务总览

| ID | 任务 | 依赖 | 规模 | 设计映射 |
| --- | --- | --- | --- | --- |
| T1 | 单元模型与结构化构建（行为零变化） | — | L | I1、I2（含上下文用例） |
| T2 | 回合内压缩执行（检查点/规划/上限/事件） | T1 | L | I3、I4、I5（Core 事件）、I6、I7 |
| T3 | 录制分支 | T2 | S | I5（录制侧） |
| T4 | 文档同步 + 清理 + 全量回归 | T1–T3 | M | I8 |

---

## T1 单元模型与结构化构建（行为零变化）

**目标**：把「回合内消息 = 扁平 `List<ChatMessage>`」升级为「system 文本 + 有序单元 + 受保护消息」三区结构（D§4），并让 runtime 一次性构建结构化载荷传给 loop。**本任务不接线任何压缩触发**：回合行为与既有测试断言逐条不变，既有用例允许新增、不允许修改语义。

**改动文件**

- `SelfClaw.Infrastructure/Agents/Direct/Context/DirectConversationContext.cs`（重构）
- `SelfClaw.Infrastructure/Agents/Direct/Context/DirectPromptComposer.cs`（抽出结构化构建）
- `SelfClaw.Infrastructure/Agents/Direct/Models/DirectTurnSetup.cs`（载荷扩展）
- `SelfClaw.Infrastructure/Agents/Direct/DirectAgentChatRuntime.cs`（SetupTurnAsync 组装）
- `SelfClaw.Infrastructure/Agents/Direct/DirectConversationLoop.cs`（仅 context 构造行）
- `SelfClaw.Tests/Infrastructure/Agents/Direct/Context/DirectConversationContextTests.cs`（新增）
- 测试中直接构造 `DirectTurnSetup` / `DirectConversationContext` 的位置（机械更新）

**步骤**

1. `DirectConversationContext` 新形态：
   - 构造：`(string? systemText, IReadOnlyList<DirectPromptHistoryUnit> initialUnits, IReadOnlyList<ChatMessage> trailingProtectedMessages)`。内部维护有序 entry 列表（每条 entry 是「单元」或「受保护消息」二选一）；`Messages` 按 [system?] + entries 依次展开合成，顺序必须与改造前 `BuildMessages` 的输出逐条一致。
   - `AppendResponse`：把响应的全部消息开成一个**新单元**（`SourceSequence` 用合成值，见第 4 条）；`AppendResults`：把工具结果消息并入这个未完成的单元（响应 + 结果永不分离，D§4.1）。既有 `_callIds` 校验逻辑原样保留。
   - `AppendInputs`：改为追加**受保护** entry（存 `ChatMessage(ChatRole.User, message.MarkdownContent)`；不需要保留 `MessageRecord` 引用——当前无读者）。
   - 受保护消息（构造传入的续写提示 / completion batch、以及 steer）永不进入压缩前缀；应用压缩时原位保留。
   - 新增 `ApplyInTurnCompaction(string summary, int coveredUnitCount)`：移除**最旧的** `coveredUnitCount` 个单元（按 entry 顺序数单元）；把**裸摘要文本**记为本回合内摘要（**替换**旧的，不叠加）；合成 system 文本时用 `DirectPromptComposer.CreateSummarySection` 包裹裸摘要并追加在**原文本末尾**（段间 `\n\n`）；重计数（重置 `_countedMessageCount` 与 `_countedTokens`，下次 `CountTokens` 全量重建）。本任务只实现与单测，不接线。
   - 新增只读成员：`BuildUnits()`（当前单元列表，按序）、`InTurnSummary`（当前回合内**裸摘要文本**或 null，供 T2 作为 `PreviousSummary` 链式压缩用；包裹成 section 只发生在合成 system 文本时）。
   - 增量计数逻辑保持现状，作用于合成后的 `Messages`；每次结构变更后重建合成列表实例。
2. `DirectPromptComposer` 抽出结构化构建：`internal DirectPromptBuild BuildRequest(...)`（或等价命名），返回 system 文本、重放单元（`DirectPromptHistoryUnit`）、受保护尾部消息（续写提示、completion batch，顺序与现状一致：单元 → 续写提示 → completion batch）。`BuildMessages` **保留原签名**，改为基于 BuildRequest 的扁平化包装；`CreateCompressionSection` 改为 `internal static`（T2 复用，单一格式来源）。⚠ 续写提示的判定逻辑（最后一条可重放消息、Truncated 状态）抽成内部辅助，两处共用，不得复制。
3. `DirectTurnSetup` 载荷：以 `SystemText`、`InitialUnits`、`TrailingProtectedMessages` **替换** `Messages` 字段；新增 `CompressionPolicy`（`DirectCompressionPolicy`）、`Budget`（`DirectPromptBudget`）、`ConversationId`、`TurnId`。⚠ 同步全部构造点（生产 1 处 + 测试）。
4. ⚠ **单元序列的合成规则**：初始单元沿用真实 `SourceSequence`；回合内新单元的合成 sequence 从「当前单元最大 sequence + 1」起递增，**必须 > 0**（`PlanCompression` 用 `SourceSequence > 0` 过滤候选，0 会被全部滤掉、回合内压缩永远不触发——T2 的测试要能挡住这个坑）。
5. `DirectAgentChatRuntime.SetupTurnAsync`：单元**只构建一次**——计数、`PlanCompression`、`BuildRequest` 共用同一份单元（消除现状中 `CountPromptTokens` 与 `BuildMessages` 内部各建一次的双重构建，AGENTS 约定 10）。回合开始压缩的行为、事件、错误路径与现状逐条一致：`ApplyCompactionAsync` 的判定与产物不变；组装 setup 时按**最终边界**（本次新压缩或既有 `request.Compression`）过滤出重放单元（`SourceSequence > ThroughSequence`）传入。loop 侧仅把 `new DirectConversationContext(setup.Messages)` 改为结构化构造，其余不动。
6. 本任务**不得**改：`DirectConversationLoop` 的循环体、事件、预算逻辑；`ConversationTurnRecorder`；任何 Core 类型。

**测试**（新增 `DirectConversationContextTests`）

- 合成顺序：无 system / 有 system、单元与受保护交错时，`Messages` 展开顺序与加入顺序一致。
- 单元归组：`AppendResponse` + `AppendResults` 合成一个单元（单元内消息连续、结果在响应之后）。
- 受保护：`AppendInputs` 的 steer 为受保护 entry；`ApplyInTurnCompaction` 后受保护消息仍在原位、其之前之后单元的相对顺序不变。
- 应用压缩：移除最旧 N 个单元；system 文本末尾出现 `CreateSummarySection(裸摘要)`；`InTurnSummary` 返回裸摘要本身；再次应用时旧段被替换而非叠加；计数重置后 `CountTokens` 结果与逐条重算一致。
- 单元 sequence：初始单元保留真实值；新增单元从 max+1 递增且恒 > 0。

**验收**：`dotnet build` 0 警告；`dotnet test --filter Direct` 全绿；**既有测试零修改**（只允许新增文件与机械构造点适配）。

---

## T2 回合内压缩执行（检查点/规划/上限/事件）

**目标**：在 loop 检查点接入回合内压缩（D§3–§6）。行为任务：测试必须以「下一请求实际收到的载荷」为证据。

**改动文件**

- `SelfClaw.Core/Runtime/Agent/InTurnContextCompactionCompletedEvent.cs`（新，DTO，一文件一类型）
- `SelfClaw.Infrastructure/Agents/Direct/DirectEventTranslator.cs`（新增发布方法）
- `SelfClaw.Infrastructure/Agents/Direct/DirectConversationLoop.cs`（检查点 + 压缩执行）
- `SelfClaw.Infrastructure/Agents/Direct/Context/DirectPromptComposer.cs`（阈值单一真相源）
- `SelfClaw.Infrastructure/Agents/Direct/DirectAgentChatRuntime.cs`（两个诊断常量改 internal；策略获取上移）
- `SelfClaw.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs`（loop 注册补依赖）
- `SelfClaw.Tests/Infrastructure/Agents/Direct/DirectInTurnCompactionTests.cs`（新增）

**步骤**

1. Core 事件形状：`public sealed record InTurnContextCompactionCompletedEvent(int CoveredUnitCount, int? SourceTokens, int? SummaryTokens, string? Model) : AgentStreamEvent;`（注释写明：仅观测用途，不落库、不进转录）。
2. `DirectEventTranslator` 新增（模式对齐既有 `PublishInputBoundary`：消费端已关闭时抛 `InvalidOperationException`）：`PublishRunStatus(AgentRunStatus status)`、`PublishInTurnCompactionStarted(int estimatedTokens, int thresholdTokens)`（复用既有 `ContextCompactionStartedEvent`）、`PublishInTurnCompactionCompleted(InTurnContextCompactionCompletedEvent evt)`（或直接内联写）、`PublishInTurnCompactionFailed(string reason)`（复用既有 `ContextCompactionFailedEvent`）。
3. loop 检查点（D§3.1）：循环顶部、`ConsumeAvailableInputsAsync` **之前**、`requestIndex > 0` 时调用 `TryCompactInTurnAsync`。⚠ 位置必须先于 steer 排空：压缩释放的计数额度要能让原本 `FitsBudget` 失败的批次被接受；`calls.Length == 0` 的 steer 续跑经 `continue` 回到循环顶，天然覆盖，不要加第二检查点。
4. `TryCompactInTurnAsync` 判定顺序（每一步短路返回，⚠ 顺序即语义）：
   1. `setup.CompressionPolicy.Enabled == false` → 返回（不计数）；
   2. 上限：`_inTurnCompactionAttempts >= MaximumInTurnCompactionsPerTurn` → 返回；
   3. 计数：`context.CountTokens(_tokenCounter, profile)` 或 `_tokenCounter.CountTools(options.Tools, profile)` 任一为 null → 返回（D§5.5 fail-open）；`total = used + toolTokens + reserve`（`reserve = options.MaxOutputTokens ?? DirectPromptComposer.DefaultOutputTokenReserve`）；
   4. 阈值：在 `DirectPromptComposer` 增加 `internal static int ResolveThreshold(DirectCompressionPolicy policy, DirectPromptBudget budget)`（窗口 × `ThresholdRatio`，无窗口用 `AbsoluteThresholdTokens`），`PlanCompression` 改为调用它；loop 调同一方法——⚠ 不得在两处各写一份公式（C7 已确立单一真相源原则）；`total < threshold` → 返回；
   5. 规划：先对每个单元回填 `Tokens`（`CountMessages`，任一为 null → 放弃本次）；`plan = _promptComposer.PlanCompression(context.BuildUnits(), existing: null, policy, setup.Budget, used + toolTokens)`；`!plan.ShouldCompress` → 返回（不算尝试）；
   6. 事件与压缩调用：`PublishInTurnCompactionStarted(used + toolTokens, plan.ThresholdTokens)` + `PublishRunStatus(Compacting)`（顺序同回合开始）→ `_contextCompressor.TryCompressAsync(new DirectCompressionRequest(plan.PrefixUnits, context.InTurnSummary, plan.SourceTokens ?? (used + toolTokens), policy.ModelProfileId, setup.ProviderLease, setup.ConversationId, setup.TurnId), ct)`（⚠ `PreviousSummary` 传**裸摘要**：`InTurnSummary` 已是裸文本，压缩器会自行包 `selfclaw-context-summary` 外壳，切勿先包一层）；
   7. 失败（null）：`_inTurnCompactionAttempts++`；`PublishInTurnCompactionFailed(DirectAgentChatRuntime.CompactionFailedReason)`（常量改 `internal` 复用，不复制字符串）；`PublishRunStatus(Requesting)`；返回（未压缩继续，绝不移除内容）；
   8. 成功：`context.ApplyInTurnCompaction(result.Summary, plan.PrefixUnits.Count)`（裸摘要入参；context 合成 system 文本时自行包裹）；`result.UsedFallbackModel` → `PublishRunStatus(Compacting)` + `CompactionFallbackDiagnostic`；`PublishInTurnCompactionCompleted(new(...))`；`_inTurnCompactionAttempts++`；重计数（`context.CountTokens`，null 则跳过窗口检查）后若窗口非空且 `used' + toolTokens' + reserve > window` → 抛可读 `InvalidDataException`（决策 A；文案与 `EnsurePromptFitsWindow` 同族、说明「回合内压缩后仍超窗」，含 token 数与窗口数、不含正文）；否则 `PublishRunStatus(Requesting)`；返回。
   - 取消：`OperationCanceledException` 必须重抛（不发 Failed）。
   - 压缩调用**不计入** `MaximumProviderRequestsPerTurn`。
   - ⚠ 事件序收尾固定：任何发出过 `Started` 的路径，其后必须以 `Completed`、`Failed` 或抛错三者之一收束，且成功/失败后都要用 `RunStatus(Requesting)` 覆盖活动文案「正在压缩上下文…」，与回合开始流程（`DirectAgentChatRuntime.ApplyCompactionAsync`）逐条对照实现。
5. 常量与依赖：loop 增 `internal const int MaximumInTurnCompactionsPerTurn = 3;` 与实例字段 `_inTurnCompactionAttempts`（每回合新 loop 实例？⚠ `DirectConversationLoop` 是 **DI 单例**——计数字段必须挂在每回合对象上，不得用实例字段累积：改为局部变量传入检查点，或在 setup 上挂状态。**推荐**：把计数放进 loop 内每回合新建的小对象或以参数贯穿 `RunAsync` 局部变量）；构造注入 `DirectPromptComposer`、`DirectContextCompressor`；`ServiceCollectionExtensions` 的 loop 注册同步补参；`DirectAgentChatRuntime.ApplyCompactionAsync` 的策略获取上移到 `SetupTurnAsync`（只读一次），随 setup 传递。若 `TryCompactInTurnAsync` 超过 40–50 行，按 AGENTS 约定 4 拆私有方法。
6. 与既有逻辑的共用：`FitsBudget` 与检查点的「上下文 + 工具 + 预留」计数组合抽成一个私有辅助（可返回 null），两处共用，避免各写一份。

**测试**（新增 `DirectInTurnCompactionTests`，D§11 全表；复用 `DirectAgentChatRuntimeTests` 的 FakeChatClientFactory / TurnClient / FixedPolicySource / FixedCapabilityResolver 与 `DirectCompactionRuntimeTests` 的断言写法）

1. 跨阈值增长 → 恰好一次回合内压缩：事件序 `Started → InTurn…Completed → RunStatus(Requesting)`，下一请求载荷含摘要 section、前缀消息消失、保留尾部仍在。
2. 未达阈值 → 无压缩调用（`client.SummaryCalls` 为空）、无事件。
3. 保留规则：最新单元累计 ≤ 阈值×50%，至少保留 1 个。
4. 工具调用与结果同进同出（前缀边界落在往返组之间，不拆对）。
5. 上限：持续增长最多 3 次压缩调用，之后请求照常发送、无新事件。
6. 压缩失败 → `Failed` 事件、下一请求仍含完整前缀、回合继续。
7. 计数不可用 / `policy.Enabled == false` → 零行为变化（无计数驱动的分支、无事件）。
8. 受保护内容：续写提示、completion batch（Continuation origin）、已消费 steer 在压缩后的下一请求中仍在原位。
9. steer 交互：接近窗口时压缩后批次被接受（不再 `ContextBudgetExceeded`）；仍不满足时 Hold + `ContextBudgetExceeded` 行为不变。
10. 压缩后仍超窗 → 终态 Failed 且错误文案可读（含窗口数与指引、不含正文）。
11. 链路复用：回合开始压缩的既有用例（`DirectCompactionRuntimeTests`）全绿不动——本特性不改变回合开始判定。

**验收**：`dotnet test --filter Direct` 全绿；D§11 十一条全部落地为真实用例（报告里逐条映射用例名）。

---

## T3 录制分支

**目标**：`InTurnContextCompactionCompletedEvent` 在录制侧只做观测，不产生任何持久化状态（D§5.3、决策 C）。

**改动文件**

- `SelfClaw.Desktop/Services/Runtime/ConversationTurnRecorder.cs`：`ApplyEventCoreAsync` 增加分支：仅 `_logger.LogInformation`（含 `CoveredUnitCount` / `SourceTokens` / `SummaryTokens` / `Model`；**不含摘要正文**）；⚠ 不写 `PendingCompactions`、不调 `ReplaceCompaction`、不改 `ActivityText`、不 `RaiseTranscriptChanged`（其后的 `RunStatus(Requesting)` 与转录增量照常）。
- `SelfClaw.Tests/Desktop/Services/Runtime/ConversationTurnRecorderTests.cs`（或既有测试文件所在路径）：增补一条——投递该事件后 `PendingCompactions` 为空、`session.Compactions` 不变、活动文案不变。

**验收**：`dotnet test --filter Recorder` 与 `--filter Direct` 全绿。

---

## T4 文档同步 + 清理 + 全量回归

**目标**：文档与实现一致、无残留；本任务允许修改文档。

**步骤**

1. 提升设计：`.scratch/in-turn-compaction/design.md` → `docs/direct-in-turn-compaction-design.md`。改动仅限：状态行改「设计冻结（v1）· 已完成（T1–T4）」；文首补「配套实施文档：direct-in-turn-compaction-implementation-tasks.md」；把 §10「决策点（待确认）」改为「已确认决策」块（值为 A 可读错误 / B 上限 3 失败计入 / C 活动文案与状态栏、不落库 / D 复用 threshold / E 不加完成文案）；§4.3 把 steer「保留 MessageRecord 引用」一句按最终实现改准确（实现只保留 ChatMessage）。技术结论一字不改。
2. 提升任务书：`.scratch/in-turn-compaction/tasks.md` → `docs/direct-in-turn-compaction-implementation-tasks.md`，原样提升（进度表保持现状，最终审核结论由审核方补写）。
3. 修订 `docs/direct-context-compaction-design.md`（四处，均加指向新文档的引用）：
   - §1 表格 `DirectConversationLoop.cs` 行与「关键事实」段：改为「回合内增长已由回合内压缩约束（见 direct-in-turn-compaction-design.md），回合开始压缩不变」；
   - §2.2 非目标删除「回合内压缩」条目，替换为一句：回合内压缩已另立设计（指向新文档）；
   - §10.1 C06 改为：「每回合开始最多一次压缩（不变）；回合内维护性压缩 ≤ `MaximumInTurnCompactionsPerTurn`（3 次），不落库、不裁剪（见 direct-in-turn-compaction-design.md）」；
   - §11 风险表「回合内无压缩」行改为已解决并指向新文档。
4. `docs/runtime-execution-flow.md`：在 Direct 顺序与「tokenizer 计数驱动压缩」相关段落（用 `rg 压缩` 定位）补一句回合内检查点（循环顶部、steer 排空之前，同阈值）；不得改动同段其它结论。
5. `AGENTS.md`「深入阅读」子系统条目补 `direct-in-turn-compaction-design.md`。
6. 清理核对（报告命令与结果）：`rg -n "MaximumInTurnCompactionsPerTurn"` 仅常量定义与引用；loop 中无第二份阈值公式（`rg -n "ThresholdRatio"` 仅 composer/设置层）；无新增死代码。
7. 全量回归：`dotnet build SelfClaw.slnx`（0 警告）与 `dotnet test SelfClaw.Tests/SelfClaw.Tests.csproj`（全量，用 `-p:BaseOutputPath=bin/pi-itc-t4/`），报告通过/失败/跳过数；`git diff --check`。
8. 报告里附「手工验收清单」的最终版（见下节，按实际实现修订措辞）。

**验收**：文档与实现一致；全量测试全绿；无新增警告。

---

## 手工验收清单

1. **可见性**：调低绝对阈值，跑一个多工具轮次 Direct 会话 → 活动区出现「正在压缩上下文...」（`ContextCompactionStartedEvent` → `RunStatus(Compacting)`），压缩后由 `RunStatus(Requesting)` 覆盖；随后状态栏「上下文」读数下降（`last_request_input_tokens` 取最新）；转录**不**出现分隔项。
2. **连续性**：压缩后继续提问，此前工具结果的关键结论仍被引用（摘要可用）。
3. **不落库**：重启后该回合无任何压缩痕迹（`conversation_compactions` 无新行、转录无项）；下一回合由回合开始压缩按既有方式落库。
4. **steer 净增益**：接近窗口的长回合发送 steer → 批次被接受而非 `ContextBudgetExceeded`（检查点在 steer 排空之前）。
5. **关闭压缩**：设置里关闭 → 回合内不压缩、不发任何回合内事件；回合开始仍按既有逻辑处理（已超窗则给既有可读错误「开启压缩 / 降低输出上限 / 换更大窗口」，不回退字节估算）；回合内增长超窗交给 provider 报错，失败路径与旧版一致。
6. **上限兜底**：极端长回合达到 3 次（含失败尝试）后不再有新的 Started/Completed/Failed，请求照常发送，无卡顿。
7. **决策 A 路径**：受保护内容/工具定义本身超窗时，回合以可读错误 Failed（文案含 required/window token 数、不含正文），消息与转录保留。
