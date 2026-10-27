# Direct 任务计划（todo_write）系统设计

> 状态：**已实施**（P1–P4 全部落地；后端 1796 用例、前端 352 用例通过）。
>
> 基线：schema **v32**、`DirectTurnCapabilityResolver` 汇总 bindings、`tool_runs.arguments_json` 全量落库、`TranscriptProjection` 整包推送渲染状态。
>
> 范围：Direct 运行时的计划工具、计划派生、prompt 注入、转录两者（dock 与内联卡片）。CLI 模式不在范围内。
>
> 前置约束：**不新增表、不改 schema**。非 v32 数据库只读拒绝，任何迁移都会让现有用户库不可用。

---

## 0. 结论

```text
todo_write(整表替换)
    -> tool_runs.arguments_json（已有列，全量保存）
    -> TodoPlanHistory.FindLatest / TodoPlanSection（同一套校验规则）
    -> TranscriptRenderState.Todo（dock） + TranscriptRenderSegment(kind=todo)（内联）
```

| 决策 | 定稿 |
| --- | --- |
| 工具名 | `todo_write`，整表替换 |
| 状态归属 | `tool_runs.arguments_json`，无新表、无新列 |
| 作用域 | 会话级；绑定 Interactive 与 Continuation |
| 主展示 | Composer 上方 dock，实时更新 |
| 次展示 | 调用位置的折叠行，展开复用同一列表组件 |
| 审批 | 不需要 |
| 权限 | 新增 `ToolCallKind.Plan`，`read-only` / `system` 放行，`none` 拒绝 |
| Prompt 注入 | **惰性**：仅当计划已被压缩边界覆盖时注入快照 |
| 回放裁剪 | 只有最新一份合法计划的 args 完整回放，更早的快照替换为占位 |

---

## 1. 目标与非目标

**目标**

1. 模型能维护一份对用户可见、可跨回合、可跨重启的任务计划。
2. 用户可以实时看到「当前在做哪一步、还剩几步」，而不用展开工具调用。
3. 计划的历史可回看，但不会把转录刷成 N 张完整卡片。
4. 不引入任何数据库变更。

**非目标（v1 明确不做）**

- 用户手动勾选/编辑计划（计划由模型维护）。
- 优先级、依赖、子任务、截止时间。
- 跨会话的全局 todo。
- 独立 `conversation_todos` 表。
- CLI 模式（Claude Code 自带的 `TodoWrite` 不映射到本卡片）。
- 历史 `todo_write` 调用的回放裁剪（先测量，再决定）。

---

## 2. 为什么是这三个选择

### 2.1 整表替换而非增量 patch

- 幂等：同一次调用重复执行结果一致，`NoProgressGuard` 的重放语义可预测。
- 可回放：`FunctionCallContent` 天然记录完整状态，不需要重建补丁链。
- 免 id 分配：模型不需要发明稳定 id，也就不会出现「改错条目」这类不可诊断的失败。
- 与主流一致：Claude Code `TodoWrite`、Codex `update_plan`、Cline `todo_write` 都是整表替换。

### 2.2 状态从 `arguments_json` 派生而非独立表

- `tool_runs` 已随回合提交事务写入，且 `arguments_json` **不截断**（`TranscriptToolResultLimiter` 只作用于 `result_content`）。
- 会话删除、回合级联删除天然覆盖计划生命周期，不需要额外清理逻辑。
- schema 仍是 v32，用户现有数据库不需要迁移。

代价：每次读取要扫一遍该会话的工具记录。`FindLatest` 在投影里按引用缓存（见 §6.2），在 `SetupTurnAsync` 里每回合一次，成本可忽略。

### 2.4 历史回放只保留最新一份计划的完整参数

整表替换的副作用是**每次更新都是完整快照**，而 `DirectPromptComposer` 会把每条 `FunctionCallContent` 的参数原样回放。一次 6 步任务更新 5–6 次、每次约 2–3 KB，历史里就会永久躺着 12–15 KB ≈ 3–4k token，每轮重复付费（直到被压缩）。

因此对回放做**裁剪**，而不是对存储做裁剪：

| 对象 | 处理 |
| --- | --- |
| 最新一份合法计划（`FindLatest` 命中的那条） | 完整回放 args 与 result |
| 更早的 `todo_write` | args 换成 `{"elided": true}`，result 换成一句 `Plan superseded by a newer todo_write.`；call / result **配对保留** |
| 失败 / 取消 / 非法的那条 | 同上（它本来也不是当前计划） |

要点：

- 只改**发给 provider 的内容**，`tool_runs` 与 UI 里的历史一字不动（内联卡片仍能看到每次快照）。
- `FunctionCallContent` 与 `FunctionResultContent` 必须成对，所以不能删除调用，只能缩短内容。
- 计费口径自动跟随：`BuildUnits` 同时是计数、压缩规划与请求的唯一来源，裁剪后三者的数字一致。
- 空列表（清空计划）是一次合法的「当前计划=空」，所以它保持完整回放，后面的更早快照被裁剪。
- 不改变 `NoProgressGuard`：它看的是运行期的真实结果，不看 prompt。

### 2.3 惰性注入而非每轮注入

system 是 prompt 前缀，任何字节变化都会让整个前缀缓存失效。每轮注入计划快照会让「计划每变一次 → 下一个请求冷启动」。因此注入条件收紧为「计划已被压缩覆盖」——那一刻 system 本来就因为 summary 而改变，缓存已经失效，注入是**零额外成本**（详见 §7）。

---

## 3. 工具契约

### 3.1 参数

```jsonc
{
  "todos": [
    { "content": "检查 Direct 工具装配", "status": "completed", "activeForm": "检查 Direct 工具装配" },
    { "content": "新增 todo_write 工具", "status": "in_progress", "activeForm": "新增 todo_write 工具" },
    { "content": "投影到 transcript 卡片", "status": "pending" }
  ]
}
```

| 字段 | 必需 | 规则 |
| --- | --- | --- |
| `todos` | 是 | 数组；空数组 = 清空计划；最多 30 项 |
| `todos[].content` | 是 | trim 后非空、≤ 200 字符、trim 后互不重复 |
| `todos[].status` | 是 | `pending` / `in_progress` / `completed`，大小写不敏感 |
| `todos[].activeForm` | 否 | ≤ 100 字符；dock 顶部「正在…」用，缺省回落 `content` |

- 顺序即展示顺序，**不按状态重排**。
- `in_progress` 最多 1 项，允许 0 项（全部完成时）。
- 序列化后总长 ≤ 8 KiB（由 30 项 × 上限推导，留出 JSON 开销）。

参数类型用 `string status` 而不是 C# 枚举：M.E.AI 的 schema 生成对枚举的线上表示依赖 provider，字符串加校验更可控，也能对非法值给出可读错误。

### 3.2 工具描述（模型侧契约）

```
Update this conversation's task plan. Pass the complete list every time; it replaces the previous plan.
Use it for multi-step work (3+ steps): before starting, when the active step changes, and right after a
step is verified complete. Keep exactly one item in_progress while work is underway. Mark a step
completed immediately when it is truly done; never mark work complete that you have not verified.
Skip it for a single trivial action, for answering questions, and for narration. Keep each content
short, specific and action-oriented (max 30 items, 200 characters each). Pass an empty list to clear.
Statuses: pending, in_progress, completed.
```

不加额外的 system 段：契约跟着工具定义走，只有需要它的回合才付 token。

### 3.3 结果

| 情况 | `Status` | `Summary` | `Content` | `Detail`（持久化并回放） |
| --- | --- | --- | --- | --- |
| 更新成功 | Completed | `Updated plan (2/3)` | `{ total, completed, inProgress, pending }` | `Plan updated: 3 tasks (2 completed, 1 in progress). Current: <activeForm>.` |
| 清空成功 | Completed | `Cleared plan.` | `{ total: 0, completed: 0, inProgress: 0, pending: 0 }` | `Plan cleared.` |
| 校验失败 | Failed | `Invalid todo list: <原因>` | `{ error }` | 同 Summary |

- 校验失败**不抛异常**：不消耗 `DirectToolInvoker.MaximumConsecutiveToolFaults`，模型看到原因后重发整表。
- `Detail` 刻意短：完整列表在 args 里，重复一遍只是永久 prompt 噪音。

---

## 4. 权限与回合范围

| 项 | 决策 |
| --- | --- |
| `ToolCallKind` | 新增 `Plan = 6` |
| `DirectCapabilityRules.Allows` | `none` → 否；`read-only` → 是；`system` → 是 |
| 审批 | `requiresApproval: false` |
| 工作区 | 不依赖工作区是否存在 |
| Interactive | 绑定 |
| Continuation | 绑定（补全回合仍应更新计划） |
| Subagent | 不绑定：没有用户可见 surface，白耗 token 与回合 |
| Automation | v1 不绑定，后续放开只需改一个判断 |

`read-only` 放行的理由：该策略限制的是对工作区的写入，计划是会话元数据，不触碰文件。

---

## 5. 代码落点

### 5.1 Core（纯规则，无 IO）

```
SelfClaw.Core/Models/Tooling/TodoItemStatus.cs       enum { Pending, InProgress, Completed }
SelfClaw.Core/Models/Tooling/TodoItem.cs             record TodoItem(string Content, TodoItemStatus Status, string? ActiveForm = null)
SelfClaw.Core/Models/Tooling/TodoPlan.cs             record TodoPlan(IReadOnlyList<TodoItem> Items)
SelfClaw.Core/Runtime/Tooling/TodoPlanRules.cs       ToolName / 上限 / ParseStatus / Validate / TryParse
```

`TodoPlanRules` 是**唯一规则源**：工具用它拒绝非法输入，投影用它决定「这条调用算不算一份计划」。两边共用同一套上限与解析，避免出现「工具拒了但 UI 当计划」的分叉。

```csharp
internal static string? Validate(IReadOnlyList<TodoItem> items)   // 通过返回 null
internal static bool TryParse(string? argumentsJson, out TodoPlan? plan)  // 供投影容错解析
```

`TryParse` 只做结构解析 + `Validate`；失败一律返回 false（投影回落到通用工具卡）。

### 5.2 Infrastructure

```
Agents/Direct/Tools/TodoToolset.cs                          工具 + 描述 + 结果格式化
Agents/Direct/Tools/Models/TodoWriteItemArgument.cs         record TodoWriteItemArgument(string Content, string Status, string? ActiveForm = null)
Agents/Direct/Context/TodoPlanSection.cs                    格式化 system 段 / 从被覆盖的 unit 取计划
Agents/Direct/Capabilities/DirectTurnCapabilityResolver.cs  绑定 todo
Agents/Direct/Capabilities/DirectTurnPolicy.cs              AllowsTodoPlan
Core/Runtime/Agent/ToolCallKind.cs                          Plan = 6
DependencyInjection/ServiceCollectionExtensions.cs          注册 TodoToolset
```

`TodoPlanHistory` 与 `TodoPlanSnapshot` 最终落在 **Core**（`Core/Runtime/Tooling/`、`Core/Models/Tooling/`），不在 Infrastructure：
Prompt 侧（Infrastructure）与转录投影（Desktop）都要「取最新计划」，放在 Infrastructure 会因 `internal` 导致 Desktop 无法复用，
只能各写一份。它们只依赖 Core 类型，上移后单一实现、两边共用（见 §6.1 实现说明）。

`TodoToolset` 是**无状态**的：会话归属来自工具调用的来源记录，不注入任何仓储、不持有可变字段。多会话并发调用不会互相影响。

### 5.3 Desktop

```
Services/Transcript/TranscriptTodoPresenter.cs      ToolExecutionRecord -> kind="todo" 的 segment（失败回落 null）
Services/Transcript/Views/TranscriptTodoItem.cs
Services/Transcript/Views/TranscriptTodoPlan.cs
Services/Transcript/TranscriptMessageProjector.cs   todo_write 走 TodoPresenter，否则原路径
Services/Transcript/TranscriptProjection.cs         计算并缓存 dock 计划
Services/Transcript/Views/TranscriptRenderState.cs  增加 Todo
Services/Transcript/Views/TranscriptRenderSegment.cs 增加 Todo
```

---

## 6. 派生与持久化

### 6.1 当前计划

```
sequenceOf(messageId) = messages[].Sequence
candidates = toolRuns
    .Where(r => r.ToolName == "todo_write" && r.Status == Completed && r.MessageId is not null)
    .OrderBy(sequenceOf(r.MessageId)).ThenBy(ordinal of its ToolCall segment)
latest = 最后一个 TryParse 成功的候选
```

实现：`SelfClaw.Core/Runtime/Tooling/TodoPlanHistory.cs` 的 `FindLatest(messages, toolRuns)`，返回
`TodoPlanSnapshot(Plan, Run, SourceSequence)`。`Run` 是调用方传入的那个实例引用，显示层据此做缓存键，不必二次按 id 查找。
Prompt 侧与投影侧共用它——这也是它必须在 Core 的原因（见 §5.2 说明）。

规则：

1. 只认 `Completed`：`Running` / `AwaitingApproval` / `Failed` / `Cancelled` 都不改变计划。
2. 解析失败或被校验拒绝的 run **跳过并继续向前找**：一次坏调用不能清空用户看到的计划。
3. 空列表是合法计划，语义是「已清空」——dock 隐藏，内联卡片显示「已清空任务计划」。
4. 排序用 `(message.Sequence, segment.Ordinal)`，两点保证同一毫秒内的两条调用也有确定顺序。

### 6.2 投影缓存（实现时最容易踩的坑）

`TranscriptProjection.Build` 每 120 ms 被调用一次，而 `TranscriptRenderState` 是 record：`IReadOnlyList` 字段按**引用**比较。如果每次 `Build` 都新建 `TranscriptTodoPlan`，`state == _lastState` 永远为 false，每个 streaming tick 都会推送一次整包状态。

因此：

- 计划按「来源 `ToolExecutionRecord` 引用」缓存；引用未变时返回**同一个** `TranscriptTodoPlan` 实例；
- 内联 segment 走 `TranscriptMessageProjector` 现有的 `_toolSegmentCache`（同样按引用）；`with { SegmentOrdinal = ... }` 的克隆不改变 `Todo` 引用；
- 已被删除的 run 从两个缓存里一并清掉。

### 6.3 生命周期

| 事件 | 结果 |
| --- | --- |
| 流式更新 | `ConversationTurnRecorder` → `ConversationRuntimeState.ApplyStreamedToolRun` → `RaiseTranscriptChanged(false)` → 下一次合并推送 |
| 回合终态 | 工具已 `Completed`，计划随 tool run 一起提交 |
| 切换会话 | 从该会话的 `ToolRuns` 重新派生 |
| 重启 | 启动恢复把中断的 `Running` 工具改成失败态 → 被规则 1 忽略，回退到上一条合法计划 |
| 删除回合 / 会话 | 级联删除 tool run，计划自然消失 |
| 上下文压缩 | 只影响 prompt，不删消息；dock 与内联卡片不变 |

### 6.4 回放裁剪（实现位置）

`DirectPromptComposer.BuildUnitsCore` 在遍历消息前先算出「保留完整参数」的那条 run：

```
keepFullRunId = 所有可回放消息的 ToolCall 段里，
                最后一个 ToolName == todo_write && Status == Completed && TryParse 成功的 run
```

`BuildAssistantUnit` 对每条 `todo_write`：

```csharp
if (run.Id == keepFullRunId)  { 原样回放 }
else                          { arguments = { elided: true }; resultText = ElidedResultText; }
```

- 只替换 `FunctionCallContent.Arguments` 与配对结果文本，**call/result 结构不变**；失败态的 `Exception` 仍按现有逻辑设置。
- 判定只依赖 `TodoPlanRules`（Core），不需要额外服务，也不改 `DirectPromptComposer` 的公开签名。
- 同一条规则覆盖「模型重试后仍失败」的情形：失败的最新快照不是当前计划，因此它自己也被裁剪，而上一条合法计划保持完整。
- 常量（`ElidedResultText`、占位 args）与裁剪判定放在 `DirectPromptComposer` 内，不新建只为一行服务的类型。

---

## 7. Prompt 注入（P4）

### 7.1 触发条件

**唯一判定：最新一份合法计划位于压缩边界之内（已被 summary 覆盖）。**

| 场景 | 注入 |
| --- | --- |
| 没有压缩 | 否 |
| 计划写在压缩边界之后（仍在 history 里） | 否 |
| 计划被 turn-start 压缩覆盖 | 是 |
| 计划被 in-turn 压缩覆盖 | 是 |
| 计划在压缩边界之前，但之后又有新的 `todo_write` | 否（新计划在 history 里） |

推论：**没有压缩的回合，system 文本与今天逐字节相同**。这是可以直接断言的强性质。

### 7.2 位置与形状

紧跟 summary section 之后、capability sections 之前：

```
[agent instructions]
[compression summary]
[SelfClaw Plan]
The task plan as it stood when the history above was summarized. A newer todo_write in the history supersedes it.
- [x] 检查 Direct 工具装配
- [~] 新增 todo_write 工具
- [ ] 投影到 transcript 卡片
[capability sections]
```

- 快照语义：**不随新的 `todo_write` 更新**。更新意味着又一次改变 system → 又一次前缀失效，而新计划本来就在 history 里。
- 只在存在未完成项时注入；全部完成不注入（没有「继续做」的价值）。
- 约 60–120 token，仅在压缩发生的那个请求上出现。

### 7.3 两个判定点

**turn-start**（`DirectAgentChatRuntime.SetupTurnAsync`）

```csharp
var plan = TodoPlanHistory.FindLatest(request.Messages, request.ToolExecutions ?? []);
// 计数与最终请求必须用同一个 compression 求同一个 section
CountPromptTokens(..., compression: request.Compression, plan);
CountPromptTokens(..., compression: compressionAfter,     plan);   // 压缩后重算
BuildSystemText(...,  compression: compressionAfter, TodoPlanSection.For(compressionAfter, plan));
```

`CountPromptTokens` 在压缩前后各调用一次（分别对应 `request.Compression` 与新产生的 `compression`），两次都必须按各自传入的 boundary 计算 section，否则 `EnsurePromptFitsWindow` 算出的尺寸与真实请求不一致。

**in-turn**（`DirectConversationContext.ApplyInTurnCompaction`）

被移除的 unit 就是要覆盖的范围，直接在其中找 `todo_write`：

```csharp
var removed = 被移除的 entries 的 Messages;          // 现有循环里已经知道移除的是哪些
var section = TodoPlanSection.BuildCovered(removed);  // 未覆盖到计划则 null
```

- 不需要新的 setup 字段，也不需要共享可变状态：上下文自己就知道删掉了什么。
- 这一步必须做：长回合里计划通常写在开头，而 in-turn 压缩覆盖的正是**最老的** unit——计划恰恰最先被吞掉。
- 成本同样为零：那一刻 system 已因追加 summary 而改变，缓存已经失效。

### 7.4 去重

turn-start 与 in-turn 两个 section 可能同时存在（先有压缩，回合内又压缩一次）。若两次快照文本相同，只保留一份：

```csharp
if (!string.IsNullOrEmpty(section) && !string.Equals(section, _planSection, StringComparison.Ordinal))
    _planSection = section;
```

### 7.5 缓存影响验证

| 断言 | 方法 |
| --- | --- |
| 无压缩 + 有计划 → system 文本不变 | 单测比较 `BuildSystemText` 输出字符串 |
| 压缩未覆盖计划 → 不出现 section | 单测 |
| 缓存命中率不受影响 | 实测同一会话连续回合的 `cached_input_tokens`：无压缩回合应与加功能前一致 |

Anthropic 走 SDK，本仓库不设置 `cache_control` 断点；OpenAI-compatible 走自动前缀缓存。两者的命中量都由 `TurnUsageReporter` 透出到用量环，可直接观测。

---

## 8. Wire 契约

```csharp
// Desktop/Services/Transcript/Views/
public sealed record TranscriptTodoItem(string Content, string Status, string? ActiveForm);
public sealed record TranscriptTodoPlan(IReadOnlyList<TranscriptTodoItem> Items);
```

| 位置 | 变化 |
| --- | --- |
| `TranscriptRenderState` | 末尾增加 `TranscriptTodoPlan? Todo = null`（dock） |
| `TranscriptRenderSegment` | 末尾增加 `TranscriptTodoPlan? Todo = null`；`Kind` 允许 `"todo"` |

- `Status` 传 `pending \| in_progress \| completed` 字符串，Vue 不做数字映射。
- 进度、当前项、是否全部完成由 `renderers/todo.js` 纯函数派生，避免前后端两套计数漂移。
- 序列化为 camelCase + `WhenWritingNull`：`todo` 为空时字段整体省略，`transcriptBridge` 必须显式重置（`payload.todo ?? null`），否则切会话会残留上一条计划。

同步三处：`TranscriptProjection`（生成 + 缓存）、`useChatTranscript.replaceState`（复制）、`transcriptBridge`（重置）。

内联 segment 生成：

```csharp
// TranscriptMessageProjector.BuildSegments 的 ToolCall 分支
var segment = toolRun.ToolName == TodoPlanRules.ToolName
    ? TranscriptTodoPresenter.Build(toolRun) ?? BuildToolSegmentCached(toolRun)   // 解析失败回落通用卡
    : BuildToolSegmentCached(toolRun);
```

---

## 9. UI

### 9.1 主 surface：Composer 上方 dock

```
┌──────────────────────────────────────────────────┐
│ ◐ 任务计划                              2/5   ⌃  │
│ ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━  │
│ ✓ 检查 Direct 工具装配                            │
│ ✓ 新增 todo_write 工具                            │
│ ◐ 投影到 transcript 卡片          ← 正在投影      │
│ ○ 前端 dock 与内联卡片                            │
│ ○ 测试与验收                                      │
└──────────────────────────────────────────────────┘
```

- 位置：`ComposerPanel` 新增 `#plan` slot，审批栏之下、队列之上；与 composer 同宽同轴。
- 头部：lucide `ListChecks` + 标题 + `2/5` + 折叠按钮；2px 进度线（accent，宽度过渡 240ms）。
- 进行中：`CircleDot` accent、左侧 2px 高亮条、`pulse` 呼吸；顶部副文案显示 `activeForm`。
- 已完成：`CircleCheck`，`--muted` + 极细删除线（`text-decoration-color: var(--line-1)`）。
- 待处理：`Circle`，`--muted-soft`。
- 列表 `max-height: 168px` 内部滚动（约 6 行），不挤压转录。
- 可见性：无计划 → 不渲染；全部完成 → 过渡后隐藏（历史在内联卡片里）。
- 折叠：默认「忙碌展开 / 空闲折叠为一行摘要」，用户手动切换按会话记住。
- 失败、取消、被 steer 跳过：不打扰 dock，错误只出现在内联行。

### 9.2 次 surface：内联折叠行

```
☑ 更新任务计划   2/5   ›
```

- 位于 `todo_write` 的真实位置，**不并入 tool group**（`buildRenderBlocks` 把 todo 视作不可分组的独立块）。
- 默认折叠；展开复用 `TodoPlanList`，与 dock 同一份数据、同一套状态样式。
- 空列表：一行「已清空任务计划」，不可展开。
- 失败：`ToolStatusIcon(failed)` + 摘要，展开看 `detailText`。

### 9.3 组件与文件

```
components/chat/todo/TodoPlanDock.vue        容器：折叠、可见性、过渡
components/chat/todo/TodoPlanList.vue        纯展示列表（dock 与内联共用）
components/chat/transcript/TodoCard.vue      内联折叠行
composables/useChatTodo.js                   per-conversation 折叠状态、进度派生
renderers/todo.js                            counts / active / labels / 完成判定
styles/todo-plan.css                         共享视觉，scoped 内 @import
```

- 图标全部 `lucide-vue-next`；无 emoji、无手写 SVG。
- `useTranscriptCollapse` 增加 todo 开合集合，`collapseAll` 一并清理。
- 内容按纯文本渲染（`{{ }}`），不解析 markdown。
- 可达性：折叠头 `aria-expanded` + `aria-controls`；进度 `role="status" aria-live="polite"`；列表 `ul/li`。
- 动效：`TransitionGroup` 处理条目状态变化；dock 出没与审批栏同为 160 ms 位移+透明度；`prefers-reduced-motion` 全关。
- 窄窗（< 960px）：隐藏 `activeForm` 副文案；不改变列宽 token。

---

## 10. 边界与失败模式

| 场景 | 行为 |
| --- | --- |
| 模型连续更新计划 | 每次一条内联历史行；dock 只显示最新；旧行默认折叠 |
| 同一列表重复提交 5 次 | `NoProgressGuard` 按现有规则中止回合（预期行为） |
| 非法参数（两个 in_progress / 重复 content / 超长 / 超过 30 项 / 超过 8 KiB） | 返回 Failed + 可读原因；计划不变 |
| `arguments_json` 无法解析 | 投影回落通用工具卡；计划不变 |
| 工具被拒绝 / 取消 / steer 跳过 | 状态 Cancelled，计划不变 |
| 用户停止回合 | 已完成项保留；未完成项保留在 dock |
| 后台 continuation | 计划从 tool run 派生，正常显示 |
| 重启 | `Running` 被启动恢复改成失败态 → 回退到上一条合法计划 |
| 上下文压缩 | 转录与 dock 不变；`§7` 决定是否注入快照 |
| 子代理 / 自动化回合 | 不绑定 `todo_write`，不会出现计划卡 |
| MCP 同名 `todo_write` | 现有 `BindTools` 重名检查直接失败；该 MCP 工具需改名或禁用 |
| 会话切换 | dock 立即切到目标会话的计划；无计划则隐藏 |

---

## 11. 测试

**Core / Infrastructure**

- `TodoPlanRulesTests`：合法解析、大小写、空列表、31 项、重复 content、两个 in_progress、超长 content/activeForm、非法 JSON、序列化上限。
- `TodoToolsetTests`：名称/kind/描述；成功结果三元组；清空计划；非法输入返回 Failed 且不抛。
- `TodoPlanHistoryTests`：只认 Completed；坏记录跳过并回退；`(Sequence, Ordinal)` 排序；会话隔离。
- `TodoPlanReplayElisionTests`：最新合法计划完整回放；更早快照被替换为占位且 call/result 仍配对；失败的最新快照也被裁剪、上一条合法计划保持完整；清空计划（空数组）保持完整；裁剪后 `BuildUnits` 的 token 数小于裁剪前。
- `DirectTurnCapabilityResolverTests`：Interactive/Continuation 绑定；Subagent/Automation 不绑定；`read-only` 放行、`none` 拒绝；无工作区时仍绑定。
- `TodoPlanSectionTests`（P4）：无压缩 → 无 section；压缩未覆盖 → 无；覆盖 → 精确文本；turn-start 与 in-turn 文本相同 → 只一份；不同 → 按序两份。
- `DirectPromptComposerTests`：无压缩时 `BuildSystemText` 与无计划版本**字节相等**。

**Desktop**

- `TranscriptTodoPresenterTests`：`todo_write` → `kind="todo"` + 结构化 plan；非法参数回落 `"tool"`。
- `TranscriptProjectionTests`：dock 计划 = 最后一条 Completed；清空 → null；失败不改变；无变化的 streaming tick 返回同一 `Todo` 实例且 `Build` 返回 null（防止每 tick 推送）。

**Vue**

- `tests/todoPlan.test.js`：dock 折叠/展开、进度文案、全部完成后隐藏、进行中 `activeForm`、空计划、超长截断。
- `transcriptRenderers.test.js`：todo 段不被并入 tool group；内联行默认折叠。
- `hostTranscript.test.js`：`todo` 在切会话与空值时正确重置。
- e2e：fixture 注入一条 `todo_write` 历史，断言 dock 与内联卡片的出现、展开、完成后消失。

---

## 12. 实施阶段

| 阶段 | 内容 | 验收 |
| --- | --- | --- |
| P1 | Core 模型/规则 + `TodoToolset` + `ToolCallKind.Plan` + 策略与绑定 | 真实 Direct 回合能调用；非法列表得到可读错误；单测绿 |
| P2 | `TodoPlanHistory` + 回放裁剪 + 投影（segment kind、state `todo`、引用缓存） | 历史/刷新/切会话正确；无额外推送；回放裁剪生效；Desktop 测试绿 |
| P3 | 前端 dock + 内联卡片 + composable/renderer/样式/动效/可达性 | 视觉验收；Vitest + e2e 绿 |
| P4 | 惰性快照注入（turn-start + in-turn）+ 活动文案 + 文档 | `BuildSystemText` 字节相等断言；压缩覆盖后模型仍能看到计划；`docs/direct-todo-plan-design.md` 状态改为已实施 |

实施结果：

| 阶段 | 状态 |
| --- | --- |
| P1 Core 模型 + `TodoToolset` + `ToolCallKind.Plan` + 策略与绑定 | ✅ |
| P2a `TodoPlanHistory` + 回放裁剪 | ✅ |
| P2b Desktop 投影（segment kind、state `todo`、引用缓存） | ✅ |
| P3 前端 dock + 内联卡片 | ✅ |
| P3b dock 折叠态显示当前步骤 + live region 覆盖当前项 | ✅（审查阶段补入） |
| P4 惰性快照注入（turn-start + in-turn）+ 活动与宠物文案 | ✅ |

**实施中修正的两处设计**：

1. `TodoPlanHistory` / `TodoPlanSnapshot` 上移到 Core（原设计放 Infrastructure）。Desktop 需要同一套「取最新计划」规则，
   Infrastructure 的 `internal` 会迫使 Desktop 复制一份，因此上移；两份实现已在审查中合并为一份。
2. `todo_write` 的参数是**顶层扁平属性** `todos`，不是 wrapper record。单个复杂类型参数会让 M.E.AI 生成
   `{"arguments":{...}}`，把 `arguments_json` 包一层，导致所有按 `{"todos":[...]}` 解析的读取方（工具、派生、回放裁剪）全部失效。
   测试用 schema 断言锁死（`properties.todos.type == "array"`）。

P4 收尾**不新增 `AGENTS.md` 规则**：`AGENTS.md` 已有「数据库 schema v32，非 v32 只读拒绝，绝不迁移」，加表必须先改版本，该约束已经覆盖本设计最危险的误操作；其余内容都能从 `docs/` 与代码推出。

同时把 `AgentActivityCoordinator.MapToolHeadline` 与 `PetActivityPresenter` 的 `ToolCallKind.Plan` 归到「正在更新任务计划」（宠物行为落到默认 `Working`，不变）。

---

## 13. 已决议

| # | 议题 | 结论 |
| --- | --- | --- |
| 1 | 展示形态 | dock + 内联可展开行 |
| 2 | 计划 system 注入 | 做，但改为**惰性**（仅被压缩覆盖时） |
| 3 | 工具绑定范围 | Interactive + Continuation；Subagent / Automation 不绑定 |
| 4 | 全部完成后 | dock 隐藏，历史留在内联卡片 |
| 5 | 字段 | `content` / `status` / `activeForm`，不加 priority |
| 6 | 工具名 | `todo_write` |
| 7 | in-turn 压缩覆盖 | 也注入（成本为零，不补就是明确缺口） |
| 8 | 独立表 | 不做（schema 必须保持 v32） |
| 9 | 历史回放裁剪 | 做（B）：只保留最新一份合法计划的完整参数，更早快照替换为占位 |
