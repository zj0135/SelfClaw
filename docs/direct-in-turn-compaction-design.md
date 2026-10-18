# Direct 回合内上下文压缩设计

状态：设计冻结（v1）· 已完成（T1–T4）
配套实施文档：《direct-in-turn-compaction-implementation-tasks.md》
关系：修订 `docs/direct-context-compaction-design.md` 的 §1 现状表、§2.2 非目标与不变量 C06；复用其策略、阈值、保留比例、压缩器与降级语义。

已确认决策（2026-10-18，用户批准方案）：

- **决策 A**：压缩后仍超窗 → 可读错误（回合 Failed，消息与转录保留）。
- **决策 B**：`MaximumInTurnCompactionsPerTurn = 3`，失败尝试计入。
- **决策 C**：可见性 = 活动文案 + 状态栏读数；不写转录、不落库。
- **决策 D**：触发水位复用回合开始 threshold。
- **决策 E**：不新增完成文案。

实施任务书：`docs/direct-in-turn-compaction-implementation-tasks.md`（T1–T4）。

## 0. 结论摘要

| 问题 | 现状（代码事实） | 本方案 |
| --- | --- | --- |
| 回合内增长 | 工具循环期间上下文只增不减；超窗时由 provider 报错终结回合 | 在工具循环的**回合间隙**检查同一阈值；超阈值即把内存中最早的若干单元交给压缩模型，摘要注入 system 段，仅替换**本回合进程内**的请求上下文 |
| 持久化 | 回合开始压缩：摘要落库（`conversation_compactions`，schema v32），边界 `ThroughSequence` 供后续回合重放 | **零持久化**：不写库、不改 schema、不进转录；下回合仍由既有回合开始压缩负责落库 |
| steer 准入 | 超预算的批次直接 Hold 并以 `ContextBudgetExceeded` 终结回合 | 检查点位于 steer 排空之前，压缩释放的额度可让批次被接受（净增益）；仍不满足则行为不变 |
| 失败语义 | —— | 压缩失败 / 计数不可用 / 上限用尽 → 回到现状（继续发送，provider 决定），绝不裁剪；唯一例外见 §6 决策 A |

一句话：**回合开始压缩解决"新回合带着超长历史出发"；回合内压缩解决"回合自己把窗口撑爆"——同一套阈值与摘要器，但摘要只活在本次回合的进程内，转录与数据库不动。**

## 1. 现状盘点（与本方案相关的代码事实）

| 位置 | 事实 |
| --- | --- |
| `DirectConversationLoop.cs` | 每回合 ≤128 次 provider 请求；每次迭代 = 排空 steer → 请求 → 追加响应 → 执行工具 → 追加结果 |
| `DirectConversationLoop.cs` `FitsBudget` | 只用于 steer 批次准入（`used + tools + reserve ≤ window`）；超预算 → `HoldUnconsumedAsync` + `ContextBudgetExceeded` 终结 |
| `DirectConversationContext.cs` | 扁平 `List<ChatMessage>` + 增量计数（`_countedMessageCount/_countedTokens`）；回合内消息**没有单元结构**；`AppendInputs` 丢弃 steer 的 `MessageRecord`（只保留文本） |
| `DirectAgentChatRuntime.cs` `ApplyCompactionAsync` | 回合开始压缩只发生一次：计数 → `PlanCompression` → 压缩 → `ContextCompactionCompletedEvent`（录制侧进 `PendingCompactions` → 回合终态原子落库） |
| `DirectPromptComposer.cs` `PlanCompression` | threshold = 窗口×比例（无窗口用绝对阈值）；reserve 在触发侧；保留 = 阈值×50%，至少最新 1 个单元；前缀为空则拒绝 |
| `DirectContextCompressor.cs` | `TryCompressAsync`：单次调用、内建"指定模型不可用 → 回退本回合 lease"；失败返回 null |
| `ConversationTurnRecorder.cs` | 消息 sequence 在**录制侧**保留（`ReserveMessageSequenceAsync`，首个 delta 即分配）；产出侧（Infrastructure）不知道本回合消息的 sequence |
| `TurnUsageAccumulator.cs` | `last_request_input_tokens` 取最新、不求和 → 压缩后下一请求的输入量下降，输入框状态栏即时可见 |
| `direct-message-input-design.md` §相关行 | "已经提交的 steer 在本回合内作为受保护输入，不能在之后迭代中被静默裁掉" |
| 增长来源上限 | MCP 结果序列化 ≤64 KiB（`McpToolResultFormatter`）；Shell 输出 ≤24k 字符（`ProcessRunner.MaxOutputCharacters`）；文件读取 >1 MB 直接拒绝（`WorkspaceFileAccess.MaxFileBytes`）；Skill 资源 ≤1 MB；**入库**工具结果另有 `TranscriptToolResultLimiter.LimitStored`（64 KiB 字符）——回合内内存上下文可能持有比未来任何回合能重放的内容更多的东西 |

两个关键推论（决定了方案形态）：

1. **落库边界（`ThroughSequence`）在回合内不可得**：sequence 由录制侧在事件流中分配，产出侧无从得知。若要让回合内压缩也落库，必须发明"封口事件 + 录制侧边界解析"协议（见 §7 备选 A）。
2. **单元 = 一条存储消息**：回合内 assistant 输出在录制侧累积为**同一条**消息（只有 steer 边界会切断 `CurrentAssistantMessageId`）。因此"压缩到第 k 轮工具结果为止"在落库时无法表达为消息粒度的边界——边界必须整消息对齐，这正是备选 A 代价高昂的根因。

## 2. 目标与非目标

### 2.1 目标

1. 长工具循环不再因上下文超窗中途失败（或至少大幅延后；失败时给出可读指引）。
2. 与回合开始压缩共用同一套策略、阈值、保留比例、压缩模型与降级语义（不引入第二套概念）。
3. 零 schema、零持久化、零转录协议变更；子代理 / continuation / 自动化回合自动受益（同一条循环、同一个 runtime）。
4. 对 steer 准入是净增益：压缩释放额度后，原本会被 Hold 的批次可被接受。

### 2.2 非目标

- **回合内压缩落库**（摘要跨回合复用）：本次不做，理由与后续路径见 §7 备选 A。
- 回合内"逐条裁剪工具结果"：仍不做（与"不再裁剪"的既有立场冲突）。
- CLI 链路、跨 provider 精确计数、摘要质量评估：沿用既有非目标。

## 3. 触发与规划

### 3.1 检查点位置

在 `DirectConversationLoop.RunAsync` 循环顶部、`ConsumeAvailableInputsAsync` 之前，且 `requestIndex ≥ 1`：

```text
for requestIndex in 0..127:
    if requestIndex > 0: TryCompactInTurn()      // ← 新增检查点
    ConsumeAvailableInputs()                     // steer 排空（FitsBudget 不变）
    ReadResponse()
    ... 工具执行与结果写回 ...
```

- 该位置同时覆盖"上一轮工具结果刚写回"与"上一批 steer 刚消费"两种增长；`calls.Length == 0` 的 steer 续跑路径经 `continue` 回到循环顶，无需第二检查点。
- `requestIndex == 0` 不检查：setup 刚用同一份计数做过同一决策（要么已压缩、要么判定不压），重复决策无意义。
- 检查点在 steer 排空**之前**：压缩释放的额度可让原本超预算的批次通过 `FitsBudget`。

### 3.2 判定公式（与既有 §5.4 同口径）

```text
counted   = context.CountTokens(tokenCounter, profile) + CountTools(options.Tools, profile)
total     = counted + reserve                    // reserve = MaxOutputTokens ?? 4096
threshold = 窗口×ThresholdRatio 或 AbsoluteThresholdTokens   // 与回合开始完全一致
触发：total ≥ threshold
```

快速路径（按序短路，均不产生任何行为）：`policy.Enabled == false` → 跳过（不计数）；计数不可用（null）→ 跳过；`total < threshold` → 跳过。

计数成本：`CountTokens` 增量维护（只计新增消息）；`CountTools` 有 schema 指纹缓存。每次迭代的检查成本可忽略（C11 同）。

### 3.3 规划（复用 `PlanCompression`）

把内存单元（§4）包装为 `DirectPromptHistoryUnit`（`SourceSequence = 0`、`Tokens` = 建单元时已计数），`existing = null` 调用 `PlanCompression`：

- 保留最新单元至累计 ≤ 阈值×50%（至少 1 个），其余为前缀；
- 前缀为空（只剩受保护内容，或最新单元自身超保留目标）→ 拒绝、不压缩、不计入上限。

### 3.4 上限

`MaximumInTurnCompactionsPerTurn = 3`（常量）。**失败尝试也计入**（约束延迟与成本）；达到上限后不再压缩，继续发送（回到现状，provider 决定）。压缩调用**不计入** `MaximumProviderRequestsPerTurn`。

## 4. 内存单元模型（边界与受保护内容）

### 4.1 单元（可压缩的最小单位，边界只能落在单元之间）

| 来源 | 单元 = |
| --- | --- |
| 回合开始的重放历史 | composer 的 `DirectPromptHistoryUnit`（一条存储消息；工具调用与结果同单元） |
| 回合内工具往返 | 一次 provider 响应的全部消息 + 紧随其后的工具结果消息（**调用与结果永不分离**） |

### 4.2 受保护（永不进入前缀、永不移除）

| 内容 | 依据 |
| --- | --- |
| system 消息 | 每次请求重建；摘要以追加 section 的形式挂在其末尾 |
| 续写提示（`ContinuationPrompt`） | C05 |
| 子代理 completion batch | C05 |
| **已消费的 steer 消息** | `direct-message-input-design.md`：已提交的 steer 在本回合内是受保护输入，不得在后续迭代被裁掉。它们在**下一回合**成为普通历史单元，由回合开始压缩按同一策略处理 |
| 未消费的 steer 批次 | 本就不在上下文中（Hold 语义不变） |

### 4.3 上下文改造（`DirectConversationContext`）

由"扁平列表"改为三段结构：system 文本 + 单元序列 + 受保护消息；`Messages` 按序合成。要点：

- 计数初始化直接复用初始单元已有的 `Tokens`（回合开始时已计数），只补计 system/受保护消息，省一次全量扫描；
- `AppendResponse`/`AppendResults` 归组为一个新单元；`AppendInputs` 的 steer 消息进入受保护区（只保留 `ChatMessage`，不保留 `MessageRecord` 引用——当前无读者）；
- 应用压缩后重建消息序列并**重计数**（重置增量计数后由下一次 `CountTokens` 重建；每回合 ≤3 次，成本有界）。

## 5. 执行、注入与事件

### 5.1 压缩调用

复用 `DirectContextCompressor.TryCompressAsync`：

- `PrefixUnits` = 前缀单元；`PreviousSummary` = 本回合内现有摘要（链式）；`SourceTokens` = 前缀单元计数和；`SourceMessageCount` 口径 = 前缀单元数（一条存储消息口径，与既有语义一致）；
- `ModelProfileId` = policy 指定；`TurnLease` = 本回合 `ProviderLease`；`ConversationId/TurnId` 仅日志；
- 指定模型不可用时的回退与 `UsedFallbackModel` 诊断，与回合开始一致（`RunStatus(Compacting, …)`）。

### 5.2 摘要注入与移除

成功后：

1. 从上下文移除前缀单元的全部消息；
2. 摘要包裹为 `<selfclaw-context-summary version="1">…</selfclaw-context-summary>`（与 `CreateCompressionSection` 相同外壳与说明行）**追加到 system 文本末尾**；连续多次回合内压缩时**替换**本回合内 section（不叠加），旧内容经 `PreviousSummary` 传入；回合开始压缩的 section 保持原位不动；
3. 重计数；`counted' + reserve > window` → 抛与 `EnsurePromptFitsWindow` 同族的可读错误（决策 A）。

⚠ 布局说明：回合开始压缩的 section 位于能力指令之前（C6 的字节布局约束，目的是"无压缩时与旧实现逐字节一致"）。回合内 section 追加在 system 文本末尾，只在回合内压缩发生后出现——这条路径在旧实现中不存在，不违反该约束。

### 5.3 事件

| 事件 | 处置 |
| --- | --- |
| `ContextCompactionStartedEvent(estimated, threshold)` | 复用；录制侧既有处理（活动文案"正在压缩上下文..."） |
| `ContextCompactionFailedEvent(reason)` | 复用；录制侧既有处理；回合继续按未压缩上下文发送 |
| `InTurnContextCompactionCompletedEvent(CoveredUnitCount, SourceTokens, SummaryTokens, Model)`（新，`Core/Runtime/Agent`） | 录制侧**仅记日志**：不写 `PendingCompactions`、不调 `ReplaceCompaction`、转录不出现分隔项、不落库 |

可见性：活动文案（Started / 失败）+ 下一请求后输入框状态栏"上下文"读数下降（`last_request_input_tokens` 取最新）。重新加载后无任何痕迹（无持久化状态改变）。[决策 C]

## 6. 失败、降级与超窗

- 压缩失败：`Failed` 事件 + 继续按未压缩上下文发送；**绝不移除任何内容**（与回合开始 §6 一致）。计入上限。
- 计数不可用：跳过检查点（setup 已发过 `CountingUnavailableDiagnostic`），不新增诊断、不使回合失败（§5.5 同）。
- 上限用尽：继续发送；若 provider 因此报错，失败路径与现状一致。
- **压缩后仍超窗**（决策 A）：`counted' + reserve > window` → 抛 `InvalidDataException`（文案与 `EnsurePromptFitsWindow` 同族：开启压缩 / 降低输出上限 / 换更大窗口），回合 Failed 但消息与转录保留。理由：与 §5.4"绝不静默发送已知超限请求"同构；该情形只有"受保护内容 + 工具定义"本身超窗、或保留的最新单元超窗时才可达。

## 7. 备选方案与取舍

**A. 落库型回合内压缩（本次不做，记录为后续方向）**

收益：摘要跨回合复用、转录分隔项持久。代价（逐条已对码核实）：

1. 产出侧不知道 sequence → 需要"压缩完成事件携带锚点（如最后一次工具调用的 `CallId`）+ 录制侧解析出边界"的新协议；
2. 单元 = 一条存储消息，而回合内压缩点几乎总落在当前 assistant 消息**中途** → 必须新增"封口"事件把消息在压缩点切开；否则下一回合按边界重放会**连带跳过未摘要的后半段**（信息丢失），封口又意味着转录里 assistant 消息被中途分成两段；
3. detached 路径（子代理 / continuation）终态提交契约是单条 `AssistantMessage`（`RecordedTurnFinalizationRequest`），且输入边界（唯一现成的封口点）只允许 Interactive → 封口产生的多消息需要扩契约并连带 builder / writer / committer；
4. 收益有限：下一回合的回合开始压缩本来就会对同一内容做一次落库摘要——落库型只是把这次调用提前，并多付 1–3 的复杂度。

本方案的单元模型、压缩器调用与事件骨架可原样复用，后续若做落库型只需在其上补"封口 + 边界解析"。

**B. 原位截断旧工具结果**：与"不再裁剪"的设计立场冲突（C01 / 删除清单），不做。

**C. 保持现状（回合内无压缩）**：正是本方案要解决的问题。

## 8. 不变量（修订与新增）

- **C06 修订**：每回合开始最多一次压缩（不变）；回合内维护性压缩 ≤ `MaximumInTurnCompactionsPerTurn`，不落库、不裁剪。
- T01 回合内压缩只作用于进程内请求上下文：不产生任何数据库写入、不改转录。
- T02 单元完整性：工具调用与其结果永不分离；system、续写提示、completion batch、已消费 steer 永不被回合内压缩覆盖。
- T03 失败 / 计数不可用 / 上限用尽 → 回退为"本回合不再压缩"，不新增失败路径（唯一例外：决策 A 的"压缩后仍超窗"可读失败）。
- T04 压缩调用不计入 `MaximumProviderRequestsPerTurn`；自身受 3 次上限约束。
- T05 回合内摘要与回合开始摘要共用一个压缩器、一套阈值与保留比例；无第二套计数口径。

## 9. 实施拆分（I1–I8）

| 项 | 内容 | 主要落点 |
| --- | --- | --- |
| I1 | 上下文单元模型：三区结构、构造入参、计数初始化复用单元 Tokens、`ApplyInTurnCompaction`、`AppendResponse/AppendResults` 归组、`AppendInputs` 转受保护消息 | `DirectConversationContext` |
| I2 | setup 载荷：policy、budget、初始单元（含 Tokens）、`ConversationId/TurnId`；runtime 组装时复用 `CountPromptTokens` 已构建的单元（顺带消除 `BuildMessages` 内部的二次单元构建，AGENTS 约定 10） | `DirectTurnSetup`、`DirectAgentChatRuntime`、`DirectPromptComposer` |
| I3 | 循环检查点：快速路径、规划、上限、事件、压缩调用、应用、重计数与超窗错误 | `DirectConversationLoop` |
| I4 | 摘要注入与链式（本回合内 section 替换） | `DirectConversationContext` |
| I5 | 新事件 + 录制分支（log-only） | `ContextCompaction*` 新事件文件、`ConversationTurnRecorder` |
| I6 | DI：loop 注册补 composer / compressor | `ServiceCollectionExtensions.cs`（loop 注册处） |
| I7 | 测试（见 §11） | `SelfClaw.Tests/Infrastructure/Agents/Direct/` |
| I8 | 文档同步：`direct-context-compaction-design.md`（§1 表格行、§2.2、C06、§11 风险行、§5.4 交叉引用）、`direct-context-compaction-implementation-tasks.md`（DP4 注记）、核对 `runtime-execution-flow.md` 是否需要同步 | `docs/` |

## 10. 已确认决策

| # | 决策 | 值 |
| --- | --- | --- |
| A | 压缩后仍超窗：可读错误 vs 继续发送 | 可读错误（与既有 §5.4 同构）：回合 Failed，消息与转录保留 |
| B | 上限 = 3 次、失败计入 | `MaximumInTurnCompactionsPerTurn = 3`，失败尝试计入 |
| C | 可见性：仅活动文案 + 状态栏读数 vs 转录瞬态分隔项 | 活动文案 + 状态栏读数；不写转录、不落库 |
| D | 触发水位：复用回合开始的 threshold vs 回合内单独水位 | 复用回合开始 threshold（一套概念；churn 由上限兜底） |
| E | 压缩完成后是否补一条活动文案（如"上下文已压缩，继续执行…"） | 不新增完成文案 |

## 11. 测试与验证

新增 `DirectInTurnCompactionTests`（循环级，复用 `DirectAgentChatRuntimeTests` 的 `FakeChatClientFactory` / `FixedPolicySource` / `TurnClient` 模式）：

1. 跨阈值增长 → 恰好一次回合内压缩：`Started → InTurn…Completed`，下一请求含摘要 section 且前缀消息消失、保留尾部仍在。
2. 未达阈值 → 无压缩调用、无事件。
3. 保留规则：最新单元累计 ≤ 阈值×50%，至少保留 1 个。
4. 工具调用与结果同进同出（前缀边界落在往返组之间）。
5. 上限：持续增长只压缩 3 次，之后请求照常发送。
6. 压缩失败 → `Failed` 事件、上下文未变、回合继续。
7. 计数不可用 / policy 关闭 → 零行为变化。
8. 受保护内容：续写提示、completion batch（continuation origin）、已消费 steer 在压缩后仍在。
9. steer 交互：超窗批次在压缩后被接受；仍不满足时 Hold + `ContextBudgetExceeded` 行为不变。
10. 压缩后仍超窗 → 可读失败（决策 A）。
11. 回归：回合开始压缩行为不变（`DirectCompactionRuntimeTests` 全绿）。

```powershell
dotnet build SelfClaw.slnx
dotnet test SelfClaw.Tests/SelfClaw.Tests.csproj --filter Direct
dotnet test SelfClaw.Tests/SelfClaw.Tests.csproj --filter Compaction
```
