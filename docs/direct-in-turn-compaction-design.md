# Direct 回合内上下文压缩设计

状态：设计冻结（v1）· 已完成（T1–T4）· 修订 2026-11-24（C23：回合内压缩落库，决策 C 与不变量 T01 改写，§7 备选 A 落地为锚点列方案）
配套实施文档：《direct-in-turn-compaction-implementation-tasks.md》
关系：修订 `docs/direct-context-compaction-design.md` 的 §1 现状表、§2.2 非目标与不变量 C06；复用其策略、阈值、保留比例、压缩器与降级语义。

已确认决策（2026-10-18，用户批准方案）：

- **决策 A**：压缩后仍超窗 → 可读错误（回合 Failed，消息与转录保留）。
- **决策 B**：`DirectCompactionStep.MaximumInTurnCompactionsPerTurn`（设计时定为 3，2026-10 修订为 10，见 §3「上限与终止」），失败尝试计入。
- **决策 C**（2026-11-24 改写）：回合内压缩**随回合终态落库**，转录在该回合用户消息之后显示一条 `上下文压缩` 分隔项。原决策是“不写转录、不落库”，改写的原因见 §5.4。
- **决策 D**：触发水位复用回合开始的触发点。
- **决策 E**：不新增完成文案。

实施任务书：`docs/direct-in-turn-compaction-implementation-tasks.md`（T1–T4）。

## 0. 结论摘要

| 问题 | 现状（代码事实） | 本方案 |
| --- | --- | --- |
| 回合内增长 | 工具循环期间上下文只增不减；超窗时由 provider 报错终结回合 | 在工具循环的**回合间隙**检查同一触发点；到达即把内存中最早的若干单元交给压缩模型，摘要替换请求里唯一的摘要 section |
| 持久化 | 回合开始压缩：摘要落库（`conversation_compactions`），边界 `ThroughSequence` 供后续回合重放 | 同一张表、同一条提交路径：一个回合一条回合内记录，边界 = `ThroughSequence` + 工具调用锚点 `ThroughToolRunId`（schema v33）。下一回合从摘要 + 锚点之后的原文出发，不再重放整条 assistant 消息 |
| steer 准入 | 超预算的批次直接 Hold 并以 `ContextBudgetExceeded` 终结回合 | 检查点位于 steer 排空之前，压缩释放的额度可让批次被接受（净增益）；仍不满足则行为不变 |
| 失败语义 | —— | 压缩失败 / 计数不可用 / 上限用尽 → 回到现状（继续发送，provider 决定），绝不裁剪；唯一例外见 §6 决策 A |

一句话：**回合开始压缩解决"新回合带着超长历史出发"；回合内压缩解决"回合自己把窗口撑爆"——同一套触发点与摘要器、同一个摘要 section、同一条落库路径，区别只在边界从哪里来。**

## 1. 现状盘点（与本方案相关的代码事实）

| 位置 | 事实 |
| --- | --- |
| `DirectConversationLoop.cs`（现 `DirectTurnRun.cs`） | 设计时每回合 ≤128 次 provider 请求（现 `DirectTurnRun.MaximumProviderRequestsPerTurn` = 1024）；每次迭代 = 排空 steer → 请求 → 追加响应 → 执行工具 → 追加结果 |
| `DirectConversationLoop.cs` `FitsBudget`（现 `DirectTurnRun.FitsBudget`） | 只用于 steer 批次准入（`used + tools + reserve ≤ window`）；超预算 → `HoldUnconsumedAsync` + `ContextBudgetExceeded` 终结 |
| `DirectConversationContext.cs` | 扁平 `List<ChatMessage>` + 增量计数（`_countedMessageCount/_countedTokens`）；回合内消息**没有单元结构**；`AppendInputs` 丢弃 steer 的 `MessageRecord`（只保留文本） |
| `DirectAgentChatRuntime.cs` `ApplyCompactionAsync`（现 `DirectCompactionStep.CompactTurnStartAsync`） | 回合开始压缩只发生一次：计数 → `PlanCompression` → 压缩 → `ContextCompactionCompletedEvent`（录制侧进 `PendingCompactions` → 回合终态原子落库） |
| `DirectPromptComposer.cs` `PlanCompression`（现 `DirectCompactionPlanner.PlanCompression`） | 设计时：threshold = 窗口×比例（无窗口用绝对阈值）；reserve 在触发侧；保留 = 阈值×50%，至少最新 1 个单元；前缀为空则拒绝。C23 起触发点与保留目标的公式见 §3.2 / §3.3 |
| `DirectContextCompressor.cs` | `TryCompressAsync`：单次调用、内建"指定模型不可用 → 回退本回合 lease"；失败返回 null |
| `ConversationTurnRecorder.cs` | 消息 sequence 在**录制侧**保留（`ReserveMessageSequenceAsync`，首个 delta 即分配）；产出侧（Infrastructure）不知道本回合消息的 sequence |
| `TurnUsageAccumulator.cs` | `last_request_input_tokens` 取最新、不求和 → 压缩后下一请求的输入量下降，输入框状态栏即时可见 |
| `direct-message-input-design.md` §相关行 | "已经提交的 steer 在本回合内作为受保护输入，不能在之后迭代中被静默裁掉" |
| 增长来源上限 | MCP 结果序列化 ≤64 KiB（`McpToolResultFormatter`）；Shell 输出 ≤24k 字符（`ProcessRunner.MaxOutputCharacters`）；文件读取 >1 MB 直接拒绝（`WorkspaceFileAccess.MaxFileBytes`）；Skill 资源 ≤1 MB；搜索单行按匹配点开窗（≤1000 字符）且整次结果 ≤32 KiB（`WorkspaceSearchService`，超限时 `SizeLimitReached` / `MatchLimitReached` 显式告知）；工具结果文本统一由 `ToolResultTextLimits.Limit`（64 KiB 字符）在**工具边界**（`DirectToolInvoker`）就先截断——同一函数再由 `ConversationTurnRecorder` 作用于 CLI 结果。回合内内存上下文与落库/重放因此口径一致，状态栏显示的上下文尺寸可以在下一回合复现 |

两个关键推论（决定了方案形态）：

1. **落库边界（`ThroughSequence`）在回合内不可得**：sequence 由录制侧在事件流中分配，产出侧无从得知。所以回合内压缩的完成事件只带产出侧知道的东西——回合开始时已落库的最大 sequence 与最后一次被覆盖的工具调用的 `CallId`，由录制侧把 call id 解析成它存下的 tool run（§5.3）。
2. **单元 = 一条存储消息**：回合内 assistant 输出在录制侧累积为**同一条**消息（只有 steer 边界会切断 `CurrentAssistantMessageId`）。因此"压缩到第 k 轮工具结果为止"无法表达为消息粒度的边界。落库方案没有把消息切开（“封口”），而是在边界上加一个指向消息内部的锚点（§5.4、§7 备选 A）。

## 2. 目标与非目标

### 2.1 目标

1. 长工具循环不再因上下文超窗中途失败（或至少大幅延后；失败时给出可读指引）。
2. 与回合开始压缩共用同一套策略、触发点、保留比例、压缩模型与降级语义（不引入第二套概念）。
3. 回合内压缩的结果跨回合复用：下一回合不重放已经摘要过的内容，界面读数在回合之间保持一致。子代理 / continuation / 自动化回合自动受益（同一条循环、同一个 runtime、同一条提交路径）。
4. 对 steer 准入是净增益：压缩释放额度后，原本会被 Hold 的批次可被接受。

### 2.2 非目标

- 回合内"逐条裁剪工具结果"：仍不做（与"不再裁剪"的既有立场冲突）。
- 把 assistant 消息在压缩点切成两条（“封口”）：不做，理由见 §7 备选 A。
- CLI 链路、跨 provider 精确计数、摘要质量评估：沿用既有非目标。

## 3. 触发与规划

### 3.1 检查点位置

在 `DirectTurnRun.RunAsync` 循环顶部、`ConsumeAvailableInputsAsync` 之前，且 `requestIndex ≥ 1`（实现为 `DirectCompactionStep.CheckpointInTurnAsync`：测一次上下文 → 需要时压缩并重测 → 窗口校验）：

```text
for requestIndex in 0..MaximumProviderRequestsPerTurn-1:
    if requestIndex > 0: CheckpointInTurnAsync() // ← 检查点（DirectCompactionStep）
    ConsumeAvailableInputs()                     // steer 排空（FitsBudget 不变）
    ReadResponse()
    ... 工具执行与结果写回 ...
```

- 该位置同时覆盖"上一轮工具结果刚写回"与"上一批 steer 刚消费"两种增长；`calls.Length == 0` 的 steer 续跑路径经 `continue` 回到循环顶，无需第二检查点。
- `requestIndex == 0` 不检查：setup 刚用同一份计数做过同一决策（要么已压缩、要么判定不压），重复决策无意义。
- 检查点在 steer 排空**之前**：压缩释放的额度可让原本超预算的批次通过 `FitsBudget`。

### 3.2 判定公式（与既有 §5.4 同口径）

```text
counted = context.CountTokens(tokenCounter, profile) + CountTools(options.Tools, profile)   // 与界面读数同口径，不含输出预留
trigger = DirectCompactionPlanner.ResolveTrigger(policy, budget)                            // 与回合开始是同一个函数
触发：max(counted, 本回合上一次请求的实测输入 ?? 0) ≥ trigger
```

输出预留不再加到计数上，只通过 `ResolveTrigger` 给触发点封顶（`direct-context-compaction-design.md` §5.4）。旧判定 `counted + reserve ≥ threshold` 在最大输出很大的模型上，触发点低于压缩后的保留量，于是每个回合间隙都满足触发条件，每次只挤出一个单元并为它单独调一次摘要模型——C23 的日志里同一回合三分钟内连压三次、每次只覆盖 1 个单元。

快速路径（按序短路，均不产生任何行为）：`policy.Enabled == false` → 跳过（不计数）；计数不可用（null）→ 跳过；未到触发点 → 跳过（这一步在给每个单元计数之前，因为绝大多数回合间隙都不到触发点）。

计数成本：`CountTokens` 增量维护（只计新增消息）；`CountTools` 有 schema 指纹缓存。每次迭代的检查成本可忽略（C11 同）。

回合内检查点接 provider 实测下限（2026-11 修订，原先只有回合开始接）：`DirectTurnRun` 记下**本回合上一次请求**的实测输入 token（该请求没报 usage 则为 null，绝不沿用更早的值），触发判定取 `max(本地计数, 实测输入)`。它只能抬高触发、不参与前缀规划，也不参与窗口校验。原先不接的两条理由都不成立：其一，上一次请求的 prompt 是当前 prompt 的严格前缀（之后只追加了响应与工具结果），作为**下限**恰好安全；其二，检查点在循环顶部、压缩之后必然先发一次请求才会再到检查点，所以这个值永远晚于最近一次摘要，不会描述一个已不存在的 prompt。接入的原因是本地计数系统性偏低：tokenizer 与 provider 不同、图片按常量计、reasoning 的签名数据不可见，长工具回合会在本地判断「没到阈值」时先被 provider 以超窗拒绝。只取输入、不加输出：部分协议的输出 token 含不会重放的 reasoning，加进去会高估。回合内响应里的 `TextReasoningContent` 文本本身已由 `DirectTokenCounter` 计入（落库历史重放不带 reasoning，所以只影响回合内）。

### 3.3 规划（复用 `PlanCompression`）

把内存单元（§4）包装为 `DirectPromptHistoryUnit`（回合内单元用高于重放历史的合成 `SourceSequence`，`Tokens` 由 `DirectPromptMeter.CountUnits` 计数），`existing = null` 调用 `PlanCompression`：

- 保留目标针对整个 prompt：`keepTarget = trigger × 50% − 固定开销`（system 含现有摘要、工具定义、受保护消息）；保留最新单元至累计 ≤ `keepTarget`（至少 1 个），其余为前缀。压完的 prompt 约为触发点的一半，下一次触发要等上下文再长出另一半；
- 前缀为空（只剩受保护内容，或最新单元自身超保留目标）→ 拒绝、不压缩、不计入上限。

### 3.4 上限

`MaximumInTurnCompactionsPerTurn = 10`（常量，原 3）。**失败尝试也计入**（约束延迟与成本）；达到上限后不再压缩，请求继续发送。压缩调用**不计入** `MaximumProviderRequestsPerTurn`。

**窗口校验不再只在压缩后**：每个请求前（`requestIndex > 0`）检查点的最后一步都是窗口校验（`DirectCompactionStep.EnsureInTurnPromptFitsWindow`，复用检查点已测的值）。原因是压缩额度用尽后没有任何机制会再缩小 prompt，只靠压缩后校验等于放行一个必然超窗的请求、再由 provider 返回一个不可读的 400。现在超窗由我们自己报可读错误：`The conversation has outgrown the model context window: … approximately {total} tokens, exceeding the window of {window} tokens.`，并在额度耗尽时明确 `The turn used all {n} of its in-turn compactions and still does not fit.`。没有声明窗口的模型直接跳过（没有可比较的基准，且不为它付计数成本）；计数不可用时 fail-open（§5.5）。

## 3. 上限与终止（2026-10 修订）

| 保护 | 值 | 终止语义 |
| --- | --- | --- |
| 用户取消 | — | Cancelled |
| 连续工具故障 | 2 | 抛 `DirectToolFaultLimitException`，回合终止 |
| **无进展检测**（`NoProgressGuard`） | 连续 5 个相同批次 | `Stalled` → Failed（`NoProgress` 文案） |
| **请求上限**（电路断路器） | `MaximumProviderRequestsPerTurn = 1024` | `BudgetExhausted` → Failed |
| **每轮窗口校验** | 声明的上下文窗口 | 抛可读 `InvalidDataException` → Failed |

四者的分工要说清：**请求计数无法区分长任务与死循环**（读 200 个小文件就该是 200 次请求），所以它只当电路断路器用，值放大到 1024。真正防"看起来在干活"的是无进展检测：同一批工具调用得到同一批结果，连续 `NoProgressGuard.RepeatedBatchLimit` = 5 次即判卡死。轮询构建、反复读一个正在变的文件都有新结果，不会被误杀。

压缩额度从 3 提到 10，并且**窗口校验从"仅在压缩后"移到"每个请求前"**——额度耗尽后没有任何东西会再缩小 prompt，只靠压缩后校验等于放行必然超窗的请求、再让 provider 回一个不可读的 400。

## 4. 内存单元模型（边界与受保护内容）

### 4.1 单元（可压缩的最小单位，边界只能落在单元之间）

| 来源 | 单元 = |
| --- | --- |
| 回合开始的重放历史 | composer 的 `DirectPromptHistoryUnit`（一条存储消息；工具调用与结果同单元） |
| 回合内工具往返 | 一次 provider 响应的全部消息 + 紧随其后的工具结果消息（**调用与结果永不分离**） |

含图单元不是例外：带图的开场消息、读到图片的工具往返（承载图片的 follow-up user 消息与其 tool 结果同单元）都可以进入前缀，摘要请求里图片换成文本占位。若把它们排除在外，带图开场的回合里所有往返都排在图之后，回合内压缩会全部失效直到超窗。

### 4.2 受保护（永不进入前缀、永不移除）

| 内容 | 依据 |
| --- | --- |
| system 消息 | 每次请求重建；摘要是固定文本之后的唯一 section（§5.2） |
| 子代理 completion batch | C05 |
| **已消费的 steer 消息** | `direct-message-input-design.md`：已提交的 steer 在本回合内是受保护输入，不得在后续迭代被裁掉。它们在**下一回合**成为普通历史单元，由回合开始压缩按同一策略处理 |
| 未消费的 steer 批次 | 本就不在上下文中（Hold 语义不变） |

### 4.3 上下文改造（`DirectConversationContext`）

由"扁平列表"改为三段结构：system 文本 + 单元序列 + 受保护消息；`Messages` 按序合成。要点：

- 计数初始化直接复用初始单元已有的 `Tokens`（回合开始时已计数），只补计 system/受保护消息，省一次全量扫描；
- `AppendResponse`/`AppendResults` 归组为一个新单元；`AppendInputs` 的 steer 消息进入受保护区，并记下它的存储 sequence——落库边界跨不过一条已消费的 steer（§5.4），上下文靠它把“已落库的受保护消息”与只存在于本次请求里的 completion batch 区分开；
- 应用压缩后重建消息序列并**重计数**（重置增量计数后由下一次 `CountTokens` 重建；每回合 ≤ `MaximumInTurnCompactionsPerTurn` = 10 次，成本有界）。

## 5. 执行、注入与事件

### 5.1 压缩调用

复用 `DirectContextCompressor.TryCompressAsync`：

- `PrefixUnits` = 前缀单元；`PreviousSummary` = 请求当前的唯一摘要（`DirectConversationContext.Summary`：已落库的、回合开始压出来的、或本回合上一次回合内压缩的，都是它）；`SourceTokens` = 前缀单元计数和；`SourceMessageCount` 口径 = 前缀单元数（一条存储消息口径，与既有语义一致）；
- `ModelProfileId` = policy 指定；`TurnLease` = 本回合 `ProviderLease`；`ConversationId/TurnId` 仅日志；
- 指定模型不可用时的回退与 `UsedFallbackModel` 诊断，与回合开始一致（`RunStatus(Compacting, …)`）。

### 5.2 摘要注入与移除

成功后（`DirectConversationContext.ApplyInTurnCompaction`）：

1. 从上下文移除前缀单元的全部消息；
2. 新摘要**替换**请求里唯一的摘要 section。一个请求从头到尾只有一个 section，位置固定在固定 system 文本之后、任务计划之前（`DirectSystemPrompt.ComposeSystemText`，`direct-context-compaction-design.md` §7）；旧摘要——不管它来自落库记录、回合开始压缩还是上一次回合内压缩——已经经 `PreviousSummary` 折进了新摘要。原先“回合开始的 section 留在能力指令之前、回合内 section 追加在末尾、两者并存”的布局已删除：并存时回合内摘要不含更早的摘要，没法单独落库；
3. 重计数；`counted' + reserve > window` → 抛与 `EnsurePromptFitsWindow` 同族的可读错误（决策 A）。

### 5.3 事件

| 事件 | 处置 |
| --- | --- |
| `ContextCompactionStartedEvent(estimated, threshold)` | 复用；录制侧既有处理（活动文案"正在压缩上下文..."）。`threshold` 是触发点，与 `estimated` 同口径 |
| `ContextCompactionFailedEvent(reason)` | 复用；录制侧既有处理；回合继续按未压缩上下文发送 |
| `InTurnContextCompactionCompletedEvent(CoveredUnitCount, SourceTokens, SummaryTokens, Model, Compaction, AnchorCallId)`（`Core/Runtime/Agent`） | `Compaction` 非空时落库（§5.4），为空时仅记日志 |

可见性：活动文案（Started / 失败）+ 完成事件后立即下发的本地估算读数（`UsageProgressEvent`，`IsEstimated = true`，界面带 `~`）+ 该回合用户消息之后的 `上下文压缩` 分隔项。重新加载后分隔项与读数都还在。[决策 C]

### 5.4 落库与重放（C23）

为什么要落库：不落库时，下一回合把上一回合整条 assistant 消息全量重放——界面刚显示的低读数随即失效，回合开始立刻再压一次；这条消息经历过两次以上回合内压缩时会比窗口还大，摘要请求失败、窗口校验再让回合失败，会话从此开不了新回合。

**边界怎么表达**。落库边界 = `ThroughSequence` + 可选的 `ThroughToolRunId`（`direct-context-compaction-design.md` §4.1）：前者之前的消息整条被覆盖，后者指向其后那条 assistant 消息里摘要覆盖到的最后一次工具调用。

**产出侧**（`DirectConversationContext.AdvanceCoverage` → `DirectInTurnCoverage(ThroughSequence, AnchorCallId, MessageCount, SourceTokens)`，累计值保存在上下文里）。按 prompt 顺序走被移除的前缀：

| 走到的条目 | 处置 |
| --- | --- |
| 重放历史的单元（已落库） | `ThroughSequence` 推进到它的 sequence，`MessageCount + 1` |
| 回合内单元（本回合的 assistant 消息还在写） | `ThroughSequence` 至少推进到回合开始时已落库的最大 sequence（`DirectTurnSetup.HistoryThroughSequence`）；锚点取该单元最后一个 `FunctionCallContent.CallId`；首次进入这条消息时 `MessageCount + 1`。没有工具调用的单元无处可锚，下一回合照常重放它的文本 |
| completion batch（受保护、从不落库） | 跳过，不影响边界 |
| 已消费的 steer（受保护、已落库） | **停止推进**。steer 留在 prompt 里，而它两侧的单元都可能被移除，“一个 sequence + 其后消息里的一个锚点”表达不了这种覆盖。此后本回合的回合内摘要只在请求里生效 |

这次压缩没有推进任何可表达的边界时返回 null，事件的 `Compaction` 为空，已落库的记录保持上一次能描述其覆盖范围的摘要。

**记录身份**。`DirectConversationContext.InTurnCompactionId` 每回合一个固定 id：同一回合的多次回合内压缩更新同一条记录，转录只有一条分隔项。回合开始压缩的记录是另一条（各自的 id），两条并存时重放取 `ThroughSequence` 较大的那条，即回合内的。

**录制侧**（`ConversationTurnRecorder.ApplyInTurnCompaction`）。锚点在事件里是 call id，因为只有录制侧知道落库的 tool run：用 `turn.ToolRunsByCallId` 解析，解析不到就不带锚点；`PendingCompactions` 里按 id 替换；`session.ReplaceCompaction` 后立即发布。落库走既有的回合终态原子提交，交互 / 子代理 / continuation / 自动化同一条路径，提交契约没有改动。

**重放侧**。`DirectHistoryReplay.BuildUnits` 重建锚点所在的 assistant 消息时，走到锚点调用就丢弃已累积的内容，只留其后的部分；`DirectHistoryReplay.IsCovered` 是“某次工具调用是否已被摘要覆盖”的唯一判定（任务计划、技能激活都用它）。其余只读 `ThroughSequence` 的地方不需要改：锚点消息的 sequence 恒大于它。

**落库后的行为**。下一回合的 prompt = 摘要 + 锚点之后的原文 + 新消息，与上一回合结束时的上下文一致：读数不再失效，也不会一开始就再压一次。

**降级只有一个方向**——多重放一些摘要里已有的内容，从不丢历史：

- 记录没落库（崩溃、可重试失败的 continuation）；
- 锚点解析不到（call id 没有对应的 tool run）；
- steer 之后的覆盖。

这些情况下一回合由回合开始压缩兜底；那条 assistant 消息可能比摘要模型的窗口还大，由分块链式摘要处理（`direct-context-compaction-design.md` §6 规则 4）。

## 6. 失败、降级与超窗

- 压缩失败：`Failed` 事件 + 继续按未压缩上下文发送；**绝不移除任何内容**（与回合开始 §6 一致）。计入上限。
- 计数不可用：跳过检查点（setup 已发过 `CountingUnavailableDiagnostic`），不新增诊断、不使回合失败（§5.5 同）。
- 上限用尽：继续发送；若 provider 因此报错，失败路径与现状一致。
- **压缩后仍超窗**（决策 A）：`counted' + reserve > window` → 抛 `InvalidDataException`（文案与 `EnsurePromptFitsWindow` 同族：开启压缩 / 降低输出上限 / 换更大窗口），回合 Failed 但消息与转录保留。理由：与 §5.4"绝不静默发送已知超限请求"同构；该情形只有"受保护内容 + 工具定义"本身超窗、或保留的最新单元超窗时才可达。

## 7. 备选方案与取舍

**A. 落库型回合内压缩（C23 已落地，采用锚点列而不是封口）**

最初记录的代价有四条，落地时逐条的处置：

1. 产出侧不知道 sequence → 完成事件携带 `AnchorCallId`（最后一次被覆盖的工具调用）与回合开始时已落库的最大 sequence，录制侧把 call id 解析成 tool run id。这一条照原设想做了。
2. 压缩点几乎总落在当前 assistant 消息**中途** → 原设想是新增“封口”事件把消息在压缩点切开。**没有采用**：封口让转录里的 assistant 消息被中途分成两段，还牵出第 3 条。改为在压缩记录上加一列 `through_tool_run_id`（schema v33），让边界可以指进消息内部；消息本身不动，转录零改动。
3. detached 路径的终态提交契约是单条 `AssistantMessage`，封口产生的多消息需要扩契约 → 不封口就不存在这个问题，提交契约没有改动。
4. “收益有限，下一回合的回合开始压缩本来就会对同一内容做一次落库摘要” → 这条判断被实际使用推翻：那一次“本来就会做”的压缩要把整条 assistant 消息一次性交给摘要模型，消息大过窗口时必然失败并卡死会话；即便成功，也让界面读数在回合之间跳变、压缩频率翻倍。

锚点方案的代价是边界多了一个维度：所有问“这次调用还在不在 prompt 里”的地方必须走 `DirectHistoryReplay.IsCovered`，以及 steer 之后的覆盖无法表达（§5.4）。

**B. 原位截断旧工具结果**：与"不再裁剪"的设计立场冲突（C01 / 删除清单），不做。

**C. 保持现状（回合内无压缩）**：正是本方案要解决的问题。

## 8. 不变量（修订与新增）

- **C06 修订**：每回合开始最多一次压缩（不变）；回合内维护性压缩 ≤ `MaximumInTurnCompactionsPerTurn`，不裁剪，随回合终态落库为一条记录。
- T01（C23 改写）回合内压缩不产生自己的数据库写入：记录只随回合终态原子提交；一个回合至多一条回合内记录（固定 id，后一次替换前一次）；落库边界表达不了的覆盖只在本回合请求里生效，降级方向永远是“多重放”，不是“少重放”。
- T02 单元完整性：工具调用与其结果永不分离；system、completion batch、已消费 steer 永不被回合内压缩覆盖。
- T03 失败 / 计数不可用 / 上限用尽 → 回退为"本回合不再压缩"，不新增失败路径（唯一例外：决策 A 的"压缩后仍超窗"可读失败）。
- T04 压缩调用不计入 `MaximumProviderRequestsPerTurn`；自身受 `MaximumInTurnCompactionsPerTurn`（10 次）上限约束。
- T05 回合内摘要与回合开始摘要共用一个压缩器、一个触发点公式与保留比例、一个摘要 section；无第二套计数口径。

## 9. 实施拆分（I1–I8）

| 项 | 内容 | 主要落点 |
| --- | --- | --- |
| I1 | 上下文单元模型：三区结构、构造入参、计数初始化复用单元 Tokens、`ApplyInTurnCompaction`、`AppendResponse/AppendResults` 归组、`AppendInputs` 转受保护消息 | `DirectConversationContext` |
| I2 | setup 载荷：policy、budget、初始单元（含 Tokens）、`ConversationId/TurnId`；runtime 组装时复用 `CountPromptTokens` 已构建的单元（顺带消除 `BuildMessages` 内部的二次单元构建，AGENTS 约定 10） | `DirectTurnSetup`、`DirectAgentChatRuntime`、`DirectPromptComposer`（现 `DirectTurnPreparer` + `DirectPromptMeter`） |
| I3 | 循环检查点：快速路径、规划、上限、事件、压缩调用、应用、重计数与超窗错误 | `DirectConversationLoop`（现 `DirectTurnRun` + `DirectCompactionStep`） |
| I4 | 摘要注入与链式（本回合内 section 替换） | `DirectConversationContext` |
| I5 | 新事件 + 录制分支（首轮 log-only；C23 起落库，§5.4） | `ContextCompaction*` 新事件文件、`ConversationTurnRecorder` |
| I6 | DI：loop 注册补 composer / compressor | `ServiceCollectionExtensions.cs`（loop 注册处） |
| I7 | 测试（见 §11） | `SelfClaw.Tests/Infrastructure/Agents/Direct/` |
| I8 | 文档同步：`direct-context-compaction-design.md`（§1 表格行、§2.2、C06、§11 风险行、§5.4 交叉引用）、`direct-context-compaction-implementation-tasks.md`（DP4 注记）、核对 `runtime-execution-flow.md` 是否需要同步 | `docs/` |

## 10. 已确认决策

| # | 决策 | 值 |
| --- | --- | --- |
| A | 压缩后仍超窗：可读错误 vs 继续发送 | 可读错误（与既有 §5.4 同构）：回合 Failed，消息与转录保留 |
| B | 上限、失败计入 | `MaximumInTurnCompactionsPerTurn`（设计时 3，2026-10 修订为 10），失败尝试计入 |
| C | 可见性：仅活动文案 + 状态栏读数 vs 转录分隔项 | 2026-10 定为前者（不写转录、不落库）；2026-11-24 改为落库 + 转录分隔项（§5.4） |
| D | 触发水位：复用回合开始的触发点 vs 回合内单独水位 | 复用回合开始的触发点（一套概念；压缩不自激由保留目标保证，上限只是兜底） |
| E | 压缩完成后是否补一条活动文案（如"上下文已压缩，继续执行…"） | 不新增完成文案 |

## 11. 测试与验证

新增 `DirectInTurnCompactionTests`（循环级，复用 `DirectAgentChatRuntimeTests` 的 `FakeChatClientFactory` / `FixedPolicySource` / `TurnClient` 模式）：

1. 跨阈值增长 → 恰好一次回合内压缩：`Started → InTurn…Completed`，下一请求含摘要 section 且前缀消息消失、保留尾部仍在。
2. 未达阈值 → 无压缩调用、无事件。
3. 保留规则：压完的整个 prompt ≤ 触发点×50%，至少保留 1 个单元。
4. 工具调用与结果同进同出（前缀边界落在往返组之间）。
5. 上限：持续增长只压缩 `MaximumInTurnCompactionsPerTurn`（10）次，之后请求照常发送。
6. 压缩失败 → `Failed` 事件、上下文未变、回合继续。
7. 计数不可用 / policy 关闭 → 零行为变化。
8. 受保护内容：completion batch（continuation origin）、已消费 steer 在压缩后仍在。
9. steer 交互：超窗批次在压缩后被接受；仍不满足时 Hold + `ContextBudgetExceeded` 行为不变。
10. 压缩后仍超窗 → 可读失败（决策 A）。
11. 回归：回合开始压缩行为不变（`DirectCompactionRuntimeTests` 全绿）。
12. 落库（C23）：完成事件携带记录与锚点 call id；首次回合内压缩把回合开始的摘要折进自己的摘要，请求里只剩一个 section；带回合内压缩的回合结束后，用落库数据构造下一回合请求，只重放摘要和锚点之后的内容且回合开始不再触发压缩；去掉锚点时下一回合重放整条消息并再压一次（降级方向）。覆盖范围的单元级用例在 `DirectConversationContextTests`（仅重放单元、跨入回合内单元、遇 steer 停止、completion batch 不打断、无可表达边界时返回 null）。

```powershell
dotnet build SelfClaw.slnx
dotnet test SelfClaw.Tests/SelfClaw.Tests.csproj --filter Direct
dotnet test SelfClaw.Tests/SelfClaw.Tests.csproj --filter Compaction
```
