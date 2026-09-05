# Assistant 消息内容结构化系统设计（content blocks）

> 状态：已实施（2026-09-05）。P1/P2/P4 落地：块模型、schema v25、流式块累积器、终态对齐、投影直读、锚点机制全量删除。P3（模型历史按 content parts 结构化回放）未实施——当前 `DirectPromptComposer` 仍回放派生纯文本 markdown（已不含任何标记，伪造锚点的根因已消除）；结构化 tool-call 回放待后续迭代。
>
> 基线：设计时以 schema v24、`AssistantMessageSegmenter` 锚点机制、`StreamingAssistantMessage` 文本流归约、`DirectPromptComposer` 原样回放为准。
>
> 范围：assistant 消息的内容表示从「markdown + 内嵌标记」改为结构化 content blocks；覆盖数据模型、流式归约、终态落库、渲染投影、模型历史回放与代码清理。CLI 与 Direct 两种运行时共用同一事件流与同一块模型。
>
> 前置决定：**不做旧数据兼容迁移。含标记的历史 `messages` 数据由使用者统一删除，不写 v24→v25 的数据回填。** schema 版本仍递增，但迁移只做 DDL（建表/重建表/删列），不搬数据。

---

## 0. 背景与核心结论

当前实现把「工具调用发生在正文的什么位置」这一传输层协议编码进内容层文本：

- `ConversationRuntimeState.CaptureToolRunAnchor` 在流式 markdown 中追加 `<!--selfclaw:tool:{id}-->`；
- `StreamingAssistantMessage` 用 `<!--selfclaw:think:start/end-->` 包装 thinking；
- 渲染时 `AssistantMessageSegmenter` 靠解析把这些标记还原成段；
- `DirectPromptComposer` 把含标记的 markdown **原样回放给模型**。

已经暴露的失败模式（生产库实证，会话 `80b980f0…` / 消息 `480b7b35…`）：模型在自己历史中看到锚点句法后开始模仿，编造出格式不合法的锚点（`<!--selfcław:tool:…-->`，品牌词含 U+0142；6 段 GUID），渲染层 `TryReadToolAnchor` 解析失败后锚点以明文出现在正文中。临时修复（`RemoveModelFacingMarkers` + 近似锚点清扫）只做了止血。

结构化方案的核心结论：

```text
Agent 事件流（Text/Thinking/ToolCall 增量）
    -> ConversationRuntimeState 块累积器（顺序即位置，无文本标记）
    -> MessageSegmentRecord[]（message_segments 子表）
    -> TranscriptProjection 直读块 -> TranscriptRenderSegment（Vue 合同不变）
    -> DirectPromptComposer 构造 M.E.AI 原生 content parts 回放
```

「结构 → 文本 → 再解析」的往返被删除：位置在事件到达时已知，不再需要从文本中恢复；模型看到的是协议正确的工具调用历史，伪造锚点这一失败类被结构性根除。

## 1. 现状盘点（将被替换的部分）

| 组件 | 现状 | 本设计中的去向 |
| --- | --- | --- |
| `MessageRecord.MarkdownContent` | assistant 消息含锚点/thinking 标记的单字符串 | 保留为**派生列**：Text 块拼接，不含任何标记 |
| `StreamingAssistantMessage` | StringBuilder + `WrapThinking`/`AppendToolAnchor` | 替换为块累积器 `StreamingAssistantContent` |
| `AssistantMessageSegmenter` | 渲染期常驻解析器（~600 行，含交错修复） | 收缩为终态 FinalText 对齐工具（每轮一次） |
| `ToolRunAnchor` 字典 + `tool_runs.after_segment_index` | 与锚点并行的第二套位置坐标 | 整体退役（列随 schema v25 重建删除） |
| `TranscriptToolRunPresenter.BuildToolRunsByMessageId` | 锚点/列/时间三重启发式归位 | 退役，块 ordinal 即位置 |
| `TranscriptToolRunPresenter.InsertToolSegments` | 无锚点消息的兜底插入 | 退役 |
| `DirectPromptComposer` | 含标记 markdown 原样回放 | 按块构造 M.E.AI content parts |
| `ConversationCompletionNotifier.RemoveMetadata` | 通知预览剥锚点 hack | 退役（markdown 已纯净） |
| 前端 `TranscriptRenderSegment` / Vue 组件 | segments 数组渲染 | **零改动** |

## 2. 数据模型

### 2.1 Core DTO（`SelfClaw.Core/Models/Conversations/`）

纯数据载体，遵循项目 DTO 约定（record + 主构造函数，无逻辑）：

```csharp
public enum MessageSegmentKind
{
    Text = 0,
    Thinking = 1,
    ToolCall = 2
}

public sealed record MessageSegmentRecord(
    Guid MessageId,
    int Ordinal,
    MessageSegmentKind Kind,
    string? Text,          // Text / Thinking 块的原文
    Guid? ToolRunId);      // ToolCall 块指向 tool_runs.id
```

`MessageRecord` 增加可选段列表：

```csharp
public sealed record MessageRecord(
    ...,
    IReadOnlyList<MessageSegmentRecord>? Segments = null);
```

约定：

- 用户消息：`Segments == null`，`MarkdownContent` 仍是唯一内容源；
- assistant 消息：`Segments` 为有序块列表；`MarkdownContent` 为派生值（所有 Text 块按序拼接），仅用于会话列表预览、搜索、导出，**渲染与模型回放都不读它**；
- 同一块内 Text/Thinking 交错由运行时保证不发生（见 §3）。

### 2.2 Schema v25（`SqliteDatabase.cs`）

```sql
CREATE TABLE message_segments (
    message_id  TEXT NOT NULL,
    ordinal     INTEGER NOT NULL,
    kind        INTEGER NOT NULL,
    text        TEXT NULL,
    tool_run_id TEXT NULL,
    PRIMARY KEY (message_id, ordinal),
    FOREIGN KEY (message_id) REFERENCES messages(id) ON DELETE CASCADE
);
```

`tool_runs` 原子重建（照 v22→v23 的 rebuild 模式）：删除 `after_segment_index` 列，其余列不变。`tool_runs.message_id` 保留——块的 `tool_run_id` 与 `tool_runs.message_id` 互为冗余校验（同属一条 assistant 消息），写入路径由 `SqliteTurnFinalizationWriter` 在同一事务内保证一致。

**无数据回填**：迁移只执行 DDL。旧 assistant 行删除后 `message_segments` 为空；若仍有残留旧 assistant 行，加载时 `Segments` 为空列表，渲染为空正文（用户已确认接受，不做 markdown 解析 fallback）。

### 2.3 仓储读写（`SqliteConversationRepository`）

- `ListMessagesAsync`：按 conversation 对 `message_segments` 做一次 JOIN 查询，按 `message_id` 分组后挂到 `MessageRecord.Segments`，避免 N+1；
- 写入：assistant 消息落库时先 `DELETE FROM message_segments WHERE message_id = $id` 再批量插入（终态一次写入，见 §4）；
- `SqliteTurnFinalizationWriter.TryWriteAsync` 在现有事务内追加 message_segments 重写与 `tool_runs` upsert（无 `after_segment_index`）。

## 3. 流式运行时（块累积器）

`StreamingAssistantMessage` 替换为 `StreamingAssistantContent`（`SelfClaw.Desktop/Services/Runtime/`）：

| Agent 事件 | 动作 |
| --- | --- |
| `AssistantTextDeltaEvent` | 当前尾块是 Text → append；否则追加新 Text 块 |
| `AssistantThinkingDeltaEvent` | 当前尾块是 Thinking → append；否则追加新 Thinking 块 |
| `ToolCallStartedEvent` | 追加 ToolCall 块（id = tool run id）；工具状态由 `ToolExecutionRecord` 表达，块本身无状态 |
| thinking 结束（`CompleteAssistantStream`） | 关闭当前 Thinking 块；「最后一块是未闭合 Thinking」即 pending 状态 |
| `RunCompletedEvent` | 终态对齐（§4）后落库 |

规则：

- **块类型切换即开新块**——Text/Thinking 交替天然形成块序列，`WrapThinking` / `AppendToolAnchor` / `MergeAdjacentThinkingSegments` 等交错修复逻辑没有存在理由；
- 位置即 ordinal：`ToolCall` 块落位时即知「在第 N 个块之后」，`CaptureToolRunAnchor` 的偏移计算整体删除；
- `Materialize()` 产出（`Segments`, `MarkdownContent` 派生值, `UpdatedAtUtc`），`ConversationRuntimeState.Messages` 读取路径不变；
- `Revision` / 脏检查语义沿用现有 `_messageStreams` 缓存结构。

`ConversationRuntimeState` 变更点：

- 删除 `ToolRunAnchors` 字典与 `CaptureToolRunAnchor`；
- `ApplyAssistantDelta` / `ApplyAssistantThinkingDelta` / `CompleteAssistantStream` 改为操作块累积器；
- 构造函数不再接收 `toolRunAnchors`（`SubagentTaskExecutor.LoadRuntimeAsync` 的 anchor 字典组装随之删除）。

## 4. 终态落库与 FinalText 对齐

`RunCompletedEvent.FinalText`（CLI 最终 message、Direct 最终文本）可能与流式拼接不同。现有 `MergeFinalMarkdown` 用锚点偏移量重映射；块模型下的等价算法：

1. **Fast path**：流式 Text 块拼接 == FinalText（或 FinalText 为 null）→ 直接保留流式块序列（保住 Text/Thinking/ToolCall 的真实交错顺序）；
2. **Slow path**：FinalText 不同 →
   - 流式块给出每个 ToolCall 相对流式文本流的**字符偏移**（各 Text 块长度已知）；
   - 复用现有 `RestoreToolAnchors` 的偏移插入算法（以工具偏移为锚，把工具位置映射进 FinalText）；
   - 对映射结果做一次 Split 得到终态 Text/ToolCall 块；
   - Thinking 块保留流式版本，前置到块序列头部（与现状「thinking 归并到头部」的渲染语义一致）。

`AssistantMessageSegmenter` 的存活面收缩为：

- `Split`（含 think 标签解析）——仅终态对齐 slow path 与对齐结果验证使用；
- `RestoreToolAnchors` 的偏移算法——抽出为纯函数 `AlignToolOffsets`；
- `AppendToolAnchor` / `WrapThinking` / `MergeFinalMarkdown` / `RemoveModelFacingMarkers` / `HasToolAnchors` / `NormalizeAnchoredThinkTokens` / `TryReadTokenWithInterleavedToolAnchors` / 近似锚点清扫正则——**全部删除**（清理规则见 §7）。

终态一次写入：`TurnFinalization` 携带对齐后的 `Segments`，`SqliteTurnFinalizationWriter` 事务内写 `messages`（派生 markdown）、`message_segments`、`tool_runs`。中断路径（`FinalizeInterruptedAsync` / `ApplyUnpersistedTerminalFailure`）同样以流式块快照落库，不丢已完成的工具块。

## 5. 渲染投影

`TranscriptProjection.BuildMessageItem` 对 assistant 消息从「Split(markdown) + 锚点匹配 + 兜底插入」改为对 `Segments` 的直接映射：

| 块 Kind | 输出 |
| --- | --- |
| Text | `TranscriptRenderSegment(Kind: "content", Markdown: text)` |
| Thinking | `TranscriptRenderSegment(Kind: "thinking", Markdown: text, IsPending)` |
| ToolCall | 按 `ToolRunId` 查 tool run → 现有 `BuildToolSegment`（缺失则跳过，防御性丢弃） |

- `TranscriptRenderSegment` wire 合同与 Vue 端（`buildRenderBlocks`、`ToolGroup` 贪心合并、thinking ordinal）**零改动**——连续 ToolCall 块自然成组；
- `TranscriptToolRunPresenter` 仅保留 `BuildToolSegment` 及其摘要/详情格式化；`BuildToolRunsByMessageId`、`InsertToolSegments`、`ResolveInsertionIndex` 删除；
- 消息缓存 fingerprint 纳入 segments（块数量 + 各块文本 hash + tool 状态）。

## 6. 模型历史回放（DirectPromptComposer）

assistant 消息按块构造 M.E.AI 原生 content parts，替代整段 markdown 文本：

- **Text 块** → `TextContent`（`MessageAdjustments` 改为逐 Text 块应用）；
- **Thinking 块** → 不回传（与现状一致；provider 原生 thinking 回传待未来按 model profile 能力启用）；
- **ToolCall 块** → `FunctionCallContent(callId: tool.CorrelationId, name, argumentsJson)`，紧随其后由配对的 `ToolExecutionRecord` 生成 `FunctionResultContent(result)`。

适配器原生序列化（Anthropic → `tool_use`/`tool_result`，OpenAI → `tool_calls`/`tool` 消息）。收益：模型看到的是真正的工具调用历史而非散文叙述，协议正确、token 更省、伪造标记被结构性根除。

约束与检查：

- callId 必须用 `CorrelationId`（运行时 tool call id）而非 `tool_runs.id`，保证与 `ToolCallStartedEvent` 对得上；
- 未完成（Running/AwaitingApproval）的 ToolCall 块不回传 function result 时，需在终态前截断（现状 `BuildMessages` 本就跳过非终态消息，规则不变）；
- `MessageStatus.Truncated` 的续写提示逻辑保持不变。

CLI 路径无变化（只回放最后一条用户消息）。

## 7. 删除清单（完成后不得残留）

按 Cleanup Rule，结构化路径全量替换后删除：

**Core**
- `MessageSegmentRecord` 之外无新增；`ToolRunAnchor` 若在 Core 中定义则删除

**Infrastructure**
- `AssistantMessageSegmenter` 中 §4 列出的全部公开 API（仅保留对齐所需部分）
- `SqliteTurnFinalizationWriter` 的 `after_segment_index` 写入
- `DirectPromptComposer` 的 markdown 整段回放路径

**Desktop**
- `StreamingAssistantMessage`（被 `StreamingAssistantContent` 替换）
- `ConversationRuntimeState.ToolRunAnchors` / `CaptureToolRunAnchor`
- `TranscriptToolRunPresenter.BuildToolRunsByMessageId` / `InsertToolSegments` / `ResolveInsertionIndex`
- `ConversationCompletionNotifier.RemoveMetadata`
- `TranscriptProjectionRequest.ToolRunAnchors` 字段及 fingerprint 中对应段
- 本次止血引入的 `RemoveModelFacingMarkers` 测试与实现

## 8. 实施阶段（每步独立可发布）

| 阶段 | 内容 | 验收 |
| --- | --- | --- |
| P1 | Core DTO + schema v25（DDL）+ 仓储批量读写 + `StreamingAssistantContent` + 投影直读 | 全量测试绿；Direct/CLI 流式回合卡片位置与现状一致；旧 assistant 行（未删场景）渲染为空正文 |
| P2 | 终态块落库（含 FinalText fast/slow path 对齐）+ `TurnRecorder`/两个 Subagent committer 改造 | 历史回放、中断、取消、truncated 全路径落库正确 |
| P3 | `DirectPromptComposer` 结构化回放 | 抓包确认 provider 请求含 `tool_use`/`tool_calls` 原生结构；多轮工具调用会话无伪造锚点 |
| P4 | §7 清单全删 + segmenter 收缩 + `AGENTS.md` schema 段落更新 | `rg "ToolRunAnchor|after_segment_index|AppendToolAnchor|selfclaw:tool"` 运行时代码零命中（测试 fixture 除外） |

P1-P2 完成即达成「DB 正文纯净、模型不再看到标记」；P3/P4 可独立延后。

## 9. 测试与验收

- **仓储**：segments JOIN 读取的块顺序与分组正确；事务写入原子性（messages/message_segments/tool_runs 同事务）；
- **块累积器**：Text↔Thinking 交替、工具夹在文本中间、pending thinking、空回合；
- **终态对齐**：FinalText == 流式（fast path 保留交错）；FinalText 重写且工具数量/顺序保持；FinalText 缺失（cancelled/failed）回退流式块快照；
- **投影**：块的 ordinal 与 Vue 收到的 segments 顺序一致；连续 ToolCall 成组；thinking 折叠状态 id 稳定性（`thinkingBlockId` ordinal 语义不变）；
- **Prompt 回放**：多轮工具会话的请求体包含原生 tool call 结构；thinking 不回传；Truncated 续写提示仍在；
- **手工/E2E**：Direct 与 CLI 各跑一轮含 thinking + 多工具 + 表格/代码块的消息；历史会话重放、取消、失败、空响应正常。

## 10. 风险与取舍

| 风险 | 应对 |
| --- | --- |
| FinalText 对齐语义漂移（最难的一点） | 偏移算法自现有 `RestoreToolAnchors` 原样迁移，仅终态执行一次；slow path 加对齐后校验（工具数量不变），失败则回退流式块 |
| `ListMessagesAsync` N+1 | segments 按 conversation 单查询 + 分组挂载 |
| 块与 tool_runs 不一致（写入半途） | 同一事务写入；读取侧 `ToolCall` 块查不到 tool run 时防御性跳过 |
| 通知/导出等旁路消费者依赖旧 markdown 语义 | 派生 markdown 不含标记，旁路简化（删 `RemoveMetadata`）；实现时 `rg MarkdownContent` 全量过一遍 |
| 旧数据残留 | 已确认不迁移；投影对空 segments 的 assistant 消息渲染空正文，不 crash |
