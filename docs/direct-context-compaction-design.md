# Direct 上下文自动压缩设计

状态：设计冻结（v2）· 已完成（C1–C16 落地并审核通过）· 更新 2026-10-17
配套实施文档：《direct-context-compaction-implementation-tasks.md》
替代关系：本设计取代 `DirectPromptComposer` 基于 UTF-8 估算的历史裁剪（`BuildHistoryWithinBudget`），并修订 `runtime-execution-flow.md` §5 的“token 预算仍是启发式估算”一段 —— 字节估算整体退场，计数改为离线 tiktoken 系 tokenizer 加 provider 回报的实测值。

已确认决策（2026-10-17）：

- **DP1 确认**：新增 `conversation_compactions` 表，schema v31 → v32；旧库按仓内既有策略只读拒绝、不迁移不回填。
- **DP2 确认**：不再使用字节启发式估算。计数统一走 tiktoken 系 tokenizer（离线词表），并以 provider 回报的实测值校正与兜底。

## 0. 结论摘要

| 问题 | 现状 | 本设计 |
| --- | --- | --- |
| 上下文超限 | `DirectPromptComposer` 按 UTF-8 字节数估算，从最旧单元开始**静默丢弃**历史 | 改为**自动压缩**：把超限前缀交给模型总结，摘要持久化并替代该前缀 |
| token 计数 | `(UTF-8 字节数 + 2) / 3` 启发式 | 离线 tiktoken 系 tokenizer 计数，配 provider 回报的实测值 |
| 阈值 | 硬编码预算计算，用户不可见 | 系统设置可配：触发比例、无窗口模型的绝对阈值、启用开关 |
| 压缩模型 | 无 | 默认跟随当前会话选中模型；可在设置中指定其他已启用模型 |
| 用户可见性 | 无（历史静默消失） | 转录中插入 `上下文压缩` 分隔项，可展开查看摘要 |
| 用量可见性 | 回合末显示总 token | 输入框区域常驻显示：当前上下文大小 + 会话累计输入/输出 token |

一句话：**把“悄悄丢掉历史”换成“显式压缩历史，并把压缩这件事告诉用户”；把“按字节猜”换成“按 tokenizer 算、按 provider 实测校正”。**

## 1. 现状盘点

| 位置 | 现有行为 |
| --- | --- |
| `SelfClaw.Infrastructure/Agents/Direct/Context/DirectPromptComposer.cs` | `BuildMessages` 组装 system → 历史 → 续写提示 → completion batch；`BuildHistoryWithinBudget` 从最新消息反向累积 `DirectPromptHistoryUnit`，预算耗尽即丢弃更旧单元；`EstimateTokens = (UTF8 字节数 + 2) / 3` |
| 同上 | `BudgetFor` 用 `ContextWindowTokens - (system + tools + 输出预留)` 得到历史预算；`DefaultOutputTokenReserve = 4096` |
| `Agents/Direct/DirectAgentChatRuntime.cs` | `SetupTurnAsync` 调用 composer（每回合一次）；`WriteTerminalOutcome` 映射终态 |
| `Agents/Direct/DirectConversationLoop.cs` | 每回合 ≤128 次 provider 请求；`FitsBudget` 只用于 steer 批次准入；回合内增长已由回合内压缩约束（见 `direct-in-turn-compaction-design.md`），回合开始压缩不变 |
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
- **跨 provider 逐 token 精确**：Anthropic 与 Ollama 未公开 tokenizer，统一用 OpenAI 编码近似（默认 `o200k_base`），不承诺与 provider 自身分词完全一致；实测口径仍以 provider 回报为准。
- **自动探测窗口**：模型窗口仍只来自模型配置（`ContextLength` / `context_window_tokens`）。
- **摘要质量自动评估、压缩成本单独计费展示**。

## 3. 用户可见行为契约

| 项 | 契约 |
| --- | --- |
| 触发 | 回合开始时，计数（或实测值）达到阈值即压缩一次；每回合最多一次 |
| 阈值 | 默认 = 模型窗口 × 75%；模型未声明窗口时用绝对阈值（默认 64 000 tokens） |
| 保留 | 压缩后保留最近历史，目标 ≤ 阈值 × 50%（常量，不开放设置） |
| 压缩模型 | 未指定 → 当前回合模型；已指定 → 该模型（设置时校验为已启用） |
| 摘要复用 | 摘要落库；下一回合只重放边界之后的消息，并把摘要作为运行时上下文注入 |
| 转录提示 | 出现 `上下文压缩` 分隔项，副标题显示被压缩消息数与 token，可展开摘要正文 |
| 关闭压缩 | 不再裁剪历史；若计数超过模型窗口，回合以可读错误失败（提示开启压缩），不静默丢历史 |
| 计数失败 | 本回合关闭压缩并给出诊断；**不回落字节估算**，也不因计数失败使回合失败 |
| 用量显示 | 输入框状态栏显示 `上下文 <当前>/<窗口>` 与 `累计 输入/输出`；数值来自 provider 实测，计数近似值加 `~` 前缀区分 |

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
    DateTimeOffset CreatedAtUtc);
```

`TurnUsage` 增加一个可选字段（与 v32 同批落地）：

```csharp
// SelfClaw.Core/Models/Usage/TurnUsage.cs
public sealed record TurnUsage(
    ..., int? LastRequestInputTokens = null, ...);
```

它是“本回合最后一次 provider 请求的输入 token 数”，即真实上下文规模，用于两处：触发判定下限、界面“当前上下文大小”的首选来源。`ContextTokens`（input+output）保留原语义不动。

`ThroughSequence` 是压缩边界的唯一真值：重放 = 摘要 + `sequence > ThroughSequence` 的消息。不用“最新压缩项之前的一切”这类位置推断，因为压缩发生在当前回合中途，当前回合更早的消息必须保留。

### 4.2 Schema v32

一次版本变更同时落地新表与 `turn_usage` 新列（其余表不变；沿用“拒绝而非迁移”的既有做法，旧库只读拒绝）：

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
    FOREIGN KEY(conversation_id) REFERENCES conversations(id) ON DELETE CASCADE,
    FOREIGN KEY(turn_id) REFERENCES conversation_turns(id) ON DELETE CASCADE
);
CREATE INDEX ix_conversation_compactions_conversation
    ON conversation_compactions(conversation_id, created_at_utc DESC);
```

`turn_usage.last_request_input_tokens` 是**目标建表语句里的一列**（无 `ALTER`、无回填、无缺列回退），因为旧库整体被拒绝。`SqliteSchema.RequiredTables` 增加 `conversation_compactions`，`CurrentSchemaVersion` 31 → 32，`AGENTS.md` 不变量 10 与 12 的版本号同步（首轮实施已完成）。

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

写入与读取的两条细则（首轮实施已按此落地）：

- `last_request_input_tokens` 在 upsert 中用 `COALESCE(excluded.last_request_input_tokens, turn_usage.last_request_input_tokens)`：该列描述“最后一次请求的输入规模”，null 表示未观测，不得抹掉已落库的实测值；其余列仍是快照覆盖。
- 重放取用最新压缩项时按 `through_sequence DESC, created_at_utc DESC, id DESC` 排序：重放语义由边界决定，时间只作次级 tie-break（当前边界随回合单调递增，两种排序等价，但语义以边界为准）。

好处：崩溃丢失压缩项时，最坏结果是下一回合重新压缩一次，不会留下“摘要与历史不一致”的半状态。

## 5. 计数、触发与预算

### 5.1 计数来源

| 来源 | 用途 | 说明 |
| --- | --- | --- |
| tokenizer 计数 | 触发判定、steer 准入、必需内容预留校验、摘要输入/输出规模 | §5.2；离线词表，运行期不联网 |
| provider 回报实测 | 触发下限、界面“当前上下文大小” | `turn_usage.last_request_input_tokens`（v32 新增），缺失时退回 `context_tokens` |

- 字节估算整体退场：不得用 `(UTF-8 字节数 + 2) / 3` 顶替计数；计数不可用时按 §5.5 降级。
- 框架开销用常量补偿（tiktoken 只计正文）：`PerMessageFramingTokens = 4`、`PerToolFramingTokens = 8`。

### 5.2 tokenizer 选择

- **依赖**：`Microsoft.ML.Tokenizers` + `Microsoft.ML.Tokenizers.Data.O200kBase` + `Microsoft.ML.Tokenizers.Data.Cl100kBase`（2026-10-17 在 nuget.org 核实，三者最新稳定版均为 **2.0.1**），版本集中在 `Directory.Packages.props`。数据包把词表以压缩形式嵌进程序集，运行时解压，**不下载、不联网**。落地确认（C4）：实际依赖与版本就是这三个包均 `2.0.1`，无额外 tokenizer 依赖；接口最终为 4 成员（`IsAvailable(model)` + 三个 `Count*(…, model)`），编码按**调用时**的模型解析，计数与指纹缓存按编码分桶。
- **API**（已在 2.0.1 的 XML 文档上核对）：`TiktokenTokenizer.CreateForEncoding("o200k_base")` / `CreateForModel("gpt-4o")` 返回基类 `Tokenizer`；计数用 `Tokenizer.CountTokens(string text)`（另有 `CountTokens(string, ReadOnlySpan<char>, EncodeSettings)` 与 `EncodeToIds` 等重载）。消息与工具定义逐块计数后加框架常量。
- **编码解析顺序**（按**每个回合的模型**解析，不是进程级）：模型 `ModelOptions["tokenizer.encoding"]` 显式指定 → 按模型名映射（`gpt-4o` / `o1` / `o3` 系 → `o200k_base`；`gpt-4` / `gpt-3.5` 系 → `cl100k_base`）→ 默认 `o200k_base`。
- **为什么不用 TiktokenSharp**：TiktokenSharp 在首次使用某个编码时从 `openaipublic.blob.core.windows.net` 下载 `.tiktoken` 词表并缓存到本地，离线场景需要预置文件并设置 `TikToken.PBEFileDirectory`，否则首次运行依赖外网且可能卡住。本产品是离线优先的桌面应用，不应把首次使用压在外部 blob 上。若坚持用 TiktokenSharp，必须随安装包预置三个词表并显式配置路径 —— 抽象层 `IDirectTokenCounter` 允许替换实现而不动其余代码。
- **准确性边界**：`o200k_base` / `cl100k_base` 是 OpenAI 的编码。OpenAI 系模型基本精确（差异来自框架开销）；Claude、Ollama 等只是近似，所以阈值保留 25% 余量，并以 provider 实测值校正下一回合的判断。

### 5.3 计数成本控制

- 上下文 token 数**增量维护**：回合开始时全量计数一次，此后每追加一条消息或一次工具结果只计增量，不重复全量扫描。
- 单条消息计数按 `(MessageId, 内容指纹)` 缓存（上限 4096 条，先进先出淘汰）；工具定义按 schema 文本指纹缓存。缓存只在内存，不落库、不产生 SQL。
- 压缩摘要的 `SummaryTokens` 是对单条摘要文本的一次计数（每回合最多一次，编码按**产出摘要的模型**解析），同样走上面这层缓存（identity 为空、指纹为摘要内容），不额外扫历史。
- 计数不做 per-token SQL，不写库。

### 5.4 触发判定

在 `DirectAgentChatRuntime.SetupTurnAsync`（回合开始、首次 provider 请求之前）判定一次：

```text
window        = AiChatOptions.ResolveContextWindowTokens(profile)          // 可能为 null
outputReserve = options.MaxOutputTokens ?? DefaultOutputTokenReserve(4096)
threshold     = window is int w ? (int)(w * policy.ThresholdRatio) : policy.AbsoluteThresholdTokens
promptTokens  = Count(system + 全部可重放历史 + tools)                     // tokenizer；**不含**输出预留，界面“上下文大小”同口径
observed      = 最新一个回合的 turn_usage.last_request_input_tokens（可能为 null）
triggerTokens = max(promptTokens + outputReserve, observed ?? 0)           // 预留落在触发侧：宁可早压，也不要在小窗口下压完仍超窗
```

- 取两者较大值：tokenizer 受编码匹配度影响，实测值受“上一次请求内容与本次不同”影响；取大更保守，宁可早压不可超窗。
- `policy.Enabled == false` → 不压缩、不裁剪；`promptTokens + outputReserve > window` 时抛可读错误（`InvalidDataException`）：说明历史超出模型窗口、提示开启压缩或换更大窗口的模型。
- 压缩后重算：**必须把新的 system 段（含摘要 section）算进去**，若仍 `promptTokens + outputReserve > window` → 抛同类错误（压缩没能解决，不静默发送超限请求）。

### 5.5 计数不可用时的降级

tokenizer 初始化失败（数据包缺失、程序集不匹配等，**按编码**分别判定）：

1. 该编码记一次 warning（含失败原因，不含正文），并向活动区发一条诊断状态。
2. **本回合关闭压缩**：不压缩、不裁剪、不报错，按完整上下文发送。
3. 触发下限仍可用 provider 实测值；界面“当前上下文大小”在实测缺失时显示为不可用，不显示估算数字。
4. 不引入字节估算兜底路径；不因计数失败使回合失败。

### 5.6 保留策略（常量，不进设置）

```text
keepTarget = threshold * 0.5           // RetainedContextRatio
保留最新单元直到累计计数 ≤ keepTarget；至少保留最新 1 个单元
其余更旧单元组成待压缩前缀 prefix
prefix 为空 → 不压缩（无可压缩内容）
throughSequence = prefix 最后一个单元对应的消息 sequence
```

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

- `SourceTokens` 是**被压缩前缀自身**的 token 数（由 `PlanCompression` 累加前缀单元得到），不是整个 prompt 的计数。`MaxOutputTokens` 的 `/4` 与界面“压缩前 token”都以它为准。
- `SourceMessageCount` 是**会话消息条数**（= `PrefixUnits.Count`）：一个单元对应一条 `MessageRecord`，而单元内部的工具调用与结果会被展开成多条 `ChatMessage`，按 `unit.Messages.Count` 求和会虚高，界面数字也会与边界语义不符。
- `SummaryTokens` 由压缩器用实际产出摘要的那个 lease 的 `Profile`（经 `IDirectTokenCounter`）算出，计数不可用时为 null。界面“压缩前 → 压缩后”的后半靠它，不能恒为 null。

规则：

1. **模型解析**：`ModelProfileId` 非空 → `IAiChatClientFactory.PrepareAsync(id)` + `Create(preparation, new AiChatClientPipelineOptions([]))`，用完释放 lease；为空 → 复用本回合 `AiChatClientLease.Client`（同一 HTTP 客户端，另传一次 `ChatOptions`，不携带工具）。指定模型不可用（禁用/删除）→ 回退到本回合模型、置 `UsedFallbackModel = true`，由 C7 据此发一条诊断 `RunStatusEvent`，不失败回合。
2. **输入构造**：`[system: DirectCompressionInstruction]` +（有旧摘要时）`[user: <selfclaw-context-summary version="1">…</>]` + 前缀单元的原始 `ChatMessage` 序列（角色、工具调用与结果原样保留，单元边界保证调用/结果成对）。
3. **输出约束**：`ChatOptions { MaxOutputTokens = clamp(sourceTokens / 4, 512, 4096) }`，其中 `sourceTokens` 为**待压缩前缀自身**的计数（由 `PlanCompression` 传入，不是整个 prompt 的计数）；其余采样走模型/provider 默认。空或全空白响应视为失败。
4. **失败降级**：单次尝试、不重试（回合内上限 `MaximumCompactionAttemptsPerTurn = 2`，含回退后的重试）；失败只记录日志并发 `ContextCompactionFailedEvent`，回合继续按未压缩上下文发送；若因此超过窗口，由 §5.4 的错误路径给出可读失败，**绝不静默裁剪**。
5. **摘要指令**（固定文案）要求保留：用户目标与约束、已确认的决定、涉及的文件路径与命令、未完成事项与已知错误、关键结论；丢弃寒暄、重复与已被推翻的中间尝试；不得引入新事实；使用简洁书面语，可用列表。

## 7. Prompt 组装

`DirectPromptComposer` 拆成两个纯函数（均无 I/O）：

```csharp
// 规划：是否需要压缩、压缩哪个前缀
internal DirectCompressionPlan PlanCompression(
    IReadOnlyList<DirectPromptHistoryUnit> units,
    ConversationCompactionRecord? existing,
    DirectCompressionPolicy policy,
    DirectPromptBudget budget,
    int? countedTokens);        // §5.4 的 promptTokens，由调用方传入；null = 计数不可用

// 组装：摘要 section + 边界之后的历史 + 必需消息（`tools` / `countedTokens` / `budget` 三个参数都已删除：
// 计数与窗口校验都归调用方，composer 不读窗口）
public IReadOnlyList<ChatMessage> BuildMessages(
    ..., ConversationCompactionRecord? compression = null);

// 供调用方计数用（composer 自己不计数）
internal IReadOnlyList<DirectPromptHistoryUnit> BuildUnits(...);
internal string? BuildSystemText(..., ConversationCompactionRecord? compression);
```

规则：

- **重放边界**：只重放 `Sequence > compression.ThroughSequence` 的可重放消息；更旧的被摘要代表。
- **摘要注入**：作为独立 system section 追加在 agent instructions 之后，包裹在 `<selfclaw-context-summary version="1">…</selfclaw-context-summary>` 中，并附一行说明“这是更早对话的压缩摘要，替代已省略的历史”。放在 system 段而不是 user 消息：它是运行时提供的上下文，不是用户输入（与 `CompletionInstruction` 先例一致）。⚠ 最终顺序（C6 定）：agent instructions → `CompletionInstruction`（有 completion batch 时）→ 压缩摘要 → capability system instructions（Plugin/Skill/Policy/Degradation）；摘要排在 completion 指令之后、能力指令之前，这样无压缩时 prompt 字节布局与旧实现完全一致。
- **裁剪移除**：`BuildHistoryWithinBudget` 与“最新消息装不下就丢弃其余”的路径删除。
- **保留的必要校验**：`counted(system + tools) + outputReserve > window` → 抛既有可读错误（这条与用户历史无关，属于配置错误）；计数不可用时跳过该检查（fail-open），由 provider 报错。⚠ 落地归属（C7 定）：这条检查的唯一归属是 **runtime**（`EnsurePromptFitsWindow`，文案要带“开启压缩 / 降低输出上限 / 换更大窗口”的指引），composer **不再重复同一条校验**——runtime 用同一份计数在 `BuildMessages` 之前先跑，composer 里那份永远不可达，属重复实现（AGENTS 约定 10）。composer 只保留与计数无关的校验（completion batch 的 64 KiB 上限、历史顺序与归属校验）。
- 续写提示、completion batch、steer 批次的语义与顺序不变。
- **计数不再是 composer 的职责**：`EstimateTokens` 字节估算与其派生方法删除；`DirectConversationLoop.FitsBudget` 改用 `IDirectTokenCounter` 与增量维护的上下文计数。
- **单元形状**：`DirectPromptHistoryUnit(Messages, SourceSequence, int? Tokens = null)`。一个单元 = 一条存储消息（工具调用与其结果同单元，边界只能落在单元之间），`Tokens` 由调用方计数后回填，`SourceTokens` = 前缀单元的计数之和。`PlanCompression` 之所以收“已计数的单元”而不是原始入参：保留比例与 `SourceTokens` 都需要每单元的计数，而“composer 不计数”这条约束必须保住。
- **物化范围**：规划要知道前缀，所以 composer 会重建**全部**可重放单元（旧实现那种“只回看 replay 需要的工具记录”的懒索引随裁剪一起删除，这是有意的）。已有压缩项时，边界及更早的消息既不被重放也无需计数，可在建单元与计数时直接从边界之后开始；**首次**压缩仍需全量扫描。

## 8. 事件与前端

### 8.1 新事件（Core/Runtime/Agent）

```csharp
public sealed record ContextCompactionStartedEvent(int EstimatedTokens, int ThresholdTokens) : AgentStreamEvent;
public sealed record ContextCompactionCompletedEvent(ConversationCompactionRecord Compaction) : AgentStreamEvent;
public sealed record ContextCompactionFailedEvent(string Reason) : AgentStreamEvent;
```

`Started` 的两个数字口径：`EstimatedTokens` = `promptTokens`（与界面“上下文大小”同口径，不含输出预留），`ThresholdTokens` = `threshold`（窗口 × 比例，或模型未声明窗口时的绝对阈值）。

`AgentRunStatus` 追加 `Compacting = 4`；`ConversationTurnRecorder.MapRunStatusText` 映射为“正在压缩上下文…”，`AgentActivityCoordinator.MapRunStatus` 映射为 `(Thinking, "正在压缩上下文")`。

### 8.2 录制

`ConversationTurnRecorder.ApplyEventCoreAsync` 增加分支：

- `Started`：`session.ActivityText` 更新 + 立刻发布。
- `Completed`：写入 `AgentTurnState.PendingCompactions` 并 `session.ReplaceCompaction(record)`，`RaiseTranscriptChanged(true)`（分隔项立即可见）。
- `Failed`：只写日志与活动文案，转录不出现误导性提示。

### 8.3 投影

- 新增渲染项：`TranscriptRenderItem(Kind: "compression", Role: "system", Status: "sealed", …)`，载荷 `TranscriptCompactionNotice(CompactionId, MessageCount, SourceTokens, SummaryTokens, Model, Summary)`。
- 排序规则：插入到 `sequence == ThroughSequence` 的消息项之后；多项按 `ThroughSequence` 升序，同值按 `CreatedAtUtc`。
- 用量摘要：`TranscriptProjection` 从 `request.Turns` 计算 `TranscriptUsageSummary(long InputTokens, long OutputTokens, int? ContextTokens, int? ContextWindowTokens, bool ContextIsEstimated)`：
  - 累计输入/输出 = 本会话所有回合 `Usage` 求和（子代理用量不并入）。
  - 当前上下文 = 最新非空 `LastRequestInputTokens`（实测）→ 缺失时用最新非空 `ContextTokens` 并置 `ContextIsEstimated = false`（同为实测口径）→ 都没有时置空，由前端显示不可用。
  - 该字段放入 `TranscriptRenderState`，`replaceState` 与 `patchState` 载荷都要带。
  - ⚠ 界面数字与压缩触发阈值**不是同一口径**：界面“当前上下文大小”是不含输出预留的 prompt 实测值，触发判定用的是 `promptTokens + outputReserve`（§5.4）。`ContextCompactionStartedEvent.EstimatedTokens` 与界面同口径，因此两者可以并列显示，但**不要**做“距离阈值还差多少”的对比展示（会少算一个输出预留）。

### 8.4 Vue

| 文件 | 变化 |
| --- | --- |
| `components/Chat/transcript/CompressionNotice.vue`（新） | `---上下文压缩---` 分隔线 + 副标题（`已压缩 N 条消息 · 48.2k → 1.2k tokens`）+ 摘要折叠展开 |
| `views/ChatView.vue` / `components/Chat/TranscriptPanel.vue` | `item.kind === 'compression'` 分支（与 `turn-outcome` 同级） |
| `renderers/pluginTranscript.js` | 新 kind 的容忍路径（超限时退化为带元数据的精简项，不崩、不误当正文） |
| `composables/useChatTranscript.js` | state 增加 `usage` |
| `components/Chat/ComposerStatusBar.vue` | 右侧渲染用量（`上下文 12.4k / 32k · 累计 输入 1.24M · 输出 342k`）；窄屏只留上下文 |
| `renderers/usageMeter.js`（新） | `formatTokenCount` / `formatContextUsage` / `formatCompressionNotice` 纯格式化；设置页与输入框状态栏共用同一份缩写 |

格式化口径（C13/C14 定，均在这个 renderer 里，不在组件里）：

- `formatTokenCount`：≥ 1M 用 `M`、≥ 1K 用 `K`，最多两位小数且不带千位分隔；已是 `32K` / `1.2M` 形态的字符串原样返回；未知与非正值返回 `—`。量级按**格式化之后**回判（999999 四舍五入成 `1000` 时必须进位为 `1M`，不得输出 `1000K`）。
- 累计输入/输出：未知或为 0（尚无回合）整段省略，不渲染一排破折号。
- 上下文：尺寸或窗口未知时整块显示 `不可用` 而不给数字；`contextIsEstimated` 只为尺寸加 `~` 并附 `由本地计数近似` 提示，窗口是模型声明值，不加 `~`；窄屏只保留上下文尺寸。
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

- `views/SettingsView.vue`：AI 分组新增导航项 `{ id: 'compression', label: '上下文压缩', en: 'COMPRESSION', icon: <lucide 图标> }`，映射到 `components/settings/Compression.vue`（`defineAsyncComponent`）。已核实导航项形状是 `{ id, label, en, icon }`，`icon` 必填（同组其它项都有），组件映射是同文件里的一个 `defineAsyncComponent(() => import(...))` 表。
- `components/settings/Compression.vue` + `composables/useDirectCompression.js`：启用开关、触发比例（10–95%）、无窗口模型绝对阈值、压缩模型下拉（默认「跟随当前会话模型」+ `ai-providers/list-enabled-models`）。
- 样式沿用 `styles/settings-console.css`（`sc-root` / `sc-page` / `sc-page-head` / `sc-page-body`）与 lucide 图标。

## 10. 不变量与删除清单

### 10.1 不变量

| ID | 不变量 |
| --- | --- |
| C01 | 任何路径都不得静默丢弃用户可见历史：要么完整发送，要么压缩并有可见提示 |
| C02 | 压缩边界是已持久化消息的 sequence；被摘要的消息不再单独重放 |
| C03 | 压缩项与回合终态在同一事务提交；重试幂等，不产生重复边界 |
| C04 | 压缩失败不影响回合终态语义；超窗时以可读错误失败 |
| C05 | 子代理 completion batch、续写提示、steer 批次仍为受保护内容，不被压缩或丢弃 |
| C06 | 每回合开始最多一次压缩（不变）；回合内维护性压缩 ≤ `MaximumInTurnCompactionsPerTurn`（3 次），不落库、不裁剪（见 `direct-in-turn-compaction-design.md`） |
| C07 | 压缩使用本回合 lease 或独立 lease，两者都在回合结束前释放 |
| C08 | 设置不可用时回落默认策略；模型不可用时回退本回合模型并留诊断 |
| C09 | 计数只有一个来源：tokenizer 计数 + provider 实测；字节估算不得作为触发依据 |
| C10 | 计数失败只降级为“本回合不压缩”，不得使回合失败，也不得改用估算 |
| C11 | 计数成本有界：增量维护 + 有界缓存，不重复全量扫描、不产生 SQL |
| C12 | 界面区分实测与近似：实测值直接显示，计数近似值加 `~`；不可用时显示不可用而不是数字 |

### 10.2 删除清单（完成后不得残留）

| 内容 | 处置 |
| --- | --- |
| `DirectPromptComposer.BuildHistoryWithinBudget` | 删除，由 `PlanCompression` + 边界重放取代 |
| `EstimateTokens` / `EstimateMessageTokens` / `EstimateToolTokens` / `EstimateUserPrompt` 等字节估算 | 删除，由 `IDirectTokenCounter` 取代 |
| 「最新消息装不下即报错并丢弃其余历史」的分支 | 删除，由压缩 + 窗口校验取代 |
| `DirectPromptBudget` 的“历史裁剪预算”语义 | 保留类型（窗口与输出预留仍需），注释与语义改为“限制与输出预留”，不再代表可裁剪额度 |
| README/AGENTS 中“按 UTF-8 估算裁剪历史”的描述 | 改为“按 tokenizer 计数触发压缩” |

## 11. 风险与未决项

| 风险 | 说明 | 缓解 |
| --- | --- | --- |
| 编码不匹配 | Claude / Ollama 只能近似（OpenAI 编码） | 阈值保留 25% 余量；provider 实测值作为下一回合的下限 |
| 计数性能 | .NET BPE 比 Rust tiktoken 慢，长历史首次全量计数可能上百毫秒 | 增量维护 + 按消息缓存 + 后台线程；预热词表；实测后按需再优化 |
| 词表分发 | 数据包把词表嵌进程序集（实测 O200kBase 包 2.42 MB、其中 DLL 1.20 MB；Cl100kBase 包约 1.10 MB），安装包与解压产物都会变大 | 可接受；若需瘦身，改为安装包内独立 `.tiktoken` 文件 + 路径配置（抽象层已预留） |
| 摘要丢信息 | 长任务细节可能被压掉 | 摘要指令强制保留文件路径/命令/未完成项；保留比例 50%；摘要可展开复查 |
| 额外延迟 | 压缩是一次同步模型调用 | 每回合最多一次；仅超阈值时发生；`Started` 事件让界面显示“正在压缩上下文…” |
| schema 版本抬升 | v32 使旧库只读拒绝（既有策略，已确认接受） | 实施前明确告知；版本断言三处同步 |
| continuation 提交路径 | detached 回合需要把压缩项带进原子事务 | 复用 `SqliteConversationTurnWriter`；若 committer 契约膨胀过大，退化为“continuation 不落压缩项，仅本回合生效”。**实际结果（C3）：未退化** —— `SubagentContinuationTurnCommitter` 的原子事务直接携带压缩项，child/continuation 与交互回合共用同一条写入路径，代价只是提交契约多一个可空集合 |
| 回合内无压缩 | 极端长工具循环仍可能超窗——**已解决**：回合内压缩已落地（见 `direct-in-turn-compaction-design.md`），复用同一阈值与保留比例、上限 3 次、零持久化 | 回合内压缩在循环顶部、steer 排空之前检查；压缩失败 / 计数不可用 / 上限用尽均回退为“本回合不再压缩” |

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
