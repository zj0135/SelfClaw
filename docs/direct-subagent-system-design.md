# Direct Subagent 后端系统设计

> 状态：设计稿 v1.0（2026-08-07）
>
> 基线：以当前仓库代码、`docs/runtime-execution-flow.md` 和 `docs/direct-extensions-system-design.md` 为准。本文描述后续实现目标，本次不修改运行时代码、数据库或前端。
>
> 范围：仅支持 Direct 主 Agent 委派给一层 Direct Subagent。CLI、运行中双向问答、跨进程 worker 和独立 Windows 安全沙箱不在 v1 范围内。

---

## 0. 核心结论

Subagent 不应被实现成 `DirectAgentChatRuntime` 内部的递归调用，也不应把子任务结果直接拼进当前 provider stream。正确的 seam 是一个持久化任务深模块：

```csharp
public interface ISubagentTaskCoordinator
{
    Task<SubagentTaskView> StartAsync(
        SubagentTaskStartRequest request,
        CancellationToken cancellationToken = default);

    Task<SubagentTaskView?> GetAsync(
        SubagentTaskQuery query,
        CancellationToken cancellationToken = default);

    Task<SubagentTaskView> CancelAsync(
        SubagentTaskCommand command,
        CancellationToken cancellationToken = default);

    Task<SubagentTaskView> RetryAsync(
        SubagentTaskRetryRequest request,
        CancellationToken cancellationToken = default);
}
```

调用者只需要知道任务命令、所有权规则和返回状态。以下复杂度全部留在模块实现内：

- Subagent 定义解析、快照与授权校验；
- task/child conversation 的事务创建；
- 全局与父会话并发限制、durable FIFO、超时和取消；
- 重启恢复、Interrupted 收敛和 retry lineage；
- child turn 执行、统一事件归约和 transcript 落库；
- completion envelope、mailbox lease、合并唤醒和 DeadLetter；
- parent continuation 的竞争仲裁和投递幂等。

删除这个模块后，上述逻辑会重新散落到 Direct tools、Desktop turn engine、SQLite repository、启动恢复和通知代码中，因此该 seam 有足够的深度。测试也应主要从这个 interface 观察持久化结果、状态转换和 continuation 请求，而不是穿透到内部 worker。

目标调用链为：

```text
Direct parent turn
  -> DirectTurnCapabilityResolver
       -> eligible parent Agent gets four subagent tools
  -> delegate_to_subagent(subagent_id, task)
       -> ISubagentTaskCoordinator.StartAsync()
       -> durable task + hidden child conversation
       -> return task id immediately

Subagent background host
  -> claim Queued task under concurrency limits
  -> run isolated Direct child turn through IAgentChatRuntime
  -> reduce AgentStreamEvent into child messages/tool_runs
  -> atomically persist task terminal state + Pending delivery

Parent mailbox dispatcher
  -> wait until parent conversation is idle
  -> atomically lease and coalesce Pending deliveries
  -> provider-only synthetic continuation turn
       -> no persisted pseudo user message
       -> normal assistant message/events/tools/finalization
  -> only after successful terminal persistence: Delivered
```

---

## 1. 当前实现基线

### 1.1 现有主链

当前一次桌面回合的真实链路是：

```text
MainWindowViewModel
  -> ConversationTurnEngine.TryAdmitAsync()
  -> ConversationTurnEngine.ExecuteAsync()
       |- persist conversation + user MessageRecord
       |- create AgentTurnState.AssistantMessageId
       |- build DirectChatTurnRequest / CliChatTurnRequest
       `- IAgentChatRuntime.StreamTurnAsync()
            -> DispatchingAgentChatRuntime
                 -> DirectAgentChatRuntime
                      |- IDirectTurnCapabilityResolver.ResolveAsync()
                      |- IAiChatClientFactory
                      `- provider IChatClient
            -> AgentStreamEvent
       -> ConversationTurnEngine.ApplyEventAsync()
       -> DesktopTurnFinalizer
       -> messages / tool_runs
  -> ConversationSessionCoordinator
  -> ITranscriptChangeSink
  -> TranscriptPublisher
  -> TranscriptRenderState
  -> Vue
```

与本设计直接相关的当前事实：

1. `DispatchingAgentChatRuntime` 已保证成功或失败恰好输出一个最终 `RunCompletedEvent`；取消始终抛出 `OperationCanceledException`。
2. `DirectAgentChatRuntime` 已把 provider 的文本、思考、工具、用量和终态翻译成 provider-neutral `AgentStreamEvent`。
3. `ConversationTurnEngine.ApplyEventAsync()` 已把同一事件协议归约为 assistant `MessageRecord`、`ToolExecutionRecord`、token 和最终状态。
4. `DesktopTurnFinalizer` 已原子收敛 assistant 与仍在运行的工具记录，并对重复 finalization 保持幂等。
5. `ConversationSessionCoordinator` 以 conversation id 隔离运行状态；只有选中的 conversation 会发布到当前 transcript，因此未选中 conversation 已能在应用进程内后台运行。
6. `TranscriptProjection` 只依赖 messages、tool runs 和 anchors，不依赖 Direct/CLI 类型。隐藏 child conversation 将来可以直接复用同一投影。
7. `DirectTurnCapabilityResolver` 已将全局启用状态与 `AgentRuntimeDefinition` 的 Plugin/Skill/MCP 绑定求交，并返回不可变 capability lease。
8. `DirectChatTurnRequest.ModelProfileId` 可以为空；为空时 `AiChatClientFactory` 在运行时读取 `desktop-default`。
9. `AgentTurnState.AssistantMessageId` 在 provider 调用前生成，且工具记录也用它作为 `MessageId`。它是当前最合适的稳定 turn id。

### 1.2 可直接复用的能力

| 现有模块 | 可复用内容 | Subagent 用途 |
|---|---|---|
| `IAgentChatRuntime` | Direct/CLI 统一流接口 | child 仍通过它运行，明确构造 Direct request |
| `AgentStreamEvent` | 文本、思考、工具、用量、终态 | child 不新增第二套事件协议 |
| `DirectTurnCapabilityResolver` | workspace、扩展、审批与资源 lease | 在 capability ceiling 下解析 child 能力 |
| `AiChatClientFactory` | 模型/连接/密钥校验和 provider adapter | 父 continuation 与 child 都复用 |
| `DesktopTurnFinalizer` | assistant/tool terminal persistence | child、interactive、continuation 共用终态纪律 |
| `ConversationSessionCoordinator` | conversation 级运行互斥和后台状态 | 防止 parent continuation 与用户回合并发 |
| `TranscriptProjection` | 通用消息与工具投影 | 未来展示 child transcript 时无需新 DTO |
| `DesktopToolApprovalHandler` | WPF、Vue、toast 审批 | 后台 child 写入工具沿用现有审批 |

### 1.3 当前缺口

当前没有以下概念或入口：

- durable background job；
- parent conversation / parent turn / child conversation 关系；
- Subagent 定义目录和主 Agent allowlist；
- 与父历史隔离的 child request；
- child task 并发调度、超时、取消和恢复；
- completion mailbox、lease、批量投递和 DeadLetter；
- 不创建 user message 的内部 continuation 入口；
- 创建任务时冻结的父 Agent、模型和能力上限；
- 禁止 child 再委派的 runtime 级约束；
- child transcript 对统一 reducer 的复用入口。

此外，当前 `ConversationTurnEngine.ApplyEventAsync()` 是私有实现。若直接复制到 Subagent worker，会立即产生两套归约规则。实施时必须先把它抽成共享的 turn recording 模块，再让 interactive、child 和 continuation 三种调用路径复用。

---

## 2. 目标、非目标与不变量

### 2.1 目标

1. Direct 主 Agent 可以异步创建、查询、取消和重试 allowlist 内的 Subagent 任务。
2. Subagent 使用独立 instructions、独立上下文和隐藏 child conversation，不读取父会话历史。
3. child 继续输出现有 `AgentStreamEvent`，完整落入 `messages` 和 `tool_runs`。
4. 任务在应用进程内异步执行，但排队、终态和待投递结果都持久化到 SQLite。
5. child 完成后，父 conversation 空闲时自动触发一次 provider-only continuation。
6. 任务能力不能超过全局当前允许、父 Agent 授权和 Subagent 配置三者交集。
7. 后台写工具沿用父任务工作区和审批模式；默认 Subagent 只读。
8. 重启、取消、超时、失败、Interrupted、retry 和 DeadLetter 都有确定且幂等的状态语义。
9. 普通 Direct/CLI 回合和当前 `AgentStreamEvent` 契约不发生行为回归。

### 2.2 非目标

- 不支持 CLI 主 Agent 委派，也不支持 CLI Subagent。
- 不支持 Subagent 再创建 Subagent；v1 深度固定为一层。
- 不支持 parent 与 child 在运行中双向聊天、追问或中间消息。
- 不把 token delta 当成 parent/child 通信协议。
- 不在应用退出后继续执行 provider stream；v1 不是 Windows service 或分布式 worker。
- 不恢复崩溃前正在流式执行的 provider 请求；原 `Running` 任务恢复为 `Interrupted`。
- 不提供 AppContainer、低权限账户或虚拟化 workspace。
- 本次不提供 child task 列表、child transcript 或 mailbox 的前端页面。

### 2.3 强制不变量

1. `parent_turn_id` 等于发起委派的 parent assistant message id，不额外引入一套临时 turn id。
2. child provider request 的历史只允许包含 child conversation 中显式委派的 `task`；不得读取 parent messages。
3. child request 的 `ExecutionOrigin` 必须为 `Subagent`，capability resolver 在该 origin 下无条件移除四个委派工具。
4. 每个 accepted task 恰好对应一个 hidden child conversation；每个 terminal task 最多对应一条 delivery。
5. child assistant/tool terminal、task 终态与首次创建 delivery 必须在同一个 SQLite 事务中提交。
6. parent continuation 的成功 assistant/tool terminal 与 delivery `Delivered` 必须在同一个 SQLite 事务中提交，不能留下“消息已成功但 mailbox 仍待投递”的崩溃窗口。
7. 设置变化可以缩小 queued task 的能力，但不能扩大创建任务时冻结的 capability ceiling。
8. `OperationCanceledException` 在 library/runtime 内继续作为控制流抛出；只在 background host 的生命周期边缘映射为 task `Cancelled`。
9. 所有 task/delivery 状态转换使用 compare-and-set SQL；重复 worker、重复取消、lease 过期或恢复不得产生第二份结果。
10. interactive user turn 优先于 synthetic continuation；同一 parent conversation 永远最多运行一个 parent turn。

---

## 3. 术语与身份

| 术语 | 定义 |
|---|---|
| parent conversation | 用户可见的原 conversation |
| parent turn | 创建任务的 Direct assistant turn；id 为其 assistant message id |
| child conversation | `kind=Subagent` 的隐藏 conversation，持有 task user message、child assistant message和工具记录 |
| task | 一次持久化 Subagent 执行，拥有独立 id、attempt 和 definition snapshot |
| delivery | 一个 terminal task 写入父 mailbox 的 completion envelope |
| continuation | mailbox 触发的 parent Direct provider turn；不创建持久化 user message |
| capability ceiling | 创建任务时捕获的最大授权集合；后续只能缩小 |
| execution snapshot | continuation/child 所需的父 Agent、具体模型、workspace、审批和 capability ceiling 快照 |

`ConversationMode` 继续表示 Programming/Channel 业务模式。新增的 `ConversationKind` 表示可见性与所有权，不能复用 `ConversationMode.Channel` 隐藏 child，否则会把 UI 过滤规则与 Subagent 生命周期错误耦合。

```csharp
public enum ConversationKind
{
    Interactive = 0,
    Subagent = 1
}
```

---

## 4. 总体架构

### 4.1 模块关系

```text
                         external seam
Direct delegation tools -----------------> ISubagentTaskCoordinator
                                              |
                                              |- validate ownership/definition
                                              |- transactionally create task + child
                                              |- query/cancel/retry commands
                                              `- signal durable queue

SubagentTaskBackgroundHost
  |- recovery pass
  |- queue dispatcher (global 4 / parent 3 / FIFO)
  |- child executor
  |    -> IAgentChatRuntime
  |    -> shared ConversationTurnRecorder
  `- completion transaction
       -> subagent_tasks terminal
       -> subagent_deliveries Pending

SubagentDeliveryDispatcher
  |- lease/coalesce parent mailbox
  |- ConversationTurnEngine.TryAdmitContinuationAsync()
  |- shared ConversationTurnRecorder
  `- Delivered / retry / DeadLetter
```

`SubagentTaskCoordinator` 是概念上的深模块，不要求所有实现塞在一个巨型类中。为了避免构造依赖环，外部 facade 不直接依赖 `IAgentChatRuntime`：

```text
IAgentChatRuntime
  -> DirectAgentChatRuntime
  -> DirectTurnCapabilityResolver
  -> ISubagentTaskCoordinator facade

SubagentTaskBackgroundHost
  -> shared child executor
  -> IAgentChatRuntime
```

facade 只持久化命令并唤醒 queue。background host 在 facade 之外消费 durable queue，因此不会形成：

```text
coordinator -> runtime -> capability resolver -> coordinator
```

内部 worker、repository、clock 和 queue signal 是实现细节或 internal seam，不扩展外部四方法 interface。

### 4.2 共享 turn recording 模块

从 `ConversationTurnEngine` 抽出 `ConversationTurnRecorder`，集中以下现有行为：

- 创建 streaming assistant placeholder；
- 归约 `AssistantTextDeltaEvent` / `AssistantThinkingDeltaEvent`；
- 创建和完成 tool run，捕获 anchor；
- 记录 usage；
- 映射 `RunCompletedEvent`；
- 调用 `DesktopTurnFinalizer` 收敛成功、失败或取消。

三个调用方只保留各自编排差异：

| 调用方 | 负责内容 |
|---|---|
| interactive `ConversationTurnEngine` | 用户 admission、持久化 user message、UI activity、完成通知 |
| child executor | task user message、timeout CTS、task terminal + delivery |
| continuation dispatcher | mailbox lease、transient envelope、无 user message、delivery commit |

`ConversationTurnRecorder` 不需要为单一生产实现增加 public interface；它可以是 Desktop 内的 `internal sealed class`，直接用 in-memory repository/runtime fake 测试。调用者和测试从同一记录入口观察最终 message/tool 状态。

归约与 terminal commit 要分开。归约器产生 `TurnFinalization`，一个 internal `IRecordedTurnCommitter` seam 决定如何提交：

| adapter | 原子提交内容 |
|---|---|
| interactive | 沿用当前 assistant + pending tools finalization |
| child | child finalization + task terminal + 首条 Pending delivery |
| continuation | parent finalization + leased deliveries `Delivered` |

这里有三个真实 adapter，internal seam 有价值。child/continuation adapter 直接使用同一 SQLite connection 和 transaction，不允许先调用现有 finalizer 提交 message，再另开事务更新 task/delivery。

### 4.3 Core request 调整

所有 `ChatTurnRequest` 增加稳定 `TurnId`，值等于本次 assistant message id。CLI 只透传，不改变行为；Direct capability tools 使用它建立 parent task 关系。

Direct 额外携带纯数据执行上下文：

```csharp
public sealed record DirectTurnExecutionContext(
    DirectTurnOrigin Origin,
    DirectCapabilityCeiling? CapabilityCeiling,
    SubagentCompletionBatch? CompletionBatch);

public enum DirectTurnOrigin
{
    Interactive = 0,
    Subagent = 1,
    Continuation = 2
}
```

- `Interactive`：普通 Direct 回合；仅绑定了 Subagent 的 Agent 获得委派工具。
- `Subagent`：永远不暴露委派工具。
- `Continuation`：仍是主 Agent，可使用创建任务时冻结的 `SubagentIds`，但能力受 ceiling 限制。
- `CompletionBatch` 只存在于内存请求中，不写进 `messages`。

`DirectPromptComposer` 在 `CompletionBatch` 存在时，把结构化 envelope 作为最后一条 transient provider user message 追加到正常持久化历史后。它不是 `MessageRecord`，因此 transcript 中不存在伪 user message。

---

## 5. Agent 与 Subagent 定义

### 5.1 主 Agent allowlist

`AgentRuntimeDefinition` 增加纯引用列表：

```csharp
public sealed record AgentRuntimeDefinition(
    string Id,
    string Name,
    string Description,
    AgentExecutionMode Mode,
    string ToolPolicy,
    IReadOnlyList<string> PluginIds,
    IReadOnlyList<string> SkillIds,
    IReadOnlyList<string> McpServerIds,
    IReadOnlyList<string> SubagentIds,
    string Instructions);
```

普通 Agent markdown 增加：

```yaml
---
name: build
description: 通用编程 Agent
mode: direct
tools: system
subagents:
  - reviewer
  - test-runner
---
Main agent instructions...
```

规则：

1. 空 `subagents` 表示没有委派能力，不能解释为允许全部。
2. CLI Agent 即使配置了 `subagents` 也不暴露工具，并产生 definition warning。
3. id 归一化为小写文件名 id，只允许 ASCII 字母、数字、`_`、`-`。
4. allowlist 是授权上限；定义文件存在不代表任意 Agent 都可调用。

### 5.2 Subagent 文件格式

用户专属定义位于：

```text
{AppData}/subagents/<subagent-id>.md
```

v1 只接受以下 front matter 字段：

```yaml
---
name: Code reviewer
description: Reviews the delegated change for correctness and regression risk.
modelProfileId: 0d865b1c-2f70-4ef4-81d6-14c9123c64f7
tools: read-only
plugins:
  - engineering-workflows
skills:
  - code-review
mcpServers:
  - github-readonly
maxRunSeconds: 900
---
Review only the task supplied by the parent Agent.
Return a concise final answer with file and line evidence.
```

字段语义：

| 字段 | 必填 | 语义 |
|---|---|---|
| `name` | 是 | 展示名 |
| `description` | 是 | 主 Agent 选择 Subagent 时看到的用途 |
| `modelProfileId` | 否 | child 具体模型；为空时继承父任务捕获的具体模型 id |
| `tools` | 否 | `none`、`read-only`、`system`；默认 `read-only` |
| `plugins` | 否 | 请求的 Plugin id 集合 |
| `skills` | 否 | 请求的 Skill id 集合 |
| `mcpServers` | 否 | 请求的 MCP server id 集合 |
| `maxRunSeconds` | 否 | child 超时；默认 900，允许 30..3600 |
| 正文 | 是 | child system instructions |

Subagent 没有 `mode` 字段，固定为 Direct。未知字段、非法 GUID、非法工具策略、越界 timeout 或空 instructions 使定义无效，而不是静默回退到更宽权限。

### 5.3 共用 markdown 解析内核

当前 `DesktopAgentDefinitionService.ParseAgentMarkdown()` 同时做 front matter 语法切分、字段解释、归一化和 warning。实施时拆为：

```text
AgentMarkdownDocumentParser
  -> MarkdownDefinitionDocument (scalars, lists, body, syntax diagnostics)

DesktopAgentDefinitionService
  -> maps normal Agent fields

SubagentDefinitionCatalog
  -> maps fixed Subagent fields and validates stricter contracts
```

解析 DTO、normal Agent DTO 和 Subagent DTO 分文件，保持纯数据。具体字段业务校验留在各 catalog/module，不放进 record 方法。保存 normal Agent 时继续使用临时文件 + atomic replace；Subagent v1 只要求加载，未来编辑入口也必须沿用原子写。

### 5.4 定义快照

`StartAsync()` 在接受任务时把归一化后的 Subagent 定义序列化到 `definition_snapshot_json`。queued/running task 不因文件随后被编辑而改变 instructions、model request 或权限请求。

安全状态仍在执行时重新检查：

- 模型/连接被禁用或删除：task `Failed(ModelUnavailable)`，不回退到其他模型；
- 扩展被禁用、删除或损坏：Subagent definition 已列出的必需项使 task 明确失败；parent continuation 的其他已撤权能力从有效集合移除并输出 capability diagnostic；
- workspace 不再存在或不可访问：依赖 workspace 的 task 失败；
- 已接受任务后删除 Subagent markdown 不影响该 task 的定义快照；新的委派会得到 `DefinitionMissing` envelope。

---

## 6. Direct 委派工具

### 6.1 暴露条件

`DirectTurnCapabilityResolver` 增加 `SubagentCapabilitySource`。只有同时满足以下条件才增加工具：

```text
request.Mode == Direct
request.ExecutionContext.Origin != Subagent
request.Agent.SubagentIds.Count > 0
```

工具是否暴露只取决于主 Agent allowlist，不取决于定义文件此刻是否有效。这样 allowlisted 定义被删除或损坏时，调用仍能创建明确的 `DefinitionMissing` / `DefinitionInvalid` failure envelope。即使 child 的定义文件伪造 `subagents` 字段，runtime origin 仍会移除委派工具，禁止嵌套不能被 markdown 绕过。

### 6.2 工具 interface

主 Agent 获得四个 M.E.AI function tools：

```text
delegate_to_subagent(subagent_id, task)
get_subagent_task(task_id)
cancel_subagent_task(task_id)
retry_subagent_task(task_id)
```

语义：

- `delegate_to_subagent` 只负责 durable acceptance，不等待 child 完成。
- `get/cancel/retry` 必须携带 runtime 注入的 parent conversation scope；模型提供的 task id 不能越权访问其他 conversation 的任务。
- `cancel` 幂等。terminal task 返回当前状态，不改写终态。
- `retry` 不复活旧行，而是创建新 task id，设置 `retry_of_task_id` 和递增 `attempt`。
- retry 的 `parent_turn_id` 是调用 retry 工具的当前 turn，便于正确批量唤醒；definition 和 capability ceiling 从原 task 复制，不因设置变化扩大。
- retry 也计入当前 parent turn 最多 8 个创建配额。

典型立即返回：

```json
{
  "taskId": "4f85f9f9-4913-4ba5-b04d-f455cc5e14da",
  "status": "Queued",
  "subagentId": "reviewer",
  "attempt": 1
}
```

### 6.3 错误规则

以下情况不创建 task：

- Subagent id 不在当前 Agent allowlist；
- task 为空或 UTF-8 大小超过 32 KiB；
- 当前 parent turn 已创建 8 个 task；
- caller 不是 Direct 主 Agent。

以下情况创建一个立即 `Failed` 的 task 和 completion envelope，使 parent mailbox 能收到明确结果：

- allowlist 中的定义已删除或无效；
- 指定 model profile 不存在或已禁用；
- 必需扩展不存在、禁用或损坏；
- workspace snapshot 无法建立。

前者是调用或授权错误，后者是已授权委派无法执行。二者不能都压缩成普通 tool exception。

---

## 7. 上下文隔离与 child conversation

### 7.1 child 输入

accepted task 在一个事务中创建：

1. `ConversationKind.Subagent` 的 hidden child conversation；
2. 一条 child user `MessageRecord`，内容严格等于显式 `task`；
3. `subagent_tasks` 行，保存 child conversation id 和预生成的 child assistant turn id。

child request 的 provider messages 只能来自该 child conversation：

```text
system:
  Subagent definition body
  + effective Plugin/Skill instructions

user:
  exact delegated task
```

禁止加入：

- parent conversation 的任何 user/assistant/system message；
- parent 的隐式 summary、最近 N 条消息或 tool result；
- completion mailbox 的其他 task；
- 当前 UI composer 状态。

若 child 需要文件信息，它通过继承的 workspace read tools 自行读取。若 parent 希望传递背景，必须显式写进 `task`，从而使审计和 token 边界可见。

### 7.2 child 运行

child executor 构造 `DirectChatTurnRequest`：

- `ConversationId = child_conversation_id`；
- `TurnId = child_turn_id`；
- `Agent = definition snapshot` 映射出的 Direct runtime definition；
- `Messages = child conversation messages only`；
- `ModelProfileId = resolved child model id`；
- `WorkspaceRoot = parent snapshot`；
- `ToolPermissionMode = parent snapshot`；
- `ToolApprovalHandler = DesktopToolApprovalHandler`；
- `Origin = Subagent`；
- `CapabilityCeiling = captured ceiling`；
- `CompletionBatch = null`。

child 仍调用 `IAgentChatRuntime.StreamTurnAsync()`。事件经共享 `ConversationTurnRecorder` 落入 child `messages` 和 `tool_runs`，因此支持现有全部事件，不只保存 final text。

### 7.3 child 可见性

- `IConversationRepository.GetConversationAsync(childId)` 和 transcript snapshot 查询保留，便于未来详情页和诊断。
- conversation 列表、导航、完成通知和 system tray 必须过滤 `ConversationKind.Subagent`。
- `MainWindowViewModel.MatchesConversationFilter()` 与 navigation filter 都显式要求 `Kind == Interactive`。
- child 运行时即使不被选中，仍会持久化；不向当前 Vue transcript 推送 child delta。

---

## 8. 模型、能力与权限

### 8.1 具体模型快照

任务不能保存“当前默认模型”这个可变指针。构建普通 Direct request 时，`ConversationTurnEngine` 先把：

```text
request.ModelProfileId ?? desktop-default selection
```

解析为具体 `Guid`。父 request、task snapshot 和后续 continuation 都使用该具体 id。

child 模型：

```text
Subagent.modelProfileId 存在 -> 该具体 id
否则                         -> 父任务捕获的具体 id
```

模型在执行前仍由 `AiChatClientFactory` 检查当前 profile、connection 和 credential。被禁用时产生 `ModelUnavailable`，不得回退，避免后台任务静默换模型。

### 8.2 capability ceiling

创建任务时保存父 turn 的授权上限：

```text
ParentExecutionSnapshot
  |- parent AgentRuntimeDefinition
  |- concrete parent model profile id
  |- workspace root identity/path snapshot
  |- ToolPermissionMode
  |- effective Plugin ids + version/content hash
  |- effective Skill ids + version/content hash
  |- effective MCP ids + config revision
  `- allowed Subagent ids
```

snapshot 不保存 DPAPI 解密后的 secret、MCP env/header 明文、provider key 或活跃 client lease。

child 有效能力：

```text
effective child capability
  = currently installed/intact/globally enabled
  ∩ parent capability ceiling
  ∩ Subagent definition request
  ∩ child tool-risk policy
```

Subagent definition 中列出的 Plugin、Skill 和 MCP id 在 v1 都是必需请求，不是 best-effort hint。任一 id 不在 parent ceiling，或执行时已禁用、删除、损坏，task 以 `CapabilityNotAuthorized` / `CapabilityUnavailable` 失败并生成 envelope；不得静默删掉该项后继续运行。只有没有在 Subagent definition 中声明的父能力可以自然不进入 child。

parent continuation 有效能力：

```text
effective continuation capability
  = currently installed/intact/globally enabled
  ∩ captured parent capability ceiling
  ∩ captured parent Agent bindings
```

这保证设置变化可以撤权，但不能让 queued task 或 continuation 获得任务创建后新增的权限。

### 8.3 内建工具策略

Subagent `tools` 语义：

| 值 | 允许的内建工具 |
|---|---|
| `none` | 无 workspace tools |
| `read-only` | `list_files`、`glob_files`、`search_text`、`read_file` |
| `system` | 当前 7 个 workspace tools，包括 write/edit/shell |

默认是 `read-only`。`system` 必须显式配置，且最终集合仍与父 Agent 的工具授权求交。未来若普通 Agent 增加 `read-only`，Subagent 不能通过 `system` 越权获得写工具。

扩展工具也受风险类别限制：

- `List/Search/Read` 可进入 `read-only`；
- `Edit/Run` 只在 `system` 下进入；
- 未知 `Other` 默认视为非只读，只在 `system` 下进入；
- Plugin instructions 本身不是工具，但其贡献的 Skill/MCP 仍逐项检查。

### 8.4 workspace 与审批

- child 继承 parent task 创建时的 workspace，不读取后来切换的 UI workspace。
- child 写工具继承 parent 的 `ToolPermissionMode`；Subagent 不能把 `RequireApproval` 改成 `FullAccess`。
- `RequireApproval` 时复用 `DesktopToolApprovalHandler`。窗口隐藏或最小化时继续走 Windows toast。
- 应用关闭仍调用现有 pending approval rejection；由此产生的取消继续传播到 child executor。
- v1 没有文件锁或 workspace transaction。多个 child 并行写同一文件存在冲突，所以默认只读，并在风险章节明确保留该限制。

---

## 9. completion envelope 与 mailbox

### 9.1 envelope 契约

parent/child 通信只使用 terminal completion envelope，不传输 token delta：

```json
{
  "version": 1,
  "deliveryId": "...",
  "taskId": "...",
  "parentTurnId": "...",
  "childConversationId": "...",
  "subagent": {
    "id": "reviewer",
    "name": "Code reviewer"
  },
  "task": "Review the current implementation...",
  "status": "Succeeded",
  "attempt": 1,
  "result": {
    "finalText": "...",
    "truncated": false,
    "error": null
  },
  "usage": {
    "inputTokens": 1200,
    "outputTokens": 340
  },
  "timing": {
    "queuedAtUtc": "...",
    "startedAtUtc": "...",
    "completedAtUtc": "...",
    "durationMs": 4800
  }
}
```

`status` 可为 `Succeeded`、`Failed`、`Cancelled`、`Interrupted`。error 使用稳定 code 和限长 message，例如 `DefinitionMissing`、`ModelUnavailable`、`CapabilityUnavailable`、`TimedOut`、`CancelledByParent`、`ProcessInterrupted`。

### 9.2 大小限制

- 单个 envelope 的 UTF-8 序列化结果最多 32 KiB；
- 单次 continuation batch 最多 64 KiB；
- metadata、状态、id、usage 和 timing 必须保留；
- 超限时优先截断 `finalText`，再截断 task/error 文本；
- 截断必须在 Unicode scalar/UTF-8 边界完成，并设置 `truncated=true`；
- batch 按 delivery 创建时间 FIFO 取值。加入下一项会超过 64 KiB 时把它留在 Pending，至少取一项。

child 完整 final text 仍保存在 child assistant message；parent 可通过 task/child id 在未来详情入口查看。mailbox 限长不截断持久化 child transcript。

### 9.3 parent transient prompt

`DirectPromptComposer` 把 batch 序列化为机器可识别的 transient user message，例如：

```text
<selfclaw-subagent-results version="1">
{ "deliveries": [ ... ] }
</selfclaw-subagent-results>
```

配套 system instruction 明确：

- 这是 SelfClaw runtime 投递的已完成委派结果，不是新的用户输入；
- 根据状态和 result 继续原任务，必要时向用户总结；
- 不假设 child 结果已自动修改 workspace；以工具记录和当前文件为准；
- 不在回复中泄漏内部 lease、snapshot JSON 或未要求的诊断信息。

该 transient message 不进入 `messages`，但 continuation 产生的 assistant text、thinking、tool runs、usage 和终态按普通 parent assistant turn 持久化。

---

## 10. parent continuation 与投递幂等

### 10.1 唤醒条件

delivery dispatcher 只在以下条件都满足时尝试 continuation：

1. delivery 为 `Pending` 且到达 `next_attempt_at_utc`；
2. parent conversation 仍存在且 `Kind=Interactive`；
3. parent conversation 当前不在运行；
4. 没有已排队的 interactive user admission；
5. 同一 parent conversation 没有其他 `Leased` delivery batch；
6. captured parent Agent 仍能构造 Direct request。

`ConversationTurnEngine.TryAdmitContinuationAsync()` 与用户 `TryAdmitAsync()` 共用 admission gate。interactive path 在等待 gate 前增加 pending-user 计数；continuation 看到该计数即放弃 claim 或释放 lease，从而让用户新回合优先。

### 10.2 合并策略

- dispatcher 对同一 parent conversation、相同 `parent_turn_id` 的 Pending delivery 先做 250 ms coalescing，再原子 claim。
- claim 时尽量合并当前已完成的同 parent turn 结果，受 64 KiB 限制。
- 不等待仍为 Queued/Running 的 sibling task，避免一个慢任务阻塞已完成结果。
- 相隔较久完成的 sibling 可以形成后续 continuation wave。
- 多 worker 通过 compare-and-set `Pending -> Leased` 和相同 `lease_token` 保证同一 delivery 不会同时进入两个 batch。

### 10.3 lease 与成功提交

claim 事务为 batch 中每行写入：

- `status = Leased`；
- 同一个随机 `lease_token`；
- `leased_until_utc`；
- 本次 `continuation_turn_id`；
- `attempt_count + 1`。

continuation 使用创建 task 时的父 execution snapshot，而不是当前 composer 的 Agent/model/workspace 选择。

只有同时满足以下条件，才在一个事务中提交 parent finalization 并把 batch 全部改为 `Delivered`：

1. runtime 输出 `RunCompletedEvent(Succeeded)`；
2. parent assistant message 和未结工具已生成确定的 `TurnFinalization`；
3. row 仍持有同一个 `lease_token`；
4. `continuation_turn_id` 与本次一致。

事务内先以 lease token 做 compare-and-set，再写 parent assistant/tool terminal 和 Delivered 状态；任何一步失败都整体 rollback。重复 completion callback 或过期 worker 的 commit 因 token 不匹配而成为 no-op。

### 10.4 失败、重试与副作用边界

delivery 最多执行三次 continuation attempt，计划时间为：

```text
delivery created + 2 seconds
first failure     + 10 seconds
second failure    + 30 seconds
third failure     -> DeadLetter
```

parent busy 或用户 turn 抢先不计 attempt，只释放/延长 lease 后等待下一次 idle signal。

可自动重试的失败：

- admission 前的 transient persistence/lease failure；
- provider 请求开始前的 transient client creation failure；
- provider failed 且本次 continuation 没有开始任何 tool call。

可重试且尚未产生工具调用的失败不提交 parent `MessageRecord`，只更新 delivery `last_error` 和下一次时间，避免重试在 transcript 中堆积内部失败消息。continuation 使用 detached recording state；成功原子提交后才发布最终 parent assistant。

一旦本次 continuation 已产生工具调用，自动重放可能重复 workspace/MCP 副作用。此时在一个事务中收敛失败 assistant/tools 并把 delivery 改为 `DeadLetter`，随后发送桌面通知。v1 不宣称 exactly-once tool execution；lease 只保证 completion envelope 不被并发投递。

进入 `DeadLetter` 时保留 envelope、attempt count 和 last error，调用 `DesktopNotificationService` 显示父 conversation 与失败摘要。后续人工 retry 应创建新的 delivery attempt，不改写原审计记录；该 UI 不在 v1 范围内。

---

## 11. SQLite schema v23

### 11.1 conversations 扩展

```sql
ALTER TABLE conversations
ADD COLUMN kind INTEGER NOT NULL DEFAULT 0;

ALTER TABLE conversations
ADD COLUMN parent_conversation_id TEXT NULL
    REFERENCES conversations(id) ON DELETE CASCADE;
```

语义：

- 现有行通过默认值迁移为 `Interactive`；
- `Subagent` 必须有 `parent_conversation_id`；
- `Interactive` 必须没有 parent；
- repository mapping 和 `ConversationRecord` 同步增加纯数据字段；
- 现有 `IConversationRepository.ListConversationsAsync()` 明确增加 `WHERE kind = Interactive`，仍同时返回 Programming 与 Channel conversation；child 诊断由 task repository 或 `GetConversationAsync(childId)` 读取，不能依赖每个 UI caller 自行记得过滤。

### 11.2 subagent_tasks

```sql
CREATE TABLE IF NOT EXISTS subagent_tasks (
    id TEXT NOT NULL PRIMARY KEY,
    parent_conversation_id TEXT NOT NULL,
    parent_turn_id TEXT NOT NULL,
    child_conversation_id TEXT NOT NULL UNIQUE,
    child_turn_id TEXT NOT NULL UNIQUE,
    subagent_id TEXT NOT NULL,
    subagent_name TEXT NOT NULL,
    task_text TEXT NOT NULL,
    status INTEGER NOT NULL CHECK(status BETWEEN 0 AND 5),
    attempt INTEGER NOT NULL DEFAULT 1 CHECK(attempt >= 1),
    retry_of_task_id TEXT NULL,
    definition_snapshot_json TEXT NOT NULL,
    parent_execution_snapshot_json TEXT NOT NULL,
    resolved_model_profile_id TEXT NULL,
    max_run_seconds INTEGER NOT NULL CHECK(max_run_seconds BETWEEN 30 AND 3600),
    final_text TEXT NULL,
    input_tokens INTEGER NULL,
    output_tokens INTEGER NULL,
    error_code TEXT NULL,
    error_message TEXT NULL,
    cancel_requested_at_utc TEXT NULL,
    queued_at_utc TEXT NOT NULL,
    started_at_utc TEXT NULL,
    completed_at_utc TEXT NULL,
    created_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL,
    FOREIGN KEY(parent_conversation_id) REFERENCES conversations(id) ON DELETE CASCADE,
    FOREIGN KEY(child_conversation_id) REFERENCES conversations(id) ON DELETE CASCADE,
    FOREIGN KEY(retry_of_task_id) REFERENCES subagent_tasks(id) ON DELETE SET NULL
);
```

`parent_turn_id` 不加 messages foreign key。当前 streaming assistant placeholder 在 finalization 前尚未写入 `messages`，但委派工具可以在流中创建 task；逻辑关系由 id 和测试保证。

### 11.3 subagent_deliveries

```sql
CREATE TABLE IF NOT EXISTS subagent_deliveries (
    id TEXT NOT NULL PRIMARY KEY,
    task_id TEXT NOT NULL UNIQUE,
    parent_conversation_id TEXT NOT NULL,
    parent_turn_id TEXT NOT NULL,
    status INTEGER NOT NULL CHECK(status BETWEEN 0 AND 3),
    envelope_json TEXT NOT NULL,
    envelope_bytes INTEGER NOT NULL CHECK(envelope_bytes BETWEEN 0 AND 32768),
    lease_token TEXT NULL,
    leased_until_utc TEXT NULL,
    attempt_count INTEGER NOT NULL DEFAULT 0 CHECK(attempt_count BETWEEN 0 AND 3),
    next_attempt_at_utc TEXT NOT NULL,
    continuation_turn_id TEXT NULL,
    last_error TEXT NULL,
    created_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL,
    delivered_at_utc TEXT NULL,
    dead_lettered_at_utc TEXT NULL,
    FOREIGN KEY(task_id) REFERENCES subagent_tasks(id) ON DELETE CASCADE,
    FOREIGN KEY(parent_conversation_id) REFERENCES conversations(id) ON DELETE CASCADE
);
```

建议索引：

```sql
CREATE INDEX ix_subagent_tasks_queue
    ON subagent_tasks(status, queued_at_utc, id);

CREATE INDEX ix_subagent_tasks_parent_status
    ON subagent_tasks(parent_conversation_id, status);

CREATE INDEX ix_subagent_tasks_parent_turn
    ON subagent_tasks(parent_turn_id, created_at_utc);

CREATE INDEX ix_subagent_deliveries_ready
    ON subagent_deliveries(status, next_attempt_at_utc, created_at_utc);

CREATE INDEX ix_subagent_deliveries_parent_turn
    ON subagent_deliveries(parent_conversation_id, parent_turn_id, status, created_at_utc);

CREATE INDEX ix_subagent_deliveries_lease
    ON subagent_deliveries(status, leased_until_utc);
```

### 11.4 repository seam 与事务

新增聚焦 repository，而不是继续扩大 `IConversationRepository`：

```text
ISubagentTaskStore
  |- create/transition task
  |- query owner-scoped task
  `- atomically terminalize task + create delivery

ISubagentTaskExecutionStore
  `- FIFO claim, cancellation and startup recovery

ISubagentDeliveryStore
  `- peek/lease/renew/resolve/recover mailbox batch
```

这些是 Core 的聚焦内部 seam，SQLite adapter 放在 `SelfClaw.Infrastructure/Agents/Subagents/Persistence/`。Core 的 `ISubagentTaskCoordinator` 不暴露 SQL transaction、lease token 或 worker claim 方法。

关键事务：

1. Start：检查 parent turn task count < 8，创建 child conversation/user message/task。
2. Claim：`Queued -> Running`，同时验证全局 4、单 parent 3。
3. Complete：child finalization + task terminal + delivery Pending。
4. Delivery claim：一批 `Pending -> Leased`，写同一 token/continuation id，并绑定冻结 parent snapshot。
5. Delivery heartbeat：15 秒续租，整批 CAS 不匹配时 rollback，避免部分 renewal 造成重复执行。
6. Delivery commit：匹配 token 的 parent finalization + `Leased -> Delivered`。
7. Recovery：所有旧 `Running -> Interrupted` 并创建唯一 delivery；过期 lease 无工具时重新排队，有已记录工具或尝试耗尽时 DeadLetter 并收敛 terminal rows。

---

## 12. 状态机

### 12.1 task 状态

```text
Queued -------> Running -------> Succeeded
  |               |  |---------> Failed
  |               |  |---------> Cancelled
  |               `------------> Interrupted (startup recovery only)
  `----------------------------> Cancelled
```

允许状态：

- `Queued = 0`：已持久化，等待 claim；
- `Running = 1`：已由当前进程 claim；
- `Succeeded = 2`：child 成功终态和 transcript 已落库；
- `Failed = 3`：provider、配置、timeout 或记录失败；
- `Cancelled = 4`：用户/parent 显式取消或应用正常关闭取消；
- `Interrupted = 5`：进程非正常结束后恢复旧 Running task。

terminal task 不回到 Queued。`RetryAsync()` 始终创建新行。

### 12.2 delivery 状态

```text
Pending -------> Leased -------> Delivered
   ^                |
   |                | retriable failure / lease expiry
   `----------------'
                    |
                    `----------> DeadLetter
```

- `Pending = 0`：等待 parent idle 和重试时间；
- `Leased = 1`：一个 dispatcher 正在构造/运行 continuation；
- `Delivered = 2`：parent 成功 assistant 终态已持久化；
- `DeadLetter = 3`：尝试耗尽或重放不安全。

每个 task 的 delivery 由 `UNIQUE(task_id)` 保证最多一条。

---

## 13. 调度、取消、超时与恢复

### 13.1 并发限制

固定 v1 限制：

- 全局同时 `Running` child task：4；
- 同一 parent conversation 同时 `Running`：3；
- 同一 parent turn 创建 task：8，包含 retry；
- 其余任务 durable FIFO，排序为 `queued_at_utc, id`。

claim 必须在 SQLite transaction 内基于当前 Running count 完成，内存 `SemaphoreSlim` 只做进程内效率优化，不能成为正确性的唯一来源。

queue signal 使用 bounded/unbounded `Channel` 只负责快速唤醒；background host 还应周期扫描 SQLite，避免进程在持久化成功、signal 前崩溃造成永久沉睡。

### 13.2 timeout

worker 为 child 创建：

```text
linked CTS = host shutdown + explicit task cancellation + maxRunSeconds timeout
```

- explicit cancel -> `Cancelled`；
- host 正常 shutdown cancel -> `Cancelled`，错误码 `ApplicationStopping`；
- timeout -> `Failed`，错误码 `TimedOut`；
- 无法区分的 runtime 自发 `OperationCanceledException` -> `Failed`，保留诊断，不冒充用户取消。

child recorder 将 partial assistant 保留，并按 cancelled/failed 收敛所有 Running/AwaitingApproval 工具。

### 13.3 取消

`CancelAsync()`：

- owner scope 不匹配：返回 not found，避免泄漏其他 conversation 的 task；
- Queued：compare-and-set 为 Cancelled，收敛 child transcript，创建 Cancelled delivery；
- Running：写 `cancel_requested_at_utc` 并取消 task CTS；worker 在边缘完成 terminal transaction；
- terminal：幂等返回当前 view。

删除 parent conversation 前，Desktop 删除流程先请求 coordinator 取消所有 active child task 并有界等待，再删除 conversation；SQLite cascade 是最终清理，不替代 CTS 传播。

### 13.4 应用重启

host 启动顺序：

1. 初始化 SQLite v23；
2. 加载 normal Agent/Subagent catalogs；
3. 在一个 recovery transaction 中把旧 `Running` task 改为 `Interrupted`；
4. 为每个 Interrupted task 创建唯一 failure envelope；
5. 用 `child_turn_id` 创建/收敛 Interrupted child assistant，并把未完成工具标为 Failed；
6. 恢复 `Queued` task；
7. 处理过期 `Leased` delivery，根据 attempt count 回 Pending 或 DeadLetter；
8. 启动 queue 和 delivery dispatcher。

Queued task 自动继续。旧 Running task绝不自动重跑，因为 provider/tool side effect 无法证明幂等；用户可通过 retry 创建新 attempt。

---

## 14. 建议代码落点

### Core

```text
SelfClaw.Core/
  Interfaces/Subagents/
    ISubagentTaskCoordinator.cs
    ISubagentTaskStore.cs
    ISubagentDeliveryStore.cs
  Models/Subagents/
    ConversationKind.cs
    SubagentTaskStatus.cs
    SubagentDeliveryStatus.cs
    SubagentTaskView.cs
    SubagentTaskStartRequest.cs
    SubagentTaskQuery.cs
    SubagentTaskCommand.cs
    SubagentTaskRetryRequest.cs
    SubagentCompletionEnvelope.cs
  Runtime/
    DirectTurnExecutionContext.cs
    DirectTurnOrigin.cs
    DirectCapabilityCeiling.cs
```

每个 DTO 单文件，只携带数据。

### Infrastructure

```text
SelfClaw.Infrastructure/
  Agents/Subagents/
    Persistence/SqliteSubagentDeliveryRepository.cs
    Persistence/SqliteSubagentTaskRepository.cs
    Persistence/SubagentDeliveryMetrics.cs
    Runtime/SubagentCapabilitySource.cs
    Runtime/SubagentCompletionEnvelopeFactory.cs
  Data/Sqlite/
    SqliteDatabase.cs                 // schema v23
    SqliteMappings.cs                 // conversation kind/parent mapping
  Extensions/Runtime/
    DirectTurnCapabilityResolver.cs   // capability ceiling + delegation source
```

SQLite、envelope 序列化、capability 交集和 Direct tool adapter 属于 Infrastructure。

### Desktop

```text
SelfClaw.Desktop/Services/
  Agents/Definitions/
    AgentMarkdownDocumentParser.cs
    MarkdownDefinitionDocument.cs
    SubagentDefinition.cs
    SubagentDefinitionCatalog.cs
  Runtime/
    ConversationTurnRecorder.cs
    IRecordedTurnCommitter.cs
    ConversationTurnEngine.cs
  Subagents/
    SubagentTaskCoordinator.cs
    SubagentTaskBackgroundHost.cs
    SubagentTaskExecutor.cs
    SubagentContinuationExecutor.cs
    SubagentContinuationTurnCommitter.cs
    SubagentDeliveryDispatcher.cs
    ISubagentConversationLifecycle.cs
```

Desktop 拥有 turn admission、WPF approval、notification 和当前进程 background lifecycle，因此 coordinator facade 与 host 编排放在 Desktop。Infrastructure 的 delegation tools 只依赖 Core interface。

### DI 与启动

1. Infrastructure 注册 task stores、`ISubagentDeliveryStore` 和 `SubagentCapabilitySource`。
2. Desktop 注册 `SubagentDefinitionCatalog`、`ConversationTurnRecorder`、`SubagentTaskCoordinator`，并把它映射为 `ISubagentTaskCoordinator`。
3. `SubagentTaskBackgroundHost` 和 `SubagentDeliveryDispatcher` 作为 hosted services 注册。
4. hosted service 必须在 repository initialize/recovery 后执行工作。若继续由 `App.OnStartup()` 手动初始化，应增加显式 `InitializeAsync()` gate，不能依赖 DI 构造顺序。
5. `OnExit()` 先停止 hosted services、拒绝 pending approvals，再由现有 host disposal 释放 runtime/capability leases。

---

## 15. 测试策略

测试以深模块 interface 和可观察持久化结果为主。内部 queue/channel 不作为断言对象。

### 15.1 定义与授权

- normal Agent `subagents` 正确解析、序列化、归一化和 warning；
- Subagent 只接受固定字段、Direct、合法 model id 和 timeout；
- 缺省 `tools` 为 `read-only`；
- allowlist 外 id 无法创建 task；
- child origin 即使定义伪造 `subagents` 也看不到四个工具；
- 定义 accepted 后编辑/删除不改变已保存 snapshot；新的 missing definition 产生明确 failure envelope。

### 15.2 上下文隔离

- child `DirectChatTurnRequest.Messages` 只含显式 task，不含任何 parent history；
- parent attachments、tool results、failed messages 和 continuation batch 都不泄漏到 child；
- child instructions 只来自自身 snapshot 与有效扩展；
- child model 缺省继承父具体 model id，不重新读取 `desktop-default`。

### 15.3 能力与审批

- 有效能力严格等于 current global enablement、parent ceiling、Subagent request 和 tool policy 的交集；
- task 创建后新启用的能力不会扩大 queued/continuation 权限；
- 后续禁用能力会撤权或产生明确 failure，不静默 fallback；
- read-only 排除 write/edit/shell 和未知风险 MCP；
- 显式 `system` 仍不能超过 parent；
- workspace 与 `ToolPermissionMode` 继承父 snapshot；
- 后台审批复用 WPF/toast，拒绝、timeout 和 shutdown 均能收敛。

### 15.4 child event recording

对每种现有事件验证 child 落库：

- text/thinking delta 合并；
- tool start/completion、source、anchor、result content；
- usage；
- succeeded/failed terminal；
- cancellation partial text 与 pending tool 收敛；
- 现有 `TranscriptProjection` 可从 child messages/tool_runs 构建 state。

同一组 contract tests 运行 interactive 与 child recorder，防止两条路径漂移。

### 15.5 调度与生命周期

- 全局 4、单 parent 3、单 parent turn 8 的限制；
- durable FIFO 顺序和空 slot 补位；
- Queued cancel、Running cancel、timeout 和 retry lineage；
- restart 保留 Queued，Running 原子变 Interrupted 且不重跑；
- child finalization、task terminal 与 delivery creation 的事务原子性；
- 重复 completion/cancel/recovery 不产生第二条 delivery。

测试使用可控 `TimeProvider`、阻塞 fake runtime 和真实临时 SQLite，不依赖 `Task.Delay` 竞速。

### 15.6 mailbox 与 continuation

- 单 envelope 32 KiB、batch 64 KiB，UTF-8 截断合法；
- 同 parent turn 同时完成只 claim 一次并合并；
- 慢 sibling 不阻塞已完成 sibling；
- parent 仍运行时不唤醒；
- 用户新回合抢先时 continuation 不并发且不消耗 attempt；
- lease 过期、重复 dispatcher、过期 commit 不重复投递；
- continuation request 有 transient completion batch，但 repository 没有伪 user message；
- 仅在成功 assistant 终态持久化后 Delivered；
- 成功 parent finalization 与 Delivered 同事务，模拟每个写入点崩溃都不会重复 continuation；
- 可重试的无工具失败不创建 parent failed message；
- pre-side-effect failure 按 2/10/30 秒重试；
- 已开始工具的失败直接 DeadLetter；
- attempt 耗尽通知桌面；
- app restart 恢复 Pending/expired Leased delivery 保持幂等；过期 lease 若已有 tool row 必须 DeadLetter 并收敛 parent/tool terminal；
- lease heartbeat 的整批 CAS renewal、同 parent 并发 lease 只有一个 winner；
- detached continuation 不改变 selected transcript，删除 tombstone 和 `CancelAndWaitAsync` 超时阻止 cascade；
- v22 库在缺少 ownership 列时升级到 v23 且保留 message/tool/session 数据。

### 15.7 回归

- 普通 Direct 请求保持完整历史和现有 capability 行为；
- CLI request 只新增透传 turn id，不获得 Subagent 工具；
- 当前 `AgentStreamEvent` 类型和 dispatcher terminal discipline 不改；
- normal conversation list、通知、删除和 transcript 行为不包含 hidden child；
- 无 `SubagentIds` 的 Agent 不增加 token/tool surface。

---

## 16. 分阶段实施

### P0：共享 turn id 与 recorder（已完成）

- [x] 给 request 增加稳定 `TurnId`；
- [x] 抽取 `ConversationTurnRecorder`；
- [x] 让现有 interactive Direct/CLI tests 通过共享 recorder；
- [x] 不引入 Subagent 行为。

完成记录：

- `ChatTurnRequest`、`DirectChatTurnRequest` 和 `CliChatTurnRequest` 已统一携带稳定 `TurnId`；interactive turn 使用预生成的 assistant message id。
- `ConversationTurnRecorder` 已集中处理 assistant text/thinking、tool event、usage 和 terminal reduction；`IRecordedTurnCommitter` 将归约与最终提交分离。
- interactive Direct/CLI 已切换到共享 recorder，原 `DesktopTurnFinalizationRequest` 被统一的 `RecordedTurnFinalizationRequest` 替代。
- 本阶段没有增加 Subagent tool、后台任务、child execution 或 continuation 行为。

### P1：定义、Core 契约与 schema v23（已完成）

- [x] 共用 markdown parser；
- [x] normal Agent `SubagentIds` 和 Subagent catalog；
- [x] conversation kind/parent；
- [x] task/delivery tables、models、repository 与 schema tests。

完成记录：

- `AgentMarkdownDocumentParser` 已成为 normal Agent 与 Subagent definition 的共享语法解析内核；normal Agent 保持宽松 warning，`SubagentDefinitionCatalog` 对固定字段、GUID、tool policy、timeout 和 instructions 执行严格校验。
- normal Agent 已支持 `subagents` 的解析、归一化、序列化和 CLI warning；`AgentRuntimeDefinition.SubagentIds` 已贯穿 Desktop 到 runtime request。
- Core 已加入 `ISubagentTaskCoordinator`、task/delivery 状态与 view/request DTO、completion envelope、`DirectTurnExecutionContext` 和 capability ceiling 数据契约，但尚未注册 coordinator 实现或暴露委派工具。
- `ConversationRecord` 已加入 `ConversationKind` 与 `ParentConversationId`；普通 conversation list 在 repository 内过滤 hidden child，按 id 仍可读取 child conversation。
- SQLite schema 已更新到 v23，新增 ownership CHECK、自引用 cascade、`subagent_tasks`、`subagent_deliveries` 及调度索引。
- `SqliteSubagentTaskRepository` 当前提供 P1 所需的原子 task acceptance 与 owner-scoped read：同一事务创建 child conversation、精确 task user message 和 Queued task，并执行单 parent turn 最多 8 个 task 的限制。
- 已提供 v22 到 v23 的真实兼容迁移：当 `profile_id`、`kind` 或 `parent_conversation_id` 任一旧列形状存在时，SQLite 在事务内重建 conversations，保留已有 ownership 字段，旧行默认 Interactive，并保留 message/tool/session 数据。
- 全量测试通过：463/463；覆盖共享 parser、严格 catalog、隐藏 child、ownership、事务回滚、task 上限、cascade、schema CHECK/UNIQUE 和 DI 注册。

### P2：durable task 与 child execution（已完成）

- [x] coordinator 四方法；
- [x] 四个 Direct tools 和 runtime-level no-nesting；
- [x] child context isolation；
- [x] scheduler、limits、timeout、cancel、recovery；
- [x] child transcript 和 completion envelope。

完成记录：

- `ISubagentTaskCoordinator` 保持为 Core 对外深接口；持久化能力拆分为 Core 的 `ISubagentTaskStore` 与 worker 专用的 `ISubagentTaskExecutionStore`，Desktop 不接触 SQLite transaction、claim 或 delivery 细节。
- `SqliteSubagentTaskRepository` 已实现 `BEGIN IMMEDIATE` acceptance/claim、全局 4/单 parent 3/单 turn 8 限制、FIFO、Queued/Running cancel、retry lineage 校验，以及 child terminal + task terminal + 唯一 Pending delivery 的原子提交。
- completion envelope 按 UTF-8 精确执行 32 KiB 上限，并按 Unicode Rune 安全截断；所有 terminal task 都生成稳定 error code 与至多一条 delivery。
- interactive Direct turn 在构建能力工具前把默认模型解析为具体 profile id；resolver 捕获实际成功解析的 capability ceiling，并对 child 严格校验 tool policy、Plugin、Skill、MCP 与 Subagent allowlist。
- 已接入 `delegate_to_subagent`、`get_subagent_task`、`cancel_subagent_task`、`retry_subagent_task`；Subagent origin 不暴露委派工具，child request 只包含精确 task user message 与自身 instructions，不复制 parent history。
- child executor 复用 `ConversationTurnRecorder` 的全部 `AgentStreamEvent` 归约与终态纪律，同时与交互会话、Vue transcript publication、桌面完成通知完全隔离；recorder committer 现在保留 provider 原始 `FinalText`，CAS 失败时重载已持久化终态。
- background host 在数据库和 catalogs 初始化完成、桌面审批订阅就绪后启动；它周期扫描 durable queue，启动时把旧 Running 收敛为 Interrupted 且不重放 provider 请求，并继续执行 Queued task。
- timeout、父级取消、应用关闭、runtime 自发取消、preflight/snapshot/provider failure 均按稳定状态和 error code 收敛；即使 child runtime 装载失败，也会尝试写入最小失败 transcript、task 终态与 delivery。
- P2 只把完成结果写为 `Pending` delivery；mailbox lease、coalescing 与 parent continuation 未启用，仍由 P3 实施。
- 全量测试通过：483/483；覆盖 capability/no-nesting、原子 claim/terminal/delivery、UTF-8 envelope、隔离 child stream、Running cancel、Interrupted recovery、Queued restart 和 terminal CAS reload。

### P3：mailbox continuation（已完成）

- [x] lease/coalescing；
- [x] transient completion batch；
- [x] user-priority admission；
- [x] success commit、retry、DeadLetter 和通知。

完成记录：

- `ISubagentDeliveryStore` 是 Core 的聚焦持久化 seam；SQLite adapter 在 `BEGIN IMMEDIATE` 内按 parent conversation、parent turn 和冻结的 `ParentExecutionSnapshotJson` 分组，按 UTF-8 wrapper/comma 精确执行 64 KiB batch 上限和 FIFO。
- `SubagentDeliveryDispatcher` 以 250 ms coalescing/scan、最多 4 个 continuation 和 45 秒 lease 运行；`SubagentContinuationExecutor` 每 15 秒整批 heartbeat 续租，续租不完整时整批回滚并放弃本次状态。
- continuation 通过 detached `ConversationRuntimeState` 和 transient `<selfclaw-subagent-results>` user message 执行，不写入伪造的 parent user message，也不在运行中发布 parent transcript 或增量 tool row。
- 成功时 parent assistant/tool terminal 与全部 delivery `Delivered` 在同一事务提交；无工具失败按 10 秒、30 秒退避后第三次 DeadLetter 且不写 parent failed message；产生工具副作用的失败原子写入失败 parent/tool terminal、DeadLetter 并通知桌面。
- admission gate 维护 pending interactive 计数，用户新回合优先于 continuation；parent busy、删除 tombstone、lease 竞争和 stale commit 不消耗 attempt，也不会重复投递。

### P4：收尾（已完成）

- [x] conversation delete integration；
- [x] runtime execution flow 文档更新；
- [x] load/soak tests；
- [x] 日志字段、指标和故障诊断；
- [x] 确认无 child conversation 泄漏到现有 Vue 列表。

完成记录：

- 删除 parent 时先设置 admission tombstone、停止 parent turn，再由 `ISubagentConversationLifecycle.CancelAndWaitAsync` 取消并有界等待所有 queued/running child；超时会阻止 SQLite cascade，避免仍在执行的 child 写入已删除 parent。
- `MainWindowViewModel` 和 repository 双重过滤 `ConversationKind.Interactive`，显式选择 child 也会被拒绝；cascade 只在 child 已达终态后执行。
- v22 到 v23 迁移、结构化 lease/retry/delivered/deadletter/recovery 日志和 `SelfClaw.Subagents` Meter 已落地；并发竞争测试验证同一 parent mailbox 只有一个 lease winner。
- 全量测试通过：496/496；其中 Subagent 定向测试 50/50，覆盖 batch/lease/recovery、transient prompt、detached transcript、用户优先、删除等待和 v22→v23 数据保留。

每阶段都应可独立合并。P0/P1 不改变用户行为；P2 可以先只提供 task API 测试；P3 才开启自动 continuation。

---

## 17. 验收标准

1. 绑定 Subagent 的 Direct Agent 能看到四个工具；其他 Direct Agent、所有 CLI Agent 和所有 child run 看不到。
2. `delegate_to_subagent` 在 durable acceptance 后立即返回，不阻塞 parent provider stream。
3. child request 测试证明没有任何 parent message，只含 task 与自身 instructions。
4. child 的现有全部 `AgentStreamEvent` 都进入 messages/tool_runs，现有 projection 能渲染。
5. 默认 child 只读；显式写权限仍不超过 parent，并沿用父审批模式。
6. 全局 4、单 parent 3、单 turn 8、FIFO 均由持久化 claim 保证。
7. queued task 重启继续，running task 变 Interrupted 且不自动重跑。
8. task 每种 terminal 状态恰好生成一条 envelope；单项和批次大小上限生效。
9. parent busy、用户 turn 抢先和多个 task 同时完成都不会产生并发 continuation 或重复投递。
10. parent transcript 不出现伪 user message；成功 continuation 产生普通 assistant message和工具记录。
11. 只有 continuation 成功终态持久化后 delivery 才是 Delivered；retry/lease/restart 保持幂等。
12. 无法安全重放或尝试耗尽的 delivery 进入 DeadLetter 并通知用户。
13. 非法/删除定义、禁用模型/扩展、timeout、cancel 和 Interrupted 都产生稳定 error code 和明确 envelope。
14. 普通 Direct/CLI 回合与 `AgentStreamEvent` 契约无回归。

---

## 18. 明确不采用的方案

### 18.1 在当前 Direct stream 中递归调用 provider

它会共享 cancellation、工具集合、history 和 terminal protocol，无法持久化排队或恢复，也容易让 parent stream 同时消费 child delta。拒绝。

### 18.2 把 parent 全历史复制到 child

这扩大 token、隐私和 prompt injection 面，也让委派边界不可审计。child 只接受显式 task。拒绝。

### 18.3 用 token delta 作为 parent/child 通信

delta 没有 durable terminal 语义，重启和批量投递无法幂等。只投递 completion envelope。拒绝。

### 18.4 child 完成后直接写 parent assistant 文本

这绕过 provider reasoning、统一事件协议、工具调用和 turn finalization，也会制造没有模型 turn 的 transcript。通过 synthetic continuation。拒绝。

### 18.5 用内存 queue 作为任务真相

进程崩溃会丢 Queued task 与完成结果。Channel 只做唤醒，SQLite 才是真相。拒绝。

### 18.6 Running task 重启后自动重跑

provider 或工具可能已经产生外部副作用，自动重放不安全。恢复为 Interrupted，显式 retry 创建新 attempt。拒绝。

### 18.7 只靠 markdown 隐藏嵌套委派

恶意或错误定义可以重新声明能力。必须由 `ExecutionOrigin.Subagent` 在 capability resolver 层强制移除工具。拒绝。

### 18.8 对所有 continuation failure 无条件重试

已有工具调用时会重复写文件、命令或 MCP 副作用。只自动重试尚未产生工具调用的失败，否则 DeadLetter。拒绝。

---

## 19. 风险与后续方向

v1 保留以下已知风险：

- 多个 `system` Subagent 可以并发写同一 workspace，现有审批不能提供文件事务或冲突合并；
- application process 退出后不会继续运行，Queued 只会在下次启动恢复；
- provider call 不具备跨崩溃 resume，Running 只能标 Interrupted；
- continuation 的 provider/tool side effect 不能做到 exactly once；
- completion envelope 中的 child text 仍是模型生成内容，parent 必须按不可信结果处理；
- child transcript 暂时只有后端可查询，没有用户可见入口。

后续版本可在不扩大 `ISubagentTaskCoordinator` interface 的前提下增加：

- task/child transcript 前端详情；
- per-workspace 写锁或变更集隔离；
- 显式人工 retry / DeadLetter 管理；
- 中间 progress envelope；
- 更细粒度工具 capability policy；
- 跨进程 worker 与 provider-specific resume。

这些变化都应留在 durable task 和 mailbox 实现内，避免让 Direct runtime、provider adapter 或 Vue 学习调度细节。

---

## 20. 最终调用链摘要

```text
User-visible Direct turn
  -> ConversationTurnEngine
       -> concrete model id + TurnId(assistant message id)
       -> DirectChatTurnRequest(Origin=Interactive)
  -> DispatchingAgentChatRuntime
  -> DirectAgentChatRuntime
  -> DirectTurnCapabilityResolver
       -> existing workspace/extensions
       -> SubagentCapabilitySource
            -> delegate/get/cancel/retry tools
  -> delegate_to_subagent
  -> ISubagentTaskCoordinator.StartAsync
       -> SQLite transaction
            |- hidden child conversation + explicit task user message
            `- Queued subagent task + snapshots

SubagentTaskBackgroundHost
  -> durable FIFO claim (global 4, parent 3)
  -> DirectChatTurnRequest(Origin=Subagent, parent history excluded)
  -> same IAgentChatRuntime
  -> same AgentStreamEvent
  -> same ConversationTurnRecorder
  -> child messages/tool_runs + terminal assistant
  -> atomic task terminal + Pending delivery envelope

SubagentDeliveryDispatcher
  -> parent idle/user-priority gate
  -> lease/coalesce mailbox (32 KiB item, 64 KiB batch)
  -> DirectChatTurnRequest(
       Origin=Continuation,
       captured parent model/agent/capability ceiling,
       transient completion batch)
  -> same runtime/events/recorder
  -> parent assistant terminal persistence
  -> lease-token commit to Delivered
       or bounded retry / DeadLetter notification
```
