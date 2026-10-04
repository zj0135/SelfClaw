# Direct 消息输入：P2 独立审核

日期：2026-10-03。审核对象：基线 `ca42646b88b7088fc94f4e299b419c66f857b678` 上的当前未提交 P2 工作区。
实施者报告见 [P2 实施报告](direct-message-input-p2-implementation-review.md)，设计与任务见
[设计](direct-message-input-design.md)、[分阶段实施与验收](direct-message-input-implementation.md)。

本报告由独立复审者编写：重新构建、重新运行正式测试，并直接读码核对七项重点。审核阶段未修改生产代码、
未清理数据库或用户数据、未提交或推送。审核后按用户授权仅修复了审核发现的问题（测试夹具与一个死成员删除，见 §0.1），
并重新验证到连续 6 次全量稳定。

## 0. 验收结论

**P2 通过独立审核。** 生产实现满足 P2 验收契约，且首轮发现的测试夹具竞态 IR-P2-01
已在本次以测试专用修复消除：修复后连续 6 次 .NET 全量均为 1419 通过 / 0 失败 / 4 门控跳过。
设计 §13 的 P2 删除清单已落实，Queue/Steer 用户入口未开放。

**P2 可以进入 P3。** P3 门槛（目标 schema v30、独立 turn/message/usage、唯一运行准入、
同一事务内 writer、真实 SQLite 消费屏障、各链路已切换）均已满足。P3 仍须遵守：不开放
Steer、不得让 child/CLI 获得输入队列、不得绕过唯一准入。

原生/真实 provider/WPF/WebView2/进程恢复 smoke 仍未运行，属未验证项，不计通过（见 §5）。

### 0.1 本轮修复摘要（审核后授权修复）

| 项 | 处理 |
| --- | --- |
| IR-P2-01 测试夹具进程级连接池竞态 | **已修复**：新增测试专用 `SqliteTestPools.ClearFor(path)`，把全部 `SqliteConnection.ClearAllPools()` 换成路径作用域清理；`AiModelConfigurationTests` 中一条默认池化原始连接改为 `Pooling=False` |
| IR-P2-02 死成员 | **已修复**：删除无任何消费者的 `ConversationSessionCoordinator.SelectedToolRuns` |
| IR-P2-02 `_deletions` 无界 tombstone | 保留。该 tombstone 是“删除成功不再重建”的 I09 不变量的实现，删除它会重新打开已删除 id，不可在审核中顺手移除；留待 P6 评估 |
| IR-P2-03 重复 boundary 理论边界 | 保留。现有生产路径不可达；不为防御性分支新增恢复机制 |

修改范围仅限测试夹具/测试代码 + 1 个死成员删除，未改任何 P2 生产行为。

## 1. 审核范围与基线

- 基线提交：`ca42646`（P1 交付，HEAD 与基线相同，所有 P2 变更均为工作区未提交内容）。
- 已跟踪差异：`git diff ca42646` 为 130 个文件、+3091/−6068；另 47 个未跟踪文件（Core/Infrastructure/
  Desktop/Tests/Vue 源码、TestDoubles、docs）。
- 覆盖内容：`SelfClaw.Core`、`SelfClaw.Infrastructure`、`SelfClaw.Desktop`、`SelfClaw.Tests`、
  `SelfClaw.TranscriptVue`、`docs/`、`AGENTS.md`、`CONTEXT.md`。
- 审核未依据实施者的测试数量结论，重新运行了 M01–M12 关键用例、.NET 全量、Vue/Vitest、Vite build 与 Playwright。

## 2. 命令与结果

环境：Windows；.NET SDK 10.0.400；Node v22.19.0 / npm 10.2.5；隔离输出 `bin/p2-ind-review/`，
不触碰用户 Desktop 的既有 DLL，不杀用户进程。

```powershell
$env:DOTNET_CLI_HOME = "$PWD/.dotnet"
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_NOLOGO = '1'

dotnet build SelfClaw.slnx --no-restore -p:BaseOutputPath=bin/p2-ind-review/ --nologo
dotnet test SelfClaw.slnx --no-restore -p:BaseOutputPath=bin/p2-ind-review/ `
  --logger 'trx;LogFileName=p2-ind-review-full.trx' `
  --results-directory .scratch/direct-message-input/p2-ind-review
dotnet test SelfClaw.slnx --no-build --no-restore -p:BaseOutputPath=bin/p2-ind-review/ `
  --logger 'trx;LogFileName=rerun-1.trx' --results-directory .scratch/direct-message-input/p2-ind-review
dotnet test SelfClaw.slnx --no-build --no-restore -p:BaseOutputPath=bin/p2-ind-review/ `
  --logger 'trx;LogFileName=rerun-2.trx' --results-directory .scratch/direct-message-input/p2-ind-review
dotnet test SelfClaw.Tests/SelfClaw.Tests.csproj --no-build --no-restore -p:BaseOutputPath=bin/p2-ind-review/ `
  --filter 'FullyQualifiedName~DirectInputBoundaryIntegrationTests|FullyQualifiedName~DirectPromptTurnHistoryTests|FullyQualifiedName~DirectTurnInputSessionTests|FullyQualifiedName~SqliteConversationInputRepositoryTests|FullyQualifiedName~SqliteConversationTurnRepositoryTests|FullyQualifiedName~SqliteSchemaInitializationTests|FullyQualifiedName~ConversationRunCoordinatorTests|FullyQualifiedName~ConversationDeletionServiceTests|FullyQualifiedName~DirectUsagePersistenceTests|FullyQualifiedName~ConversationTurnRecorderTests|FullyQualifiedName~TranscriptTurnOutcomeTests|FullyQualifiedName~SqliteSubagentTaskRepositoryTests|FullyQualifiedName~ContinuationRecoveryTests|FullyQualifiedName~SubagentDeliveryDispatcherTests|FullyQualifiedName~CliAgentChatRuntimeTests' `
  --logger 'trx;LogFileName=p2-ind-review-m01-m12.trx' `
  --results-directory .scratch/direct-message-input/p2-ind-review
dotnet test SelfClaw.Tests/SelfClaw.Tests.csproj --no-build --no-restore -p:BaseOutputPath=bin/p2-ind-review/ `
  --filter 'FullyQualifiedName~Delivery_terminal_fault_rolls_back_delivery_turn_fragment_and_usage' `
  --logger 'trx;LogFileName=isolate.trx' --results-directory .scratch/direct-message-input/p2-ind-review
npm --prefix SelfClaw.TranscriptVue test
npm --prefix SelfClaw.TranscriptVue run build
npm --prefix SelfClaw.TranscriptVue run test:e2e
git diff --check ca42646
```

| 执行 | 通过 | 失败 | 跳过 | 结果文件 / SHA-256 |
| --- | ---: | ---: | ---: | --- |
| build | — | 0 | — | 0 警告 / 0 错误 |
| 全量首轮 `p2-ind-review-full.trx` | 1418 | **1** | 4 | `2ae82ed6…4cbfce` |
| 全量复跑 1 `rerun-1.trx` | 1419 | 0 | 4 | `e1b53b88…0f5e09` |
| 全量复跑 2 `rerun-2.trx` | 1419 | 0 | 4 | `c66fa929…bbea473` |
| M01–M12 定向 `p2-ind-review-m01-m12.trx` | 156 | 0 | 0 | `bd74717b…890339` |
| 失败用例单独复跑 `isolate.trx` | 1 | 0 | 0 | `aac6bf91…6dbf1cf` |
| Vue `npm test` | 94（23 文件） | 0 | 0 | 与报告一致 |
| Vue `npm run build` | 成功 | 0 | — | Vite 构建成功 |
| Playwright `npm run test:e2e` | 15 | 0 | 0 | 与报告一致 |
| `git diff --check` | 成功 | — | — | 仅 LF→CRLF 提示，无空白错误 |

### 2.1 IR-P2-01 修复后的验证（本轮）

| 执行 | 通过 | 失败 | 跳过 | 结果文件 / SHA-256 |
| --- | ---: | ---: | ---: | --- |
| 全量 final-1 | 1419 | 0 | 4 | `0b69b300…7bd567e` |
| 全量 final-2 | 1419 | 0 | 4 | `8d1af36b…f8ca4` |
| 全量 final-3 | 1419 | 0 | 4 | `a19b1c04…be95a53` |
| 全量 final-4 | 1419 | 0 | 4 | `1e08cd36…2e15ced` |
| 全量 final-5 | 1419 | 0 | 4 | `c2511f23…7ac9cf` |
| 全量 final-6 | 1419 | 0 | 4 | `cb1bef14…f440383` |
| M01–M12 定向（修复后） | 156 | 0 | 0 | `794e1442…408507` |

修复前 3 次全量为 1 失败 + 2 通过；修复后 6/6 全绿，且不再出现 `ObjectDisposedException`
与 `IOException: test.db is being used by another process`。

四项跳过仍为 Activity provider／process recovery／WebView smoke（未设置环境变量）。

## 3. 七项重点核对结论

### 3.1 TurnId 与 MessageId 真正分离（通过）

- `messages` 表新增必填 `turn_id`/`sequence`、`UNIQUE(conversation_id, sequence)`、`UNIQUE(id, conversation_id)`、
  `CHECK(id <> turn_id)`，并以复合外键绑定 turn/会话（`SqliteSchema.cs`）。
- `MessageRecord` 已删除 `Usage`/`DurationMs`/`ErrorMessage`；`MessageStatus` 仅 `Sealed/Streaming/Interrupted`
  （`SelfClaw.Core/Models/Conversations/`）。回合状态、错误、时长、用量只由 `ConversationTurnRecord`/`turn_usage` 提供。
- 工具锚点是实际 assistant 片段：`tool_runs.message_id` 必填 + 触发器拒绝 user/system 锚点；
  `message_segments(kind=ToolCall)` 通过 `(tool_run_id, message_id)` 与同片段绑定。
- 首条 assistant 不再承载回合：recorder 在首次正文/thinking/工具/notice 时才创建片段
  （`ConversationTurnRecorder.EnsureAssistantAsync`），`AgentTurnState` 仅保留逻辑 TurnId 与可空
  `CurrentAssistantMessageId`。无内容回合可只写 turn + user。
- 无正文回合的展示：`TranscriptProjection.BuildItems` 在无 assistant 的回合追加独立 `turn-outcome` 项
  （`TranscriptMessageProjector.BuildTurnOutcome`），不在存储层制造空 assistant。
  正式测试：`TranscriptTurnOutcomeTests.No_output_turn_projects_a_distinct_outcome_without_an_assistant_message`、
  `SqliteConversationTurnRepositoryTests.No_output_terminal_persists_turn_usage_with_unknown_total_and_no_assistant`、
  `DirectInputBoundaryIntegrationTests.Multiple_boundaries_before_output_never_create_or_move_an_assistant`，均通过。

### 3.2 运行注册/准入/取消/完成/删除屏障的唯一 owner（通过）

- `ConversationRunCoordinator` 是唯一 owner：一个短 metadata lock 只保护 `_runs`/`_deletions`/`_stopping` 的裁决，
  锁内不做历史加载、provider、工具、审批、ACK 或任务等待；准备前即预留 handle。
- Engine 只执行已准入 handle（`BeginExecution` 防止重复执行），不再有 `_turnAdmissionGate`/`_pendingInteractiveAdmissions`；
  SessionCoordinator 只保留选择锁与加载合并，读取 coordinator 的活动快照，无独立可写 IsRunning；
  dispatcher 只调用 `GetUnavailableContinuationParents`/`TryReserveContinuationAsync`，未复制 busy 状态机。
- 删除屏障：`ConversationDeletionService` 先对全部 id `BeginDeletion`，再 `StopAndWaitAsync` → child
  `CancelAndWaitAsync` → release/cascade；成功 tombstone 保留（`EndDeletion(deleted:true)` 置 null），
  迟到失败不能重新打开。并发删除、child 超时中止级联均有正式测试。
- 锁内无等待执行/审批/提交 ACK：`RaiseTranscriptChanged`→`TranscriptPublisher.PublishNow` 在非 UI 线程走
  `Dispatcher.BeginInvoke`（非阻塞），边界 ACK 在 UI 发布之前，不存在拿 coordinator 锁等待 UI/ACK 的路径。

### 3.3 输入边界顺序与屏障语义（通过）

- 有序链：provider 事件与 `TurnInputBoundaryEvent` 走同一 single-reader/single-writer 无界 Channel；
  recorder 逐个事件归约，工具结果进度经 `CommitProgressAsync`（独立事务）同步提交后才处理边界事件。
  随后 `SqliteConversationInputRepository.CommitBoundaryAsync` 在**单事务**内封存片段、写 user、更新 usage、
  标记整批 Consumed、递增 queue_revision。成功后才 `inputSession.Confirm`，再发布 UI。
- `DirectConversationLoop.ConsumeInputAsync` 在 `WaitForCommitAsync` 返回前不发起下一次 provider 请求；
  发起前另有一次 `cancellationToken.ThrowIfCancellationRequested`。
- `DirectTurnInputSession`：单个 pending slot、一个 waiter、状态与注册同锁；`Fail/Cancel/Close` 首个终态获胜；
  迟到 waiter 立即结束；第二个不同 boundary 被拒绝；重复确认按 claim/消费映射重读，不重复写。
  正式测试覆盖早/晚注册、提交异常、取消、关闭、重复确认、第二 boundary
  （`DirectTurnInputSessionTests`、`DirectInputBoundaryIntegrationTests`）全部通过。
- **执行屏障与 WebView ACK 解耦**：边界等待只认 `CommitBoundaryAsync`/`ReadConsumptionAsync` 的消费事实；
  UI 发布失败被 `CommitBoundaryAsync` 捕获记录，不撤销 Consumed、不阻塞 ACK
  （`Frontend_publication_failure_after_commit_does_not_block_ack_or_reconsume` 通过）。
- M04 真实 SQLite 屏障：`Actual_sqlite_commit_is_a_barrier_before_consumption_and_next_provider_request`
  用第二个连接在 `boundary-before-commit` 处确认 Consumed=0、usage=0、无 U1，且 provider 请求数不增长；通过。

### 3.4 共用事务内 writer、detached continuation、原子 delivery（通过）

- `SqliteConversationTurnWriter` 只使用调用方传入的 connection/transaction，被 start/progress/boundary/terminal、
  child claim/complete、delivery resolution 共用；无 writer 内自开事务、无 repository 嵌套提交。
- 终态：`SqliteConversationTurnRepository.TryFinalizeTurnAsync`→同一 writer；`DesktopTurnFinalizer` 仅做有界重试。
- continuation：`SubagentContinuationExecutor` 全程 `ApplyDetachedEventAsync`（`persistProgress:false`），
  turn/message/tool/usage 只在内存；`SubagentContinuationTurnCommitter` 经
  `SqliteSubagentDeliveryRepository.TryResolveAsync` 在**同一事务**内写 `conversation_turns`+
  messages+tools+usage 并置 Delivered/DeadLetter。`allowInsert:true` 支持此前无 Running 行的 continuation。
  正式测试在终态前断言 parent 无 turn/messages/usage，通过。

### 3.5 顺序/片段对齐/工具归属/Blocked/Truncated 回放一致（通过）

- `ListMessagesAsync` `ORDER BY sequence`；`DirectPromptComposer.OrderAndValidateMessages` 拒绝重复序号与
  跨会话 turn，`BuildHistoryWithinBudget` 以完整单元回放。
- `TerminalBlockAligner` 只对当前片段做 FinalText 对齐，保留 thinking/notice 前缀；M02 断言 A1 终态仅 `A1 done`、
  A0 正文不被拼回。A0/A1 各含正确工具归属（`DirectInputBoundaryIntegrationTests` 通过）。
- `IsReplayable`：Blocked 回合整体不回放；非 Sealed 片段不回放；工具只回放锚定实际片段且为终态的记录。
  `DirectPromptTurnHistoryTests` 覆盖 Blocked/Cancelled/Failed/Truncated 与数据错误，全部通过。
- Truncated 续写只在最后实际片段后追加一次 `ContinuationPrompt`（`MaxBy(sequence)`），测试通过。

### 3.6 P1 用量契约保持（通过）

- `TurnUsageAccumulator` 保持“未知 total 具传递性”：任一观测 total 为 null 则最终为 null，删除 input+output 回退；
  `turn_usage.total_tokens` 可为 SQL NULL。
- 边界快照只作绝对覆盖，不 `Observe` 进 recorder 贡献累加器；终态一次性覆盖同一行。
  `Boundary_snapshots_and_final_usage_overwrite_one_turn_row_without_accumulating` 断言 input=21/output=9/calls=3
  且只有一行、total 保持 null，通过。
- `DirectUsagePersistenceTests` 三条链路（Interactive/child/continuation）各自真实重读 SQLite：
  `incremental-unknown`、`cumulative-partial-error` 保持 SQL NULL；`incremental-complete` 为 17。9 项通过。
- CLI parser 归一化（`CliUsageNormalizationTests`）未回归；child 结果由 `turn.Usage` 构造，不再从 assistant message 取。

### 3.7 旧路径删除、无越界 Queue/Steer、无多余抽象（通过，含低优先清理项）

- 搜索确认删除：`SqliteTurnFinalizationWriter`、`ITurnFinalizationRepository`、Core `TurnFinalization`、
  `AdmittedConversationTurn`、`MessageCreated`、Engine 准入字段、`AddUserMessageAsync`/`UpsertMessageAsync`/
  `UpsertToolExecutionAsync`、生产 `UseFunctionInvocation`、P0 `InputBoundaryBarrierBaselineTests` 均无残留。
- 无 `conversation-input/*` bridge/service/dispatcher；`ReadClaimedBatchAsync` 只读已 Claimed 的 Steer 批次，
  没有受理、容量、FIFO、暂停恢复、工具 supersede 或 UI 队列。Vue 变更仅 transcript 展示（turn-outcome）。
- 未引入通用工作流框架；新增接口均为跨层真实边界（`IConversationTurnRepository`、`IConversationInputRepository`、
  `IDirectTurnInputSession`），1–5 方法。
- 低优先清理项见 §4 IR-P2-02。

## 4. 发现的问题

### IR-P2-01（中优先，已修复）全量回归存在测试夹具全局连接池竞态

状态：**已修复并由 6 次全量复跑验证。** 以下保留原始发现与根因；修复见 §0.1。

位置：
- `SelfClaw.Tests/TestDoubles/ConversationPersistenceFixture.cs`（`Dispose` 调用
  `SqliteConnection.ClearAllPools()`，P2 新增）
- `SelfClaw.Tests/Infrastructure/Data/Sqlite/SqliteSchemaInitializationTests.cs:101`
  （测试中途调用 `SqliteConnection.ClearAllPools()`，P2 新增）
- 既有同类调用：`AiModelConfigurationTests.cs:164`、`GitWorkspaceServiceTests.cs`（4 处）。

触发：xUnit 默认跨测试类并行。某些测试线程调用 `SqliteConnection.ClearAllPools()`（进程级）时，
另一线程正在 `SqliteDatabase.OpenConnectionAsync` 打开池化连接，其原生 `sqlite3` 句柄被并发释放。

实际结果（全量首轮 `p2-ind-review-full.trx`）：

```
Failed SelfClaw.Tests.Infrastructure.Agents.Subagents.Persistence.SqliteSubagentTaskRepositoryTests
  .Delivery_terminal_fault_rolls_back_delivery_turn_fragment_and_usage
System.ObjectDisposedException : Cannot access a disposed object. Object name: 'SQLitePCL.sqlite3'
  at ...SqliteConnection.Open() -> SqliteCommand.ExecuteNonQuery()
  at SqliteDatabase.OpenConnectionAsync(...) SqliteDatabase.cs:35
  at SqliteSubagentTaskRepository.CreateAsync(...) SqliteSubagentTaskRepository.cs:39
```

预期：全量回归每次稳定 1419/0/4。
影响：实施者报告的“全量 1419 通过”不可稳定复现；该失败与生产行为无关，但不能用“重跑变绿”掩盖。
复现性：3 次全量中失败 1 次、通过 2 次；失败用例单独复跑通过；相关类定向复跑 5 次全通过。
修复方向（已在 §0.1 实施）：新增路径作用域的 `SqliteTestPools.ClearFor(path)` 取代全部
`SqliteConnection.ClearAllPools()`，并把指向临时库的原始默认池化连接改为 `Pooling=False`；
不靠重跑。

### IR-P2-02（低优先，部分修复）少量仅测试使用的访问器与无界 tombstone

- `ConversationRunCoordinator.IsRunning` / `RunningConversationIds` 仍作为协调器的内部测试可观测性保留。
- `ConversationSessionCoordinator.SelectedToolRuns` 已删除（生产与测试均无消费者）。
- `ConversationRunCoordinator._deletions` 在成功删除后保留 `null` tombstone 直到进程结束；这是 I09
  “删除成功不再重建”的实现，**不应在审核中移除**，留待 P6 评估更细的生命周期。
不属于 P2 验收阻塞项。

### IR-P2-03（低优先，理论边界，未证实可复现）重复 boundary 且消费已存在时的片段归属

`ConversationTurnRecorder.CommitBoundaryAsync` 在 `ReadConsumptionAsync` 返回既有消费、但其 user 消息尚未进入
内存时，会先 `CaptureBoundary`（把当前片段标记为 Sealed 写入内存）再 `turn.CurrentAssistantMessageId = null`，
但该 `content` 并未经本次事务持久化（`consumed ??=` 保留了外部消费）。若此后发生终态提交，该片段可能只存在于内存。
当前生产边界流由 recorder 单次提交、无 ACK 重放，因此该分支在现有路径不可达；仅作为防御性缺口记录，
不建议在 P2 收尾阶段为它新增恢复机制。

## 5. 未验证项（不计通过）

- 真实 provider / Ollama 服务、真实 WPF/WebView2、toast、原生进程恢复 smoke：4 项门控跳过，未运行。
  与实施者 §10 声明一致。
- Provider 协议第二次 HTTP body 序列化仍为离线 fixture，未做真实服务验证。
- IR-P2-01 已在授权修复阶段关闭；全量“稳定全绿”由修复后 6 次复跑达成（§2.1）。

## 6. 与实施者报告的差异

- 实施者报告的正式全量 1419/0/4 在修复前首跑未复现（1418/1/4）；差异是 §4 IR-P2-01 的测试夹具竞态，
  不是功能失败。修复后 6 次全量均与报告一致。
- 其余计数（Vue 94、build 成功、Playwright 15、定向 156）独立复现一致。
- 未发现实施者报告夸大生产功能完成度；P2 明确未开放 Queue/Steer，与代码一致。

## 7. 验收判定

| 项 | 判定 |
| --- | --- |
| I13 TurnId/MessageId 分离、回合状态/用量/错误归 turn | 通过 |
| I01/A01 单一运行准入、取消/完成/删除屏障唯一 owner | 通过 |
| I05/A07 有序消费屏障、单 waiter、无 ACK 表/重试 dispatcher | 通过 |
| I15/A04 同一事务内 writer、detached continuation、原子 delivery | 通过 |
| I06/M10 顺序、片段对齐、工具归属、Blocked/Truncated 回放 | 通过 |
| I07/M09/P1 用量未知 total 与完整 total | 通过 |
| I16/A08 删除清单落实、无 Queue/Steer 越界 | 通过 |
| 全量回归“稳定绿色” | **通过（6/6，修复 IR-P2-01 后）** |
| 原生/真实 provider/进程恢复 smoke | 未验证 |

**结论：P2 生产实现通过独立审核，全量回归稳定绿色。P2 可以开启 P3，但 P3 不得开放 Steer、
不得让 child/CLI 获得输入队列、不得绕过唯一准入。原生/真实 provider/进程恢复 smoke 仍为未完成门槛。**

## 8. 状态同步

- [分阶段实施与验收](direct-message-input-implementation.md) P2 行状态更新为“独立审核通过；IR-P2-01/02 已修复”。
- [设计](direct-message-input-design.md) 顶部 P2 状态同步。
- 本文件保留在工作区；.scratch 下的原始 TRX 为一次性证据，摘要与 SHA-256 已固化在上文。
