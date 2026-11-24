# Direct 上下文自动压缩设计

状态：设计冻结（v2）· 已完成（C1–C16 落地并审核通过）· 更新 2026-11-24（C23：触发点与界面读数同口径、保留目标针对整个 prompt、分块摘要、回合内压缩落库（schema v33）、用量环显示触发点）
配套实施文档：《direct-context-compaction-implementation-tasks.md》
替代关系：本设计取代 `DirectPromptComposer`（现拆为 `DirectHistoryReplay` / `DirectSystemPrompt` / `DirectCompactionPlanner`）基于 UTF-8 估算的历史裁剪（`BuildHistoryWithinBudget`），并修订 `runtime-execution-flow.md` §5 的“token 预算仍是启发式估算”一段 —— 字节估算整体退场，计数改为离线 tiktoken 系 tokenizer 加 provider 回报的实测值。

已确认决策（2026-10-17）：

- **DP1 确认**：新增 `conversation_compactions` 表，schema v31 → v32；旧库按仓内既有策略只读拒绝、不迁移不回填。
- **DP2 确认**：不再使用字节启发式估算。计数统一走 tiktoken 系 tokenizer（离线词表），并以 provider 回报的实测值校正与兜底。

已确认决策（2026-11-24，C23）：

- **触发点与界面读数同口径**：输出预留不再加到计数上，只作为触发点的封顶（§5.4）。
- **回合内压缩落库**：`conversation_compactions` 增加锚点列 `through_tool_run_id`，schema v32 → v33；旧库同样只读拒绝、不迁移（§4.2）。

## 0. 结论摘要

| 问题 | 现状 | 本设计 |
| --- | --- | --- |
| 上下文超限 | 当时的 `DirectPromptComposer`（现 `DirectHistoryReplay`）按 UTF-8 字节数估算，从最旧单元开始**静默丢弃**历史 | 改为**自动压缩**：把超限前缀交给模型总结，摘要持久化并替代该前缀 |
| token 计数 | `(UTF-8 字节数 + 2) / 3` 启发式 | 离线 tiktoken 系 tokenizer 计数，配 provider 回报的实测值 |
| 阈值 | 硬编码预算计算，用户不可见 | 系统设置可配：触发比例、无窗口模型的绝对阈值、启用开关 |
| 压缩模型 | 无 | 默认跟随当前会话选中模型；可在设置中指定其他已启用模型 |
| 用户可见性 | 无（历史静默消失） | 转录中插入 `上下文压缩` 分隔项，可展开查看摘要 |
| 用量可见性 | 回合末显示总 token | 输入框区域常驻显示：当前上下文大小 + 会话累计输入/输出 token |

一句话：**把“悄悄丢掉历史”换成“显式压缩历史，并把压缩这件事告诉用户”；把“按字节猜”换成“按 tokenizer 算、按 provider 实测校正”。**

## 1. 现状盘点

| 位置 | 现有行为 |
| --- | --- |
| `SelfClaw.Infrastructure/Agents/Direct/Context/DirectPromptComposer.cs`（现拆为 `DirectHistoryReplay` / `DirectSystemPrompt` / `DirectCompactionPlanner`） | `BuildMessages` 组装 system → 历史 → completion batch；`BuildHistoryWithinBudget` 从最新消息反向累积 `DirectPromptHistoryUnit`，预算耗尽即丢弃更旧单元；`EstimateTokens = (UTF8 字节数 + 2) / 3` |
| 同上 | `BudgetFor` 用 `ContextWindowTokens - (system + tools + 输出预留)` 得到历史预算；`DefaultOutputTokenReserve = 4096` |
| `Agents/Direct/DirectAgentChatRuntime.cs` | `SetupTurnAsync`（现 `DirectTurnPreparer.PrepareAsync`）调用 composer（每回合一次）；`WriteTerminalOutcome` 映射终态 |
| `Agents/Direct/DirectTurnRun.cs`（原 `DirectConversationLoop.cs`） | 每回合 ≤ `DirectTurnRun.MaximumProviderRequestsPerTurn` = 1024 次 provider 请求；`FitsBudget` 只用于 steer 批次准入；回合内增长已由回合内压缩约束（见 `direct-in-turn-compaction-design.md`），回合开始压缩不变 |
| `AiProviders/AiChatOptions.cs` | `ResolveContextWindowTokens`：`Configuration.ContextLength` 优先，其次 model option `context_window_tokens`，都没有则历史不受限 |
| `AiProviders/RequestUsageAccumulator.cs`、`Agents/Direct/TurnUsageReporter.cs` | 每次 provider 调用的 usage 归一化后计入回合；`ContextTokens = 该次调用 input + output` |
| `Desktop/Services/Runtime/ConversationTurnRecorder.cs` | 事件归约；`UsageReportedEvent` 累加到 `AgentTurnState.Usage`；终态提交带聚合 usage |
| `Desktop/Services/Transcript/*` | 快照 → 差分 → `WebViewHostChannel` 推给 Vue |
| `Core/Models/Usage/TurnUsage.cs` + `turn_usage` 表 | 每回合用量已持久化，含 `context_tokens` / `context_window_tokens` |

关键事实：**回合内增长已由回合内压缩约束（见 `direct-in-turn-compaction-design.md`），回合开始压缩不变**；本设计只负责回合开始时的裁剪点，它与回合内压缩共用策略、阈值与压缩器。

## 2. 目标与非目标

### 2.1 目标

1. 删除静默裁剪；上下文超限时改为模型压缩，且压缩结果可跨回合复用（不重复付费）。
2. 触发阈值在系统设置中可调（比例 + 无窗口模型的绝对兜底）。
3. 压缩模型默认跟随当前会话模型，可在设置中改指其他已启用模型。
4. 压缩产生事件，转录出现 `上下文压缩` 提示项（含摘要、被压缩消息数、压缩前后 token）。
5. 输入框区域常驻显示当前上下文大小与会话累计输入/输出 token。
6. token 计数不再依赖字节启发式：离线 tokenizer 计数 + provider 实测值校正。

### 2.2 非目标（本次不做，明确记录）

- **回合内压缩**：已另立设计（见 `direct-in-turn-compaction-design.md`），不在本设计范围内。
- **CLI 链路**：CLI 自行持有上下文与会话恢复，不接入本机制。
- **跨 provider 逐 token 精确**：Anthropic 未公开 tokenizer，统一用 OpenAI 编码近似（默认 `o200k_base`），不承诺与 provider 自身分词完全一致；实测口径仍以 provider 回报为准。
- **自动探测窗口**：模型窗口仍只来自模型配置（`ContextLength` / `context_window_tokens`）。
- **摘要质量自动评估、压缩成本单独计费展示**。

## 3. 用户可见行为契约

| 项 | 契约 |
| --- | --- |
| 触发 | 回合开始时，计数（或实测值）达到触发点即压缩一次；每回合最多一次 |
| 触发点 | 默认 = 模型窗口 × 75%，封顶在 `(窗口 − 输出预留) × 90%`；模型未声明窗口时用绝对阈值（默认 64 000 tokens）。它与界面“当前上下文”是同一把尺：读数走到触发点就压缩 |
| 保留 | 压缩后**整个 prompt**（system、工具定义、受保护消息都算在内）目标 ≤ 触发点 × 50%（常量，不开放设置） |
| 压缩模型 | 未指定 → 当前回合模型；已指定 → 该模型（设置时校验为已启用） |
| 摘要复用 | 摘要落库；下一回合只重放边界之后的内容，并把摘要作为运行时上下文注入。回合内压缩同样落库（边界可以落在 assistant 消息内部，§4.1） |
| 转录提示 | 出现 `上下文压缩` 分隔项，副标题显示被压缩消息数与 token，可展开摘要正文 |
| 关闭压缩 | 不再裁剪历史；若计数超过模型窗口，回合以可读错误失败（提示开启压缩），不静默丢历史 |
| 计数失败 | 本回合关闭压缩并给出诊断；**不回落字节估算**，也不因计数失败使回合失败 |
| 用量显示 | 输入框用量环显示 `当前上下文 / 窗口` 的占比，弹层给出 `累计 输入/输出`；数值来自 provider 实测，计数近似值加 `~` 前缀区分。启用压缩时环与进度条上各有一道**触发点刻度**（§8.3） |

适用范围：本机制对**所有 Direct 回合**生效（Interactive / Subagent / Continuation / Automation 各自用自己的会话历史）；CLI 回合不参与（CLI 自持上下文）。

## 4. 数据模型

### 4.1 Core DTO

```csharp
// SelfClaw.Core/Runtime/DirectCompressionPolicy.cs
public sealed record DirectCompressionPolicy(
    bool Enabled = true,
    double ThresholdRatio = 0.75d,        // 0.10–0.95
    int AbsoluteThresholdTokens = 64_000, // 模型未声明窗口时使用
    Guid? ModelProfileId = null);         // null = 跟随当前回合模型

// SelfClaw.Core/Models/Conversations/ConversationCompactionRecord.cs
public sealed record ConversationCompactionRecord(
    Guid Id,
    Guid ConversationId,
    Guid TurnId,
    long ThroughSequence,     // 覆盖到该消息 sequence（含）为止
    string Summary,
    string? Model,
    int SourceMessageCount,
    int? SourceTokens,
    int? SummaryTokens,
    DateTimeOffset CreatedAtUtc,
    Guid? ThroughToolRunId = null);   // 回合内压缩的锚点：边界之后那条 assistant 消息里，摘要覆盖到的最后一次工具调用
```

`TurnUsage` 增加一个可选字段（与 v32 同批落地）：

```csharp
// SelfClaw.Core/Models/Usage/TurnUsage.cs
public sealed record TurnUsage(
    ..., int? LastRequestInputTokens = null, ...);
```

它是“本回合最后一次 provider 请求的输入 token 数”，即真实上下文规模，用于两处：触发判定下限、界面“当前上下文大小”的首选来源。`ContextTokens`（input+output）保留原语义不动。

压缩边界由两个字段共同表达，重放 = 摘要 + 边界之后的内容：

- `ThroughSequence`：`sequence ≤ ThroughSequence` 的消息**整条**被摘要代表。不用“最新压缩项之前的一切”这类位置推断，因为压缩发生在当前回合中途，当前回合更早的消息必须保留。
- `ThroughToolRunId`（可空，v33）：一个回合的全部 assistant 输出存成**一条**消息，回合内压缩的边界几乎总落在这条消息中途，消息粒度表达不了。锚点指向该消息里摘要覆盖到的**最后一次工具调用**：它与它之前的调用都由摘要代表，只重放它之后的部分。锚点所在消息的 sequence 恒大于 `ThroughSequence`，所以只认 `ThroughSequence` 的读者（排序、转录分隔项定位、`Compose` 的整消息过滤）不需要知道锚点。
- 锚点是**软引用**（不建外键，与 `through_sequence` 一样）：解析不到（工具记录不在、段落里找不到）就当作没有锚点，后果只是多重放一些摘要里已有的内容，绝不丢历史；压缩记录也不会因此让回合提交失败。

“某次工具调用是否已被摘要覆盖”只有一个判定入口 `DirectHistoryReplay.IsCovered(messages, run, compression)`：消息在边界之前，或消息就是锚点所在消息且该调用的段序号不晚于锚点。任务计划快照（`DirectCompactionStep.TodoSection`）与技能激活是否仍在重放（`SkillCapabilitySource.FindReplayedActivations`）都走它，不得各自按整消息粒度判断。

### 4.2 Schema v32 → v33

v32 一次落地新表与 `turn_usage` 新列（其余表不变；沿用“拒绝而非迁移”的既有做法，旧库只读拒绝）。v33（C23）只在这张表上加一列可空的 `through_tool_run_id`，同样不写迁移：v32 库启动即被只读拒绝，文件不被修改。当前建表语句：

```sql
CREATE TABLE conversation_compactions (
    id TEXT NOT NULL PRIMARY KEY,
    conversation_id TEXT NOT NULL,
    turn_id TEXT NOT NULL,
    through_sequence INTEGER NOT NULL CHECK(through_sequence >= 0),
    summary TEXT NOT NULL,
    model TEXT NULL,
    source_message_count INTEGER NOT NULL,
    source_tokens INTEGER NULL,
    summary_tokens INTEGER NULL,
    created_at_utc TEXT NOT NULL,
    through_tool_run_id TEXT NULL,   -- v33；软引用，不建外键
    FOREIGN KEY(conversation_id) REFERENCES conversations(id) ON DELETE CASCADE,
    FOREIGN KEY(turn_id) REFERENCES conversation_turns(id) ON DELETE CASCADE
);
CREATE INDEX ix_conversation_compactions_conversation
    ON conversation_compactions(conversation_id, created_at_utc DESC);
```

`turn_usage.last_request_input_tokens` 是**目标建表语句里的一列**（无 `ALTER`、无回填、无缺列回退），因为旧库整体被拒绝。`SqliteSchema.RequiredTables` 增加 `conversation_compactions`，`CurrentSchemaVersion` 31 → 32（首轮实施）→ 33（C23）；`SqliteDatabase` 的拒绝文案引用该常量而不是写死版本号，`AGENTS.md` 的版本号随之同步。

想保留 v32 的现有数据，可以在应用关闭时对**备份副本**手工执行下面两句（不属于应用代码，应用永远不做迁移）：

```sql
ALTER TABLE conversation_compactions ADD COLUMN through_tool_run_id TEXT NULL;
UPDATE schema_versions SET version = 33;
```

### 4.3 承载与读取

| 层 | 变化 |
| --- | --- |
| `ConversationTranscriptSnapshot` | 增加 `IReadOnlyList<ConversationCompactionRecord>? Compactions = null`（C# 集合表达式不是编译期常量，无法写 `= []`；null 视为空，避免全量改调用点） |
| `ConversationRuntimeState` | 增加 compaction 列表与 `ReplaceCompaction`，纳入 `CaptureSnapshot` |
| `ConversationSessionCoordinator.LoadTranscriptAsync` | 并行加载 compactions |
| `SubagentTaskExecutor` / `SubagentContinuationExecutor` / `AutomationRunExecutor` / `ConversationTurnEngine` | 构造 Direct 请求时带上所属会话的最新 compaction |
| `DirectChatTurnRequest` | 增加 `ConversationCompactionRecord? Compression = null` |
| `IConversationCompactionRepository`（新，Core/Interfaces/Conversations） | `ListAsync(conversationId)`、`GetLatestAsync(conversationId)`；写入走回合提交（见 §4.4） |

### 4.4 写入路径

压缩项**随回合提交原子落库**，不单独开事务：

- `ConversationTurnCommit` 与 `RecordedTurnFinalizationRequest` 增加 `IReadOnlyList<ConversationCompactionRecord> Compactions`。
- `SqliteConversationTurnWriter.TryWriteAsync` 在同一事务内 `INSERT ... ON CONFLICT(id) DO UPDATE`（重试幂等），并校验会话/回合归属、`Summary` 非空、`ThroughSequence >= 0`、`SourceMessageCount >= 0`。
- 交互回合：`ConversationTurnRecorder` 收到压缩完成事件后写入 live state（分隔项立即可见），终态提交时落库。
- child / continuation 回合：走同一 writer（continuation 的 detached 回合在 `SubagentContinuationTurnCommitter` 的原子事务里一并提交），不新增第二条写入路径。
- 回合内压缩的记录走的也是这条路径（`direct-in-turn-compaction-design.md` §5.3）：一个回合只有**一个**回合内记录 id，同回合后续的回合内压缩在 `PendingCompactions` 里按 id 替换，upsert 连 `through_tool_run_id` 一起更新。writer 额外拒绝 `ThroughToolRunId == Guid.Empty`。

写入与读取的两条细则（首轮实施已按此落地）：

- `last_request_input_tokens` 在 upsert 中用 `COALESCE(excluded.last_request_input_tokens, turn_usage.last_request_input_tokens)`：该列描述“最后一次请求的输入规模”，null 表示未观测，不得抹掉已落库的实测值；其余列仍是快照覆盖。
- 重放取用最新压缩项时按 `through_sequence DESC, created_at_utc DESC, id DESC` 排序：重放语义由边界决定，时间只作次级 tie-break（当前边界随回合单调递增，两种排序等价，但语义以边界为准）。

好处：崩溃丢失压缩项时，最坏结果是下一回合重新压缩一次，不会留下“摘要与历史不一致”的半状态。

## 5. 计数、触发与预算

### 5.1 计数来源

| 来源 | 用途 | 说明 |
| --- | --- | --- |
| tokenizer 计数 | 触发判定、steer 准入、必需内容预留校验、摘要输入/输出规模 | §5.2；离线词表，运行期不联网 |
| provider 回报实测 | 回合开始的触发下限、界面“当前上下文大小” | `turn_usage.last_request_input_tokens`（v32 新增）；界面在它缺失时退回 `context_tokens`，触发下限不退回（§5.4） |

- 字节估算整体退场：不得用 `(UTF-8 字节数 + 2) / 3` 顶替计数；计数不可用时按 §5.5 降级。
- 框架开销用常量补偿（tiktoken 只计正文）：`PerMessageFramingTokens = 4`、`PerToolFramingTokens = 8`。

### 5.2 tokenizer 选择

- **依赖**：`Microsoft.ML.Tokenizers` + `Microsoft.ML.Tokenizers.Data.O200kBase` + `Microsoft.ML.Tokenizers.Data.Cl100kBase`（2026-10-17 在 nuget.org 核实，三者最新稳定版均为 **2.0.1**），版本集中在 `Directory.Packages.props`。数据包把词表以压缩形式嵌进程序集，运行时解压，**不下载、不联网**。落地确认（C4）：实际依赖与版本就是这三个包均 `2.0.1`，无额外 tokenizer 依赖；接口最终为 4 成员（`IsAvailable(model)` + 三个 `Count*(…, model)`），编码按**调用时**的模型解析，计数与指纹缓存按编码分桶。
- **API**（已在 2.0.1 的 XML 文档上核对）：`TiktokenTokenizer.CreateForEncoding("o200k_base")` / `CreateForModel("gpt-4o")` 返回基类 `Tokenizer`；计数用 `Tokenizer.CountTokens(string text)`（另有 `CountTokens(string, ReadOnlySpan<char>, EncodeSettings)` 与 `EncodeToIds` 等重载）。消息与工具定义逐块计数后加框架常量。
- **编码解析顺序**（按**每个回合的模型**解析，不是进程级）：模型 `ModelOptions["tokenizer.encoding"]` 显式指定 → 按模型名映射（`gpt-4o` / `o1` / `o3` 系 → `o200k_base`；`gpt-4` / `gpt-3.5` 系 → `cl100k_base`）→ 默认 `o200k_base`。
- **为什么不用 TiktokenSharp**：TiktokenSharp 在首次使用某个编码时从 `openaipublic.blob.core.windows.net` 下载 `.tiktoken` 词表并缓存到本地，离线场景需要预置文件并设置 `TikToken.PBEFileDirectory`，否则首次运行依赖外网且可能卡住。本产品是离线优先的桌面应用，不应把首次使用压在外部 blob 上。若坚持用 TiktokenSharp，必须随安装包预置三个词表并显式配置路径 —— 抽象层 `IDirectTokenCounter` 允许替换实现而不动其余代码。
- **准确性边界**：`o200k_base` / `cl100k_base` 是 OpenAI 的编码。OpenAI 系模型基本精确（差异来自框架开销）；Claude 只是近似，所以阈值保留 25% 余量，并以 provider 实测值校正下一回合的判断。

### 5.3 计数成本控制

- 上下文 token 数**增量维护**：回合开始时全量计数一次，此后每追加一条消息或一次工具结果只计增量，不重复全量扫描。
- 单条消息计数按 `(MessageId, 内容指纹)` 缓存（上限 4096 条，先进先出淘汰）；工具定义按 schema 文本指纹缓存。缓存只在内存，不落库、不产生 SQL。
- 压缩摘要的 `SummaryTokens` 是对单条摘要文本的一次计数（每回合最多一次，编码按**产出摘要的模型**解析），同样走上面这层缓存（identity 为空、指纹为摘要内容），不额外扫历史。
- 计数不做 per-token SQL，不写库。

### 5.4 触发判定

在 `DirectTurnPreparer.PrepareAsync` → `DirectCompactionStep.CompactTurnStartAsync`（回合开始、首次 provider 请求之前）判定一次：

```text
window        = AiChatOptions.ResolveContextWindowTokens(profile)          // 可能为 null
outputReserve = options.MaxOutputTokens ?? DefaultOutputTokenReserve(4096)
trigger       = window is int w
                  ? max(0, min(w * policy.ThresholdRatio, (w - outputReserve) * UsableInputShare(0.9)))
                  : policy.AbsoluteThresholdTokens                          // DirectCompactionPlanner.ResolveTrigger，唯一公式
promptTokens  = Count(system + tools + 全部可重放历史 + 受保护尾部)        // tokenizer；**不含**输出预留，界面“上下文大小”同口径
observed      = 紧邻上一回合（须为 Direct 回合）的 turn_usage.last_request_input_tokens（可能为 null）
触发：max(promptTokens, observed ?? 0) ≥ trigger
```

- **触发点与界面读数是同一把尺**（C23）。输出预留不加到计数上，只通过 `(window − outputReserve) × 0.9` 给触发点封顶：触发点永远不高于“还放得下一次完整输出”的位置，而用户配置的比例在绝大多数模型上原样生效。旧公式是 `promptTokens + outputReserve ≥ window × ratio`，对最大输出占窗口很大一块的模型，实际触发点远低于配置比例，还低于压缩后的保留量，于是压完立刻又满足触发条件（根因与实测数字见实施文档 C23）。
- `UsableInputShare = 0.9`：本地计数比 provider 实测低几个百分点（tokenizer 不同、协议帧开销），触发点贴着硬上限会放行一个 provider 必然拒绝的请求。
- 受保护尾部（continuation 的 completion batch，≤ 64 KiB）计入 `promptTokens`：`DirectTurnPreparer` 只构建一次，计数和发送用的是同一份，被计数的 prompt 就是被发送的 prompt。
- `observed` 与本地计数取较大值：tokenizer 受编码匹配度影响，实测值受“上一次请求内容与本次不同”影响；取大更保守，宁可早压不可超窗。本地计数对图片只计一个常数（`PerImageTokens`），实测下限正是它偏低时的校正：界面读数（实测）过了触发点，下一回合就压缩。
- `observed` 只取**紧邻的上一回合**，且该回合必须是 Direct 回合：它是那一回合最后一次 provider 请求的输入规模，也就是界面显示的上下文大小。**不向更早的回合回溯**：更早的实测值可能早于现有压缩边界，会让已经压过的会话再压一次。上一回合没有上报（未发出请求即失败）或是 CLI 回合（它实测的是 CLI 自己的会话，不是 Direct 重放的 prompt）时没有下限，判定只看本地计数。数据来自请求已携带的 `Turns`，不新增 SQL。
- `observed` 只决定“要不要压”，不决定“压多少”：前缀与保留仍按本地单元计数切分（§5.6）。实测值可能高于本回合真实 prompt（上一回合最后一次请求里有不再重放的内容），此时若全部单元都落在保留目标内，规划直接拒绝，不产生模型调用。`ContextCompactionStartedEvent.EstimatedTokens` 也仍是本地计数（§8.1）。
- 回合内检查点调用同一个 `ResolveTrigger`，也有自己的实测下限，但来源不同（`direct-in-turn-compaction-design.md` §3.2）：它用的是**本回合上一次请求**的实测输入，不是这里的 `observed`（上一回合的最后一次请求）。
- **窗口校验不变**，仍是 `promptTokens + outputReserve ≤ window`：`policy.Enabled == false` → 不压缩、不裁剪，超窗时抛可读错误（`InvalidDataException`），说明历史超出模型窗口、提示开启压缩或换更大窗口的模型。触发点恒不高于可用输入，所以“先压缩、再校验”的顺序保证不变。
- 压缩后重算：**必须把新的 system 段（含摘要 section）算进去**，若仍 `promptTokens + outputReserve > window` → 抛同类错误（压缩没能解决，不静默发送超限请求）。

各配置下的触发点（比例 0.5）：

| 窗口 / 最大输出 | 旧公式的实际触发点 | 现在的触发点 | 压完约 |
| --- | --- | --- | --- |
| 1,048,576 / 384,000 | 140,288（13.4%） | 524,288（50%） | 25% |
| 1,000,000 / 393,216 | 106,784（10.7%） | 500,000（50%） | 25% |
| 1,048,576 / 65,536 | 458,752（43.7%） | 524,288（50%） | 25% |
| 未声明窗口 | 绝对阈值 − 输出上限 | 绝对阈值 | 一半 |

封顶何时生效：最大输出 384,000、窗口 1,048,576 的模型可用输入只有约 664K，比例高于约 0.57 时触发点停在 598K；想用满窗口只能调低该模型的最大输出。

### 5.5 计数不可用时的降级

tokenizer 初始化失败（数据包缺失、程序集不匹配等，**按编码**分别判定）：

1. 该编码记一次 warning（含失败原因，不含正文），并向活动区发一条诊断状态。
2. **本回合关闭压缩**：不压缩、不裁剪、不报错，按完整上下文发送。
3. provider 实测值不能单独触发压缩：没有单元计数就无法规划前缀（§5.6），实测下限随计数一起失效。界面“当前上下文大小”仍显示实测值，实测缺失时显示为不可用，不显示估算数字。
4. 不引入字节估算兜底路径；不因计数失败使回合失败。

### 5.6 保留策略（常量，不进设置）

```text
fixedTokens = max(0, promptTokens - Σ候选单元 token)        // 摘要替换不掉的部分：system（含旧摘要）、tools、受保护消息
keepTarget  = max(0, trigger * 0.5 - fixedTokens)           // RetainedContextRatio，针对整个 prompt
保留最新单元直到累计计数 ≤ keepTarget；至少保留最新 1 个单元
其余更旧单元组成待压缩前缀 prefix
prefix 为空 → 不压缩（无可压缩内容）
throughSequence = prefix 最后一个单元对应的消息 sequence
```

保留目标针对**整个 prompt**而不是历史本身（C23）：压完之后的 prompt 约为触发点的一半，距离下一次触发留有另一半的增长空间。旧公式 `keepTarget = threshold × 0.5` 只量历史单元，固定开销大的时候压完的 prompt 仍然贴着触发点。由此得到一条可测的不变量：**对规划结果应用后再规划一次，必须拒绝**（不会立刻重触发）。唯一的例外是“最新单元必留”：它独自超过保留目标时仍然保留，此时由窗口校验兜底。

没有不可压缩的单元类型：含图单元（用户附件、读图的工具往返）与普通单元同等进入候选集与前缀，图片在摘要请求里换成文本占位（§6 规则 2）。把含图单元排除在规划之外的两种做法都不可行，理由见 `input-attachment-design.md` §2 第 2 条。

## 6. 压缩执行

新增 `SelfClaw.Infrastructure/Agents/Direct/Context/DirectContextCompressor.cs`（`internal sealed`，构造注入 `IAiChatClientFactory`、`IAiModelCatalog`、`IDirectTokenCounter` 与 `ILogger`）；两个 DTO 各自独立成文件、放 `Context/Models/`（AGENTS 约定 1）：

```csharp
internal sealed record DirectCompressionRequest(
    IReadOnlyList<DirectPromptHistoryUnit> PrefixUnits,
    string? PreviousSummary,
    int SourceTokens,
    Guid? ModelProfileId,
    AiChatClientLease? TurnLease,
    Guid ConversationId,                       // 仅用于日志：规则 5 要求 warning 含会话/回合
    Guid TurnId);            // 未指定模型时复用本回合 lease

internal sealed record DirectCompressionResult(
    string Summary,
    string? Model,
    int SourceMessageCount,
    int? SourceTokens,
    int? SummaryTokens,
    bool UsedFallbackModel = false);          // 指定模型不可用而回退时置 true，由 C7 据此发诊断

internal sealed class DirectContextCompressor
{
    internal Task<DirectCompressionResult?> TryCompressAsync(
        DirectCompressionRequest request, CancellationToken cancellationToken);
}
```

三条语义钉死（否则实现会各写各的）：

- `SourceTokens` 是**被压缩前缀自身**的 token 数（由 `PlanCompression` 累加前缀单元得到），不是整个 prompt 的计数。输出预算的 `/4` 与界面“压缩前 token”都以它为准。
- `SourceMessageCount` 是**会话消息条数**（= `PrefixUnits.Count`）：一个单元对应一条 `MessageRecord`，而单元内部的工具调用与结果会被展开成多条 `ChatMessage`，按 `unit.Messages.Count` 求和会虚高，界面数字也会与边界语义不符。
- `SummaryTokens` 由压缩器用实际产出摘要的那个 lease 的 `Profile`（经 `IDirectTokenCounter`）算出，计数不可用时为 null。界面“压缩前 → 压缩后”的后半靠它，不能恒为 null。

规则：

1. **模型解析**：`ModelProfileId` 非空 → `IAiChatClientFactory.PrepareAsync(id)` + `Create(preparation, new AiChatClientPipelineOptions([]))`，用完释放 lease；为空 → 复用本回合 `AiChatClientLease.Client`（同一 HTTP 客户端，另传一次 `ChatOptions`，不携带工具）。指定模型不可用（禁用/删除）→ 回退到本回合模型、置 `UsedFallbackModel = true`，由 C7 据此发一条诊断 `RunStatusEvent`，不失败回合。
2. **输入构造**：`[system: DirectCompressionInstruction]` +（有旧摘要时）`[user: <selfclaw-context-summary version="1">…</>]` + 前缀单元的原始 `ChatMessage` 序列（角色、工具调用与结果原样保留，单元边界保证调用/结果成对）。唯一的改写是图片：顶层 `DataContent`、tool result 内嵌的图片与 follow-up user 消息里的图片一律换成 `[image: name]` 文本（`PlaceholderizeAttachments`）。压缩模型不一定支持图片，原图也会撑爆它自己的窗口；含图单元和普通单元一样进入前缀，所以这是摘要请求不含二进制的唯一保证。
3. **输出约束**：`ChatOptions { MaxOutputTokens = clamp(previousSummaryTokens + sourceTokens / 4, 512, 4096) }`，其中 `sourceTokens` 为**待压缩前缀自身**的计数（由 `PlanCompression` 传入，不是整个 prompt 的计数），`previousSummaryTokens` 是旧摘要按产出模型的计数（不可用时按 0）。旧摘要必须算进预算（C23）：链式压缩要先把旧摘要的全部内容带过去才谈得上新增，只按新前缀定预算时，一个小前缀会把累积的摘要整体挤过 512 的下限，模型逐次失忆（实测摘要从 240 缩到 118 tokens，同时还要吸收 5.6K 新内容）。其余采样走模型/provider 默认。空或全空白响应视为失败。
4. **分块链式摘要**（C23）：前缀超过摘要模型自己的可用输入时切成多块，上一块的摘要作为下一块的 `PreviousSummary`。
   - 何时会用到：经历过回合内压缩的回合，存下来的那条 assistant 消息可以比一个窗口还大；它的锚点一旦失效（§4.1），下一回合就要把整条消息交给摘要模型，单次请求必然失败，而摘要失败 + 窗口校验会让这个会话再也开不了新回合。
   - 块预算 = `(window − 2 × MaximumSummaryTokens) × UsableInputShare − 指令 token`，窗口取**产出摘要的模型**的声明值：留出回答与每一块都要携带的旧摘要。
   - 切分点只能落在非 `ChatRole.Tool` 消息之前，调用与结果永不分离；单条消息自身超预算时仍整条发送（不裁剪）。
   - 摘要模型未声明窗口、计数不可用、或整体放得下时仍是**单次请求**（即原有行为）。
   - 任一块失败则整体返回 null，不应用任何部分结果：半个前缀的摘要等于悄悄丢掉另一半。
5. **失败降级**：单次尝试、不重试；失败只记一条 warning（含会话 / 回合 id，不含正文）并发 `ContextCompactionFailedEvent`，回合继续按未压缩上下文发送；若因此超过窗口，由 §5.4 的错误路径给出可读失败，**绝不静默裁剪**。
6. **摘要指令**（固定文案）要求保留：用户目标与约束、已确认的决定、涉及的文件路径与命令、未完成事项与已知错误、关键结论；丢弃寒暄、重复与已被推翻的中间尝试；不得引入新事实；使用简洁书面语，可用列表。图片以 `[image: name]` 占位出现：要求保留其文件名 / 路径与会话里已得出的结论，并写明图片本身已不在会话中（模型据此知道要再看得重新读取或请用户重发）。

## 7. Prompt 组装

`DirectPromptComposer`（现为 `DirectCompactionPlanner.PlanCompression` 与 `DirectHistoryReplay.BuildUnits` / `Compose`、`DirectSystemPrompt.BuildFixedText` / `ComposeSystemText`）拆成纯函数（均无 I/O）：

```csharp
// 规划：是否需要压缩、压缩哪个前缀（DirectCompactionPlanner）
internal DirectCompressionPlan PlanCompression(
    IReadOnlyList<DirectPromptHistoryUnit> units,
    ConversationCompactionRecord? existing,
    DirectCompressionPolicy policy,
    DirectPromptBudget budget,
    int? countedTokens,         // §5.4 的 promptTokens，由调用方传入；null = 计数不可用
    int? observedTokens = null);

// 组装：边界之后的历史 + 必需消息（`tools` / `countedTokens` / `budget` 三个参数都已删除：
// 计数与窗口校验都归调用方，composer 不读窗口）。原 BuildMessages 已删除，现为 DirectHistoryReplay.Compose
internal static DirectPromptBuild Compose(
    units, fixedSystemText, trailingProtectedMessages, ConversationCompactionRecord? compression);

// 供调用方计数用（composer 自己不计数）。compression 的锚点在这里把所在消息截短
internal static IReadOnlyList<DirectPromptHistoryUnit> BuildUnits(..., ConversationCompactionRecord? compression = null);  // DirectHistoryReplay
internal static string? BuildFixedText(agentInstructions, systemInstructions, executionContext);                         // DirectSystemPrompt
internal static string? ComposeSystemText(string? fixedText, string? summary, string? planSection);                      // DirectSystemPrompt
```

规则：

- **重放边界**：只重放 `Sequence > compression.ThroughSequence` 的可重放消息；更旧的被摘要代表。压缩记录带锚点时，`BuildUnits` 在重建锚点所在的 assistant 消息时走到锚点工具调用就清空已累积的内容，只留其后的部分；锚点按**段**匹配而不是按可重放的工具记录匹配，所以锚点调用自己没有到达终态时切点不变。截完为空的消息不产生单元。
- **摘要注入**：作为独立 system section，包裹在 `<selfclaw-context-summary version="1">…</selfclaw-context-summary>` 中，并附一行说明“这是更早对话的压缩摘要，替代已省略的历史”。放在 system 段而不是 user 消息：它是运行时提供的上下文，不是用户输入（与 `CompletionInstruction` 先例一致）。
- **一个请求只有一个摘要 section**（C23）。system 文本的顺序固定为：**固定文本**（agent instructions → 有 completion batch 时的 `CompletionInstruction` → capability system instructions）→ **摘要** → **任务计划**。摘要与计划是仅有的两段回合内会变的内容，放在固定文本之后，固定文本因此在整个回合里逐字节不变（provider 的前缀缓存不被摘要更新打断）；无压缩时的字节布局与旧实现一致。不论摘要来自已落库的记录、回合开始压缩还是回合内压缩，它都是同一个 section：回合内压缩把请求当前的摘要作为 `PreviousSummary` 折进新摘要并**替换**它。这是落库的前提——一条记录的摘要必须能独立代表边界之前的一切。旧布局（回合开始的摘要在能力指令之前、回合内摘要追加在末尾、两者并存）已删除。
- 计数与发送共用 `ComposeSystemText`：`DirectCompactionStep` 用它算 `promptTokens`，`DirectConversationContext.Recompose` 用它生成请求里的 system 消息，被计数的文本就是被发送的文本。
- **裁剪移除**：`BuildHistoryWithinBudget` 与“最新消息装不下就丢弃其余”的路径删除。
- **保留的必要校验**：`counted(system + tools) + outputReserve > window` → 抛既有可读错误（这条与用户历史无关，属于配置错误）；计数不可用时跳过该检查（fail-open），由 provider 报错。⚠ 落地归属（C7 定）：这条检查的唯一归属是 **`DirectCompactionStep`**（`EnsureTurnStartPromptFitsWindow`，文案要带“开启压缩 / 降低输出上限 / 换更大窗口”的指引），composer **不再重复同一条校验**——`DirectTurnPreparer` 用同一份计数在 `Compose` 之前先跑，composer 里那份永远不可达，属重复实现（AGENTS 约定 10）。composer 只保留与计数无关的校验（completion batch 的 64 KiB 上限、历史顺序与归属校验）。
- completion batch、steer 批次的语义与顺序不变。
- **计数不再是 composer 的职责**：`EstimateTokens` 字节估算与其派生方法删除；`DirectTurnRun.FitsBudget` 改用 `DirectPromptMeter`（底层 `IDirectTokenCounter`）与增量维护的上下文计数。
- **回合内响应的 reasoning 计入**：`DirectTokenCounter` 把 `TextReasoningContent.Text` 计入所在消息。落库历史重放不带 reasoning，所以这只影响回合内上下文——三种协议都会把本回合的 reasoning 随工具结果一起发回去，不计就会让长工具回合显得比实际发送的请求小。签名（`ProtectedData`）仍然不可计，由回合内的 provider 实测下限兜住（`direct-in-turn-compaction-design.md` §3.2）。
- **计数的口径必须是 provider 收到的文本**（C18 修）：`DirectTokenCounter` 把 `FunctionCallContent.Arguments` / `FunctionResultContent.Result` / 工具 `AdditionalProperties` 序列化成 JSON 后计数，所以序列化器必须用 `JavaScriptEncoder.UnsafeRelaxedJsonEscaping`，且**字符串片段不序列化**（它本来就是 provider 收到的原文，序列化只会多一对引号）。用默认编码器时中文被写成 `\uXXXX`、`< > &` 被写成 `\u003C` 一类转义，实测把代码/中文密集的工具结果放大约 **1.6 倍**：状态栏在“回合开始估算 → 回合结束实测”之间来回跳，压缩也会提前触发（1M 窗口 0.75 阈值下真实约 480K 就压）。这条口径同时影响 `PlanCompression`、`FitsBudget` 与 `EnsureTurnStartPromptFitsWindow` / `EnsureInTurnPromptFitsWindow`，改错不只是显示问题。
- **单元形状**：`DirectPromptHistoryUnit(Messages, SourceSequence, int? Tokens = null)`。一个单元 = 一条存储消息（工具调用与其结果同单元，边界只能落在单元之间），`Tokens` 由调用方计数后回填，`SourceTokens` = 前缀单元的计数之和。`PlanCompression` 之所以收“已计数的单元”而不是原始入参：保留比例与 `SourceTokens` 都需要每单元的计数，而“composer 不计数”这条约束必须保住。
- **物化范围**：规划要知道前缀，所以 composer 会重建**全部**可重放单元（旧实现那种“只回看 replay 需要的工具记录”的懒索引随裁剪一起删除，这是有意的）。已有压缩项时，边界及更早的消息既不被重放也无需计数，可在建单元与计数时直接从边界之后开始；**首次**压缩仍需全量扫描。

## 8. 事件与前端

### 8.1 新事件（Core/Runtime/Agent）

```csharp
public sealed record ContextCompactionStartedEvent(int EstimatedTokens, int ThresholdTokens) : AgentStreamEvent;
public sealed record ContextCompactionCompletedEvent(ConversationCompactionRecord Compaction) : AgentStreamEvent;
public sealed record ContextCompactionFailedEvent(string Reason) : AgentStreamEvent;
public sealed record ContextCompactionTriggerResolvedEvent(int TriggerTokens) : AgentStreamEvent;   // C23
```

`Started` 的两个数字口径：`EstimatedTokens` = `promptTokens`（与界面“上下文大小”同口径，不含输出预留），`ThresholdTokens` = `trigger`（§5.4 的触发点，`DirectCompressionPlan.ThresholdTokens` 承载的也是它）。两者同口径，可以直接比较。

`TriggerResolved` 只服务用量环：`DirectTurnPreparer` 读到策略后，若 `policy.Enabled` 就发布本回合的触发点（与 `Started.ThresholdTokens` 是同一个 `ResolveTrigger` 的结果）。压缩关闭的回合与 CLI 回合不发。它只存内存、不落库（§8.3）。

`AgentRunStatus` 追加 `Compacting = 4`；`ConversationTurnRecorder.MapRunStatusText` 映射为“正在压缩上下文…”，`AgentActivityCoordinator.MapRunStatus` 映射为 `(Thinking, "正在压缩上下文")`。

### 8.2 录制

`ConversationTurnRecorder.ApplyEventCoreAsync` 增加分支：

- `Started`：`session.ActivityText` 更新 + 立刻发布。
- `Completed`：写入 `AgentTurnState.PendingCompactions` 并 `session.ReplaceCompaction(record)`，`RaiseTranscriptChanged(true)`（分隔项立即可见）。
- `Failed`：只写日志与活动文案，转录不出现误导性提示。
- `InTurnContextCompactionCompletedEvent`：事件带记录时，把 `AnchorCallId` 经 `turn.ToolRunsByCallId` 解析成落库的 tool run id 写进记录（解析不到就不带锚点），在 `PendingCompactions` 里按 id 替换，`session.ReplaceCompaction` 后立即发布；不带记录时只记日志（`direct-in-turn-compaction-design.md` §5.3）。
- `TriggerResolved`：写入瞬态的 `ConversationRuntimeState.CompactionTriggerTokens` 并 `RaiseTranscriptChanged(false)`。

### 8.3 投影

- 新增渲染项：`TranscriptRenderItem(Kind: "compression", Role: "system", Status: "sealed", …)`，载荷 `TranscriptCompactionNotice(CompactionId, MessageCount, SourceTokens, SummaryTokens, Model, Summary)`。
- 排序规则：插入到 `sequence == ThroughSequence` 的消息项之后；多项按 `ThroughSequence` 升序，同值按 `CreatedAtUtc`。回合内压缩的记录一旦覆盖到本回合自己的输出，它的 `ThroughSequence` 就是回合开始时已落库的最大 sequence，分隔项因此显示在**该回合用户消息之后**，同一回合压多少次都只有这一条；`MessageCount` 取被覆盖的存储消息数，部分覆盖的那条 assistant 消息计 1。
- 用量摘要：`TranscriptProjection` 从 `request.Turns` + `request.LiveUsage` + `request.CompactionTriggerTokens` 计算 `TranscriptUsageSummary(long InputTokens, long OutputTokens, int? ContextTokens, int? ContextWindowTokens, bool ContextIsEstimated, long? CachedInputTokens, int? CompactionTriggerTokens)`：
  - 累计输入/输出 = 本会话所有回合 `Usage` 求和（子代理用量不并入）。
  - 当前上下文 = 最新非空 `LastRequestInputTokens`（实测）→ 缺失时用最新非空 `ContextTokens` 并置 `ContextIsEstimated = false`（同为实测口径）→ 都没有时置空，由前端显示不可用。
  - 运行中的回合还带一份实时快照（`UsageProgressEvent` → `ConversationRuntimeState.LiveUsage`）：它**替换**该回合已落库的 `Usage`，因为边界提交可能写过同一累加器的更早快照，相加会重复计数；快照缺少的分项保留落库值。只有快照的 `IsEstimated` 为 true（本地计数）时 `ContextIsEstimated` 才置 true，前端据此显示 `~` 与近似说明。
  - 该字段放入 `TranscriptRenderState`，`replaceState` 与 `patchState` 载荷都要带。
  - **触发点刻度**（C23，取代原先“不要做距离阈值对比”的警告）：`CompactionTriggerTokens` 与 `ContextTokens` 同口径（§5.4），所以界面可以直接把它画成刻度——读数走到刻度就压缩。口径一致之前这做不到：旧触发判定多加一个输出预留，界面上看不出离压缩还有多远，这正是 C23 的用户现象之一。
    - 触发点由宿主算好下发，**前端不复制公式**。
    - 只存内存：运行中的回合取 `ConversationRuntimeState.CompactionTriggerTokens`；回合结束后 `ConversationSessionCoordinator` 按会话缓存上一回合的值（`_selectionGate` 保护，`ForgetTranscript` 时清除），所以回合之间刻度仍在。没解析出触发点的回合（压缩关闭、CLI 回合）会清掉缓存，而不是留下更早回合的值。schema 不为它改动。
    - 时效：改了压缩设置后刻度的位置要到下一回合才更新；应用重启后要等该会话跑过一个回合才出现。关闭压缩例外——前端订阅了 `direct-compression/state`，开关一关刻度立即消失（§8.4）。

### 8.4 Vue

| 文件 | 变化 |
| --- | --- |
| `components/chat/transcript/CompressionNotice.vue`（新） | `---上下文压缩---` 分隔线 + 副标题（`已压缩 N 条消息 · 48.2k → 1.2k tokens`）+ 摘要折叠展开 |
| `views/ChatView.vue` / `components/chat/TranscriptPanel.vue` | `item.kind === 'compression'` 分支（与 `turn-outcome` 同级） |
| `renderers/pluginTranscript.js` | 新 kind 的容忍路径（超限时退化为带元数据的精简项，不崩、不误当正文） |
| `composables/useChatTranscript.js` | state 增加 `usage` |
| `components/chat/composer/ComposerUsageMeter.vue` | 输入框工具行里的用量环（占比 + 色调）与弹层的开合、定位；启用压缩且已知触发点时在环上画一道径向刻度 |
| `components/chat/composer/ComposerUsagePopover.vue`（C23 拆出） | 弹层内容与样式：当前上下文 / 上限、缓存命中率、进度条（含触发点标记）、`自动压缩 524.29K 50%` 一行、会话累计 |
| `composables/useCompressionThreshold.js` | 订阅 `direct-compression/state`，暴露绝对阈值（无窗口模型的进度上限）与 `enabled`（关闭时不画刻度） |
| `renderers/usageMeter.js`（新） | `formatTokenCount` / `calculateUsageProgress` / `ringTick` / `formatCompressionNotice` 纯格式化与几何；设置页与输入框共用同一份缩写 |

格式化口径（C13/C14 定，均在这个 renderer 里，不在组件里）：

- `formatTokenCount`：≥ 1M 用 `M`、≥ 1K 用 `K`，最多两位小数且不带千位分隔；已是 `32K` / `1.2M` 形态的字符串原样返回；未知与非正值返回 `—`。量级按**格式化之后**回判（999999 四舍五入成 `1000` 时必须进位为 `1M`，不得输出 `1000K`）。
- 累计输入/输出：未知时显示 `—`。
- 上下文：尺寸未知时显示 `不可用` 而不给数字；`contextIsEstimated` 只为尺寸加 `~` 并附近似说明，窗口是模型声明值，不加 `~`。
- **色调**（C23）：已知触发点时按 `当前上下文 / 触发点` 上色——≥ 85% 为 warm、≥ 100% 为 critical，表达的是“离下一次压缩还有多远”；触发点未知或压缩关闭时退回按窗口饱和度上色（65% / 85%）。
- **触发点刻度**（C23）：只在模型声明了窗口且触发点小于窗口时画（环上的径向刻度、进度条上的标记、弹层里的 `自动压缩` 行三者同现同隐）。没有声明窗口时进度的上限本身就是绝对阈值，再画一道刻度只是重复，但色调仍按触发点算。
- **缓存命中率**（C21）：`cachedInputTokens / inputTokens` 的百分比，最多一位小数，只在上下文尺寸那一行的右侧以纯数字加 `%` 呈现，不加标签文字。缓存量或输入量为空/为零时整个不渲染——`0%` 与"未上报"是两种不同的意思，不能混为一谈；超过 100% 时 clamp 到 `100%`（provider 分项与总数口径不一时不至于显示荒谬值）。数据来自 `TranscriptUsageSummary.CachedInputTokens`，按回合求和，与它所隶属的 `InputTokens` 同口径。
- **不再有 `按模型声明的上下文窗口计算` 这种说明行**（C21 删除）：窗口含义已由行内的 `上下文窗口` / `绝对阈值` 标签表达，重复解释只增加视觉噪声。
- 压缩分隔项：副标题四态（源+摘要 / 只有源 / 只有摘要 / 只有消息数）；无摘要时退化为非交互行且不给 `aria-expanded`；`Model` 进 `title`，摘要走 `renderMarkdown` 后再注入。

## 9. 设置存储与桥接

### 9.1 存储

真值在 `desktop-settings.json` 的 `direct_compression` 节点（与 `appearance`、`programming_assistant` 同构）：原子写入 + 已确认缓存。

`SelfClaw.Desktop/Services/Compression/DirectCompressionSettingsService.cs`（按功能分目录，与 `Services/Appearance/*` 同风格）：

- `GetAsync()`（缓存 + `Normalize`：比例 clamp 到 [0.10, 0.95]，绝对阈值 clamp 到 [4 000, 1 000 000]，`NaN` 回落默认，`ModelProfileId` 不可用时归零）
- `SaveAsync(policy)`（归一化后写盘、更新缓存、触发 `Changed`）
- 实现 `SelfClaw.Core/Interfaces/Agents/IDirectCompressionPolicySource`

### 9.2 Infrastructure 如何读到设置

`DirectAgentChatRuntime` 依赖 Core 接口 `IDirectCompressionPolicySource`（先例：`IToolApprovalHandler` 由 Desktop 实现、Infrastructure 消费）：

- `SelfClaw.Infrastructure` 注册默认实现 `DisabledDirectCompressionPolicySource`（返回 `Enabled = false`）。
- `SelfClaw.Desktop` 注册 `DirectCompressionSettingsService` 为同一接口的实现；MS DI 单服务解析取最后一次注册，`App.xaml.cs` 中 Infrastructure 在 Desktop 之前，因此 Desktop 的实现生效。
- 后果：任何未加载 Desktop 组合的宿主/测试默认关闭压缩，行为安全。

### 9.3 WebView 协议

| 消息 | 载荷 |
| --- | --- |
| `direct-compression/get` | requestId → `{ type: "direct-compression/state", requestId, policy }` |
| `direct-compression/save` | requestId、policy → 同上（校验失败返回 `error`，不落盘；归一化后的值回显） |
| `direct-compression/state`（宿主推送） | 设置变更后广播，供设置页与其他消费者刷新 |

`WebViewMessageRouter` 增加 `type.StartsWith("direct-compression/")` 分支调用新 bridge。

### 9.4 设置页

- `views/SettingsView.vue`：AI 分组新增导航项 `{ id: 'compression', label: '上下文压缩', en: 'COMPRESSION', icon: <lucide 图标> }`，映射到 `components/settings/compression/Compression.vue`（`defineAsyncComponent`）。已核实导航项形状是 `{ id, label, en, icon }`，`icon` 必填（同组其它项都有），组件映射是同文件里的一个 `defineAsyncComponent(() => import(...))` 表。
- `components/settings/compression/Compression.vue` + `composables/useDirectCompression.js`：启用开关、触发比例（10–95%）、无窗口模型绝对阈值、压缩模型下拉（默认「跟随当前会话模型」+ `ai-providers/list-enabled-models`）。
- 样式沿用 `styles/settings-console.css`（`sc-root` / `sc-page` / `sc-page-head` / `sc-page-body`）与 lucide 图标。

## 10. 不变量与删除清单

### 10.1 不变量

| ID | 不变量 |
| --- | --- |
| C01 | 任何路径都不得静默丢弃用户可见历史：要么完整发送，要么压缩并有可见提示 |
| C02 | 压缩边界是已持久化消息的 sequence，外加可选的工具调用锚点（§4.1）；被摘要的内容不再单独重放。锚点失效只会多重放，不会少重放 |
| C03 | 压缩项与回合终态在同一事务提交；重试幂等，不产生重复边界 |
| C04 | 压缩失败不影响回合终态语义；超窗时以可读错误失败 |
| C05 | 子代理 completion batch、steer 批次仍为受保护内容，不被压缩或丢弃 |
| C06 | 每回合开始最多一次压缩（不变）；回合内维护性压缩 ≤ `MaximumInTurnCompactionsPerTurn`（10 次），不裁剪，随回合终态落库为**一条**记录（见 `direct-in-turn-compaction-design.md`） |
| C07 | 压缩使用本回合 lease 或独立 lease，两者都在回合结束前释放 |
| C08 | 设置不可用时回落默认策略；模型不可用时回退本回合模型并留诊断 |
| C09 | 计数只有一个来源：tokenizer 计数 + provider 实测；字节估算不得作为触发依据 |
| C10 | 计数失败只降级为“本回合不压缩”，不得使回合失败，也不得改用估算 |
| C11 | 计数成本有界：增量维护 + 有界缓存，不重复全量扫描、不产生 SQL |
| C12 | 界面区分实测与近似：实测值直接显示，计数近似值加 `~`；不可用时显示不可用而不是数字 |
| C13 | 没有不可压缩的单元类型：含图单元与普通单元同等进入前缀；摘要请求不含二进制内容 |
| C14 | 触发点只有一个公式（`DirectCompactionPlanner.ResolveTrigger`），与界面“当前上下文”同口径；回合开始、回合内检查点与用量环刻度都取自它，前端不复制公式 |
| C15 | 压缩不自激：对规划结果应用后再规划必须拒绝（保留目标针对整个 prompt）；唯一例外是独自超过保留目标的最新单元 |
| C16 | 一个请求只有一个摘要 section，位于固定 system 文本之后；一条落库的摘要独立代表其边界之前的一切 |
| C17 | 摘要不出部分结果：分块链式摘要任一块失败即整体失败，不应用已完成的块 |

### 10.2 删除清单（完成后不得残留）

| 内容 | 处置 |
| --- | --- |
| `DirectPromptComposer.BuildHistoryWithinBudget`（该类型现为 `DirectHistoryReplay` 等） | 删除，由 `PlanCompression` + 边界重放取代 |
| `EstimateTokens` / `EstimateMessageTokens` / `EstimateToolTokens` / `EstimateUserPrompt` 等字节估算 | 删除，由 `IDirectTokenCounter` 取代 |
| 「最新消息装不下即报错并丢弃其余历史」的分支 | 删除，由压缩 + 窗口校验取代 |
| `DirectPromptBudget` 的“历史裁剪预算”语义 | 保留类型（窗口与输出预留仍需），注释与语义改为“限制与输出预留”，不再代表可裁剪额度 |
| README/AGENTS 中“按 UTF-8 估算裁剪历史”的描述 | 改为“按 tokenizer 计数触发压缩” |

## 11. 风险与未决项

| 风险 | 说明 | 缓解 |
| --- | --- | --- |
| 编码不匹配 | Claude 只能近似（OpenAI 编码） | 触发点封顶在可用输入的 90%；provider 实测值作为下一回合的下限 |
| 图片计数偏低 | 单图按常数 1600 计；高分辨率档的大图实际可达 4,784 | 回合开始触发以上一回合的 provider 实测值为下限（§5.4）；保留多少仍按本地计数切分，图片极密集的会话仍可能走到可读的超窗错误 |
| 图片被摘要 | 含图消息滑出保留尾部后，模型只剩摘要里的文字 | 摘要指令要求保留文件名与已得出的结论；`read_file` 的图可按路径重读，用户附件需重新发送；转录照常显示 |
| 计数性能 | .NET BPE 比 Rust tiktoken 慢，长历史首次全量计数可能上百毫秒 | 增量维护 + 按消息缓存 + 后台线程；预热词表；实测后按需再优化 |
| 词表分发 | 数据包把词表嵌进程序集（实测 O200kBase 包 2.42 MB、其中 DLL 1.20 MB；Cl100kBase 包约 1.10 MB），安装包与解压产物都会变大 | 可接受；若需瘦身，改为安装包内独立 `.tiktoken` 文件 + 路径配置（抽象层已预留） |
| 摘要丢信息 | 长任务细节可能被压掉 | 摘要指令强制保留文件路径/命令/未完成项；保留比例 50%；摘要可展开复查 |
| 额外延迟 | 压缩是一次同步模型调用 | 每回合最多一次；仅超阈值时发生；`Started` 事件让界面显示“正在压缩上下文…” |
| schema 版本抬升 | v32、v33 各使旧库只读拒绝一次（既有策略，已确认接受） | 实施前明确告知；拒绝文案引用版本常量；v32 → v33 只加一列可空列，§4.2 给出手工升级语句 |
| continuation 提交路径 | detached 回合需要把压缩项带进原子事务 | 复用 `SqliteConversationTurnWriter`；若 committer 契约膨胀过大，退化为“continuation 不落压缩项，仅本回合生效”。**实际结果（C3）：未退化** —— `SubagentContinuationTurnCommitter` 的原子事务直接携带压缩项，child/continuation 与交互回合共用同一条写入路径，代价只是提交契约多一个可空集合 |
| 回合内无压缩 | 极端长工具循环仍可能超窗——**已解决**：回合内压缩已落地（见 `direct-in-turn-compaction-design.md`），复用同一触发点与保留比例、上限 10 次，并随回合落库 | 回合内压缩在循环顶部、steer 排空之前检查；压缩失败 / 计数不可用 / 上限用尽均回退为“本回合不再压缩” |
| 最大输出占窗口过大 | 最大输出 384K、窗口 1M 的模型可用输入只有约 664K，比例调得再高触发点也停在 598K | 这是模型配置的事实而不是缺陷；用量环的刻度会如实显示封顶后的位置。想用满窗口需在模型管理里调低最大输出 |
| steer 之后的回合内压缩 | 落库边界是“一个 sequence + 其后那条消息里的一个锚点”，跨不过一条已消费的 steer | 那之后的回合内摘要只在本回合请求里生效，落库记录停在 steer 之前；下一回合多重放一段，必要时在回合开始再压一次（分块摘要保证这条路径不会失败） |
| 锚点失效 | 崩溃、可重试失败的 continuation、解析不到 call id | 一律退化为“多重放已摘要的内容”，不丢历史（§4.1） |
| Anthropic 缓存口径未核实 | `InputTokenCount` 在 Anthropic 协议下是否包含缓存命中的 token 没有实测（OpenAI 协议路径在 C18 已实测包含）。若不包含，界面读数与实测下限都会偏低 | 本地计数不受影响，触发仍会发生，只是少了实测下限这层校正；待补一条 probe 测试钉住 |

## 12. 验证入口

```powershell
dotnet restore SelfClaw.slnx --force-evaluate
dotnet build SelfClaw.slnx
dotnet test SelfClaw.Tests/SelfClaw.Tests.csproj

cd SelfClaw.TranscriptVue
npm test
npm run build
```

真实 provider 冒烟需 `SELFCLAW_PROVIDER_SMOKE=1` 且启用具备推理能力的模型；桌面/进程 smoke 需 `SELFCLAW_DESKTOP_SMOKE=1`。

计数相关的定向回归：`dotnet test --filter TokenCounter`、`--filter Prompt`、`--filter Direct`。
