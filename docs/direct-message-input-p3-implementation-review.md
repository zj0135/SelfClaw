# Direct 消息输入：P3 持久化 Queue 实施报告

日期：2026-10-03。基线：`17243f9`（P2 独立审核通过）。范围：只实现 P3；未开放 Steer。

> **开关状态更新（2026-10-08）：** 本文所记 Queue／Steer 功能开关已在 `8ad27da` 删除、两者始终开启；下文保持当时原文。权威记录见 [分阶段实施与验收](direct-message-input-implementation.md) 文末「2026-10-08 功能开关移除」。

设计与任务见 [设计](direct-message-input-design.md)、[分阶段实施与验收](direct-message-input-implementation.md)、
[P2 实施报告](direct-message-input-p2-implementation-review.md)。本报告由实施者编写，**不代表独立审核通过**。

## 1. 范围与结论

- 交付 Direct 忙碌时可入队、当前回合成功收尾后按 FIFO 自动逐项执行；编辑／取消／暂停／恢复、最小桥接与 Vue 队列。
- 未实现、未部分实现 Steer：无 `targetTurn` 领取、无审批 supersede、无工具跳过、无 `SuspendedBySteer`；`conversation-input/promote|steer` 明确返回 `steer-not-enabled`。
- 未新增第二套 input model、busy 表或运行状态机；受理／调度复用 P2 的 `conversation_inputs` / `conversation_input_state` / `ConversationRunCoordinator`。
- schema 仍为 **v30**，未新增 DDL，未 bump 版本。
- child/CLI 不获得输入队列：数据库触发器拒绝非 Interactive 所有者；CLI 发送保留既有 VM→run coordinator 准入路径。
- 所有 Direct 发送（空闲、忙碌、恢复）共用 same accept → dispatcher → 唯一准入路径，删除 VM 空闲旁路。
- 未开始 P4/P5/P6；未清理数据；未使用真实用户数据库/AppData；未 git commit/push。

### 1.1 功能开关

`ConversationInputFeatureSwitch`（`SELFCLAW_QUEUE_ENABLED`，默认关闭）由宿主控制。关闭时：

- 忙碌会话拒绝新输入（`queue-disabled`），已持久化输入保留；
- 后台 dispatcher 不扫描/不继续调度 backlog；
- 空闲单条发送仍经同一受理路径立即执行（否则默认 Direct 不可用）。

验收环境启用：设置 `SELFCLAW_QUEUE_ENABLED=1`。

## 2. 职责迁移与删除清单

| 原职责／路径 | 新 owner | 处理 |
| --- | --- | --- |
| VM `SendAsync` 空闲 Direct 直接 `TryReserve` + Engine | `ConversationInputService` 受理 + `ConversationInputDispatcher` | Direct 分支改走 `SubmitDirectAsync`；CLI 保留 `SubmitCliAsync`（原路径） |
| VM 直接 `AddUserMessageAsync`（本次为 start 事务内建） | `SqliteConversationTurnWriter.StartAsync` 的 `ConsumeFollowUpAsync` | 已有 P2 统一 writer，P3 增加 claim 消费 |
| 无 | `ConversationInputService` / `ConversationInputDispatcher` / `ConversationInputPublisher` / `ConversationInputBridge` / `ConversationInputChangeNotifier` | 新增 Desktop 模块 |
| 无 | `IConversationInputStore` / `IConversationInputSchedulerStore` / `ConversationInputCoordinator` | Core 契约 + Desktop 消费者接口 |
| `SqliteConversationInputRepository` 只读 Claimed Steer | 同 repository 实现受理／条件更新／容量／停止恢复／claim／evidence | 复用同一表，不新建 model |
| Vue `useChatComposer` 忙碌禁止提交 | Direct 忙碌允许入队（CLI 仍禁止） | `queueable` prop + `ConversationQueue.vue` + `useConversationInputs.js` |

未删除的保留项：P2 的 `DirectTurnInputSession`（FollowUp 期间仍具备 steer 边界读取能力，但生产无 Claimed Steer 时返回 null）；`conversation_inputs` 的 Steer 列与触发器保留（P4 使用）。

## 3. 关键实现

### 3.1 存储层（P3.1）

`SqliteConversationInputRepository` 实现 `IConversationInputStore` + `IConversationInputSchedulerStore`：

- `AcceptAsync`：同事务完成 conversation upsert、容量检查（每会话 20、全局 500）、`next_input_sequence` 分配、`queue_revision` 递增与 `client_request_id` 幂等；正文按 UTF-8 ≤64 KiB 拒绝（`ArgumentException`）；队列关闭时若存在未处理项返回 `Rejected/queue-disabled`；重复请求比较正文/目标/执行快照，不同返回 `Conflict`。
- `EditAsync`/`CancelAsync`/`HoldAsync`/`RetryAsync`：全部条件更新（`status` + `revision`），状态变更与 `queue_revision` 递增同事务；`Claimed` 不可改。
- `SetPausedAsync`：`paused` 落库，条件 `queue_revision`。
- `TryClaimNextFollowUpAsync`：`BEGIN IMMEDIATE` 事务内检查 paused、按 sequence 取首条 Pending FollowUp、条件 `UPDATE` 置 Claimed 并递增 revision。
- `ListConversationIdsWithUnprocessedInputsAsync` / `RecoverStartupAsync`：启动恢复，Claimed→Held、目标已终态的 Pending Steer→Held、有未处理项的会话 `paused=1`。
- `GetQueueStateAsync`：≤200 字符预览、`CanSteer=false`、只列 Pending/Claimed/Held；终态项不出现。

### 3.2 统一消费事务（P3.3/P3.4）

`ConversationTurnStart` 增加可选 `ConversationInputClaim`；`SqliteConversationTurnWriter.StartAsync` 在**同一事务**内匹配 claim、创建 Running 回合、写所属 user、标记输入 Consumed、递增 `queue_revision`。事务成功前不发出任何 provider 请求/工具执行。`ConversationInputMessageId.Compute` 保证 claim × input 的稳定 user id。

**边界与消费事实复用 P2**：`SqliteConversationInputRepository.CommitBoundaryAsync` 与 `SqliteConversationTurnWriter` 未新增 user 写入路径。

### 3.3 ConversationInputService（P3.2）

- 受理时冻结执行快照：AgentId、`AgentSettingsService.Revision`、Direct 模式、具体 `modelProfileId`（默认模型在受理时解析为具体 ID）、Workspace Root ID+路径、`ToolPermissionMode`。
- 幂等：同 requestId 返回同 inputId；正文/目标/模式不同返回 conflict。
- 编辑/取消校验 revision；附件返回 `UnsupportedAttachments`。
- 新会话由 VM 先准备 Workspace Root 再受理（沿用 P2 工作树清理规则）。

### 3.4 Dispatcher（P3.5）

`ConversationInputDispatcher`（`BackgroundService`）：

- 只发现候选、请求 `ConversationRunCoordinator.TryReserve` 准入、claim FIFO 头、执行 Engine、据结果唤醒下一项。
- 每个 `RunAsync` 依据 `ConversationTurnExecutionResult.Persisted && Turn.Status == Succeeded` 判定；`Persisted==false` 优先映射 `persist-failed`。不以 `RunCompletedEvent(Succeeded)` 或 `IsRunning==false` 推断成功。
- 失败即暂停该会话队列；`RunAsync` 后台任务独立，一会话失败不影响全局，扫描有 250 ms 间隔无忙循环。
- `ConversationRunCoordinator.TryReserveContinuationAsync` 已检查 `HasUnprocessedInputsAsync`，用户输入优先于 continuation（Q09）。
- 执行前重校验：Agent 存在、`Revision` 未变、模型 `IsModelAvailableAsync`、Workspace Root 仍匹配路径；任意失败 → 该输入 Held、会话暂停、不越过（Q08）。
- stop：VM 先 `PauseAsync` 再 `_runs.Stop`；dispatcher 对 Cancelled 结果亦暂停。

### 3.5 桥接与 Vue（P3.6）

- `ConversationInputBridge`：`conversation-input/subscribe|get-state|unsubscribe|edit|cancel|pause|resume|retry|detail`；`promote|steer` 拒绝。请求经应用 origin guard 后进入。
- `ConversationInputPublisher`：单活动订阅，通知只作失效信号，每次重读持久化 store 并推送；旧 subscription/旧 `queueRevision` 不覆盖新选择；快照序列化后若超 256 KiB 截断 items。
- Vue：`useConversationInputs`（reducer 拒绝跨会话/旧 revision）、`ConversationQueue.vue`（列表、取消、暂停/恢复、Held 重试，Lucide 图标 + scoped styles）；`ChatView` 只接线；Direct 忙碌时 composer 显示「加入队列」，ACK 后清框；队列变化不强制 transcript 跟随；`canSteer` 恒 false。

### 3.6 启动恢复（P3.7）

dispatcher `ExecuteAsync` 首先调用 `RecoverOnceAsync`（dispatcher 启动前扫描）：统一 paused、Claimed→Held、目标已终态的 Pending Steer→Held；Consumed 不自动重跑。

## 4. 验收用例 → 测试

.NET 新增/更新测试（命令见 §5）：

| ID | 测试全名 | 结果 |
| --- | --- | --- |
| Q01 | `SelfClaw.Tests.Desktop.Services.ConversationInputs.ConversationInputDispatcherTests.A_successful_turn_advances_the_fifo_one_independent_turn_at_a_time` | 通过 |
| Q01 | `...A_successful_turn_advances...` 断言每个 FollowUp 独立 `turn_id`、user 消息按序 A/B/C | 通过 |
| Q02 | 同上（空闲已有队列时 B/C 先于后到的 D，不绕过） | 通过 |
| Q03 | `SelfClaw.Tests.Infrastructure.Data.Sqlite.Repositories.SqliteConversationInputQueueTests.Acceptance_is_idempotent_and_rejects_a_changed_payload_as_a_conflict` | 通过 |
| Q03 | `...Claim_and_start_atomically_write_one_user_and_mark_the_input_consumed`（每条 input 恰一 user） | 通过 |
| Q04 | `...Edit_and_cancel_honor_revision_and_a_claim_makes_the_item_immutable` | 通过 |
| Q05 | `...ConversationInputDispatcherTests.A_terminal_persistence_failure_pauses_the_queue_with_the_persist_reason` | 通过 |
| Q06 | `...Non_success_turn_outcomes_pause_the_queue`（Failed/Blocked/Truncated 三态） | 通过 |
| Q06 | `...A_failed_turn_pauses_the_queue_and_never_starts_the_next_item` | 通过 |
| Q07 | `...Stop_during_a_running_turn_prevents_the_next_item_and_pauses_with_cancelled_reason`（停止先羁） | 通过 |
| Q07 | `...Stop_after_the_next_item_is_admitted_cancels_that_turn_and_pauses`（B 已准入） | 通过 |
| Q07 | `...Stop_pauses_the_queue_so_the_next_item_does_not_start` | 通过 |
| Q07 | `SelfClaw.Tests.Desktop.ViewModels.MainWindowViewModelSubmissionTests` 与 `MainWindowViewModel.StopSelectedConversation`（先落库暂停再取消） | 通过 |
| Q08 | `...A_disabled_model_holds_the_input_and_does_not_cross_it` / `...An_agent_revision_change_holds_the_input_and_pauses` | 通过 |
| Q09 | `SelfClaw.Tests.Desktop.Services.Runtime.ConversationRunCoordinatorTests.A_user_queue_blocks_only_its_own_continuation_and_not_another_parent` | 通过 |
| Q09 | `...One_failing_conversation_does_not_block_another_conversation`（隔离） | 通过 |
| Q10 | `SelfClaw.Tests.Desktop.ViewModels.ShellStateTests.Direct_submission_keeps_its_submit_time_target_after_navigation` | 通过 |
| Q10 | `...ConversationInputHostTests.Publisher_ignores_other_conversations_and_refreshes_on_reload` | 通过 |
| Q11 | `SelfClaw.Tests.Desktop.Services.Runtime.ConversationDeletionServiceTests.A_failed_delete_releases_the_barrier_but_keeps_the_durable_queue_paused` | 通过 |
| Q11 | `...A_successful_delete_removes_the_queue_and_never_reclaims`；`...Sqlite_constraints_reject_missing_turn_cross_conversation_and_consumed_without_mapping`（级联） | 通过 |
| Q12 | `...Startup_recovery_holds_old_claims_stale_steers_and_pauses_without_replaying`（store） | 通过 |
| Q12 | `...ConversationInputDispatcherTests.A_restart_holds_a_previous_claim_and_never_replays_it`（重启入口不重放） | 通过 |
| Q13 | `...Acceptance_rejects_oversized_prompts_and_does_not_evict_the_oldest_item` / `...Global_capacity_is_enforced_across_conversations` | 通过 |
| Q14 | `...A_start_transaction_failure_leaves_no_orphan_turn_user_or_consumed_input` / `...A_stale_claim_revision_rolls_back_the_start_transaction` | 通过 |
| Q15 | `...One_failing_conversation_does_not_block_another_conversation` | 通过 |
| Q16 | `...Attachments_are_rejected_explicitly` / `...A_disabled_switch_keeps_a_persisted_backlog_but_does_not_schedule_it` | 通过 |
| A06 | `SelfClaw.Tests.Desktop.ViewModels.MainWindowViewModelSubmissionTests.SubmitPromptAsync_accepts_direct_through_the_input_coordinator_without_executing_a_turn` | 通过 |
| A06 | `SelfClaw.Tests.Desktop.Composition.DesktopServiceRegistrationTests.Desktop_registration_resolves_the_input_queue_graph` | 通过 |

Host 桥接专项（`ConversationInputHostTests`，WPF dispatcher + 真实 store）：

| 场景 | 测试 | 结果 |
| --- | --- | --- |
| 订阅发布有界队列 + detail 受限正文 | `Subscribe_publishes_the_bounded_queue_and_detail_reads_the_restricted_body` | 通过 |
| 编辑/取消/暂停/恢复 revision 绑定 + 旧 subscription 拒绝 | `Edit_cancel_pause_and_resume_are_revision_bound_and_reject_stale_subscriptions` | 通过 |
| Steer/promote 明确拒绝 | `Steer_and_promote_are_rejected_explicitly` | 通过 |
| 跨会话通知隔离 + 重载重拉 | `Publisher_ignores_other_conversations_and_refreshes_on_reload` | 通过 |

Vue（Vitest）：

| 场景 | 测试 | 结果 |
| --- | --- | --- |
| 入队/列表/状态应用 | `tests/conversationInputs.test.js` `subscribes, applies pushed state and sends revision-bound operations` | 通过 |
| 旧订阅/旧 revision 不覆盖 | `rejects a push from another conversation or an older revision` / `drops state pushed for a conversation that is no longer selected` | 通过 |
| 取消/暂停 revision 绑定 | 同上（断言 `expectedRevision` / `expectedQueueRevision`） | 通过 |
| 重载恢复 | `restores state on reload by resubscribing to the selected conversation` | 通过 |
| ACK 清框 | `tests/chatComposer.test.js` `keeps the draft until accepted...` | 通过 |
| Direct 忙碌入队 / CLI 忙碌阻断 | `queues a Direct submission while a turn is busy` / `still blocks a busy CLI submission` | 通过 |

全部 Q01–Q16 已有正式测试覆盖。

## 5. 命令与计数

环境：Windows；.NET SDK 10.0.400；Node v22.19.0；隔离输出 `bin/p3/`；未锁用户 DLL。

```powershell
dotnet build SelfClaw.slnx --nologo -v q -p:BaseOutputPath=bin/p3/          # 0 警告 / 0 错误
dotnet test SelfClaw.Tests/SelfClaw.Tests.csproj --no-build --no-restore -p:BaseOutputPath=bin/p3/ `
  --logger 'trx;LogFileName=p3-final-2.trx' --results-directory .scratch/direct-message-input/p3
npm --prefix SelfClaw.TranscriptVue test
npm --prefix SelfClaw.TranscriptVue run build
npm --prefix SelfClaw.TranscriptVue run test:e2e
```

| 执行 | 通过 | 失败 | 跳过 |
| --- | ---: | ---: | ---: |
| build（slnx） | — | 0 | — |
| .NET 全量 `p3-final-1.trx` / `p3-final-2.trx`（连续两次） | 1454 | 0 | 4（provider/process-recovery/WebView smoke） |
| P3 存储定向 `SqliteConversationInputQueueTests` | 11 | 0 | 0 |
| P3 dispatcher 定向 `ConversationInputDispatcherTests` | 15 | 0 | 0 |
| P3 host 桥接 `ConversationInputHostTests` | 4 | 0 | 0 |
| P3 删除恢复 `ConversationDeletionServiceTests` | 5 | 0 | 0 |
| P3 准入隔离 `ConversationRunCoordinatorTests` | 16 | 0 | 0 |
| Desktop 组合根 `DesktopServiceRegistrationTests` | 1 | 0 | 0 |
| Vue `npm test` | 100 | 0 | 0 |
| Vue `npm run build` | 成功 | 0 | — |
| Playwright `npm run test:e2e` | 15 | 0 | 0 |

P2 基线 .NET 全量为 1419/0/4；P3 新增 35 项（存储 11、dispatcher 15、host 4、删除 2、准入 1、组合根 1、VM 1），并更新 1 项 VM 用例；连续两次全量均 1454/0/4。

## 6. 临时库证据（受理 → 准入 → 消费 → 终态）

`ConversationPersistenceFixture` 使用隔离临时 SQLite（`StoragePathDefaults` + `SqliteTestPools.ClearFor`）。测试内可查询：

- 受理：`conversation_inputs.status=0`、`conversation_input_state.queue_revision` 递增；
- 准入/领取：`status=1`、`claim_id`/`claim_owner_run_id` 非空；
- 消费：`status=2`、`consumed_turn_id`、`message_id` 非空，`messages` 恰一条 user，`turn_id` 独立；
- 终态：`conversation_turns.status` 终结，队列暂停原因由 `conversation_input_state.pause_reason` 持久化。

`A_successful_turn_advances...` 用 `SELECT COUNT(DISTINCT turn_id) FROM messages WHERE role = 1` = 3 证明无旁路、每项独立回合；
`SubmitPromptAsync_accepts_direct_through_the_input_coordinator_without_executing_a_turn` 断言 VM 提交后 runtime 请求为空，证明无空闲 Direct 旁路。

## 7. 设计偏差

- **功能开关语义**：设计 §11 只说「host 控制、默认关闭、关闭保留数据不清空」。实现将其细化为「关闭时拒绝忙碌入队 + 不扫描/不链式调度 backlog，但空闲单条仍经同一受理路径执行」，否则默认关闭会直接破坏 Direct 正常发送。已在 §1.1 记录。
- **`Candidate` 校验顺序**：设计 §5.2 要求执行前重校验；实现先 claim 再校验，失败时把该输入置 Held 并暂停，不越过。语义一致，失败输入状态为 Held 而非 Pending。
- **`ConsumeFollowUpAsync` 递增 revision**：消费同时再 `revision+1`（claim 已 +1），保证「每次修改递增」。
- **功能开关解析**：仅 `SELFCLAW_QUEUE_ENABLED`（`1`/`true`）。未接入桌面设置 UI。
- **删除持久化暂停（新增）**：设计 §6.3 要求“删除失败...保持 queue 暂停”。实现为 `ConversationDeletionService` 在 `BeginDeletion` 后、取消等待前对每个会话持久化 `paused`（重试至多 3 次），删除成功时数据随级联消失。选项前已存在的删除测试（不传 store）行为不变。

未改变设计契约；未新增 schema 版本；未新增第二套 input 状态机。

## 8. 未覆盖门槛（不计通过）

- 真实 provider / Ollama 服务、真实 WPF/WebView2、toast、原生进程恢复 smoke：4 项门控跳过，未运行。
- 桌面手动脚本（设计 §9）未执行。
- Steer 未开放（P4），工具抢占与审批 supersede 未实现。
- 未执行 P4/P5/P6，未清理旧数据。

此前版本的 Q07/Q09/Q10/Q11/Q12、host 桥接与 Desktop 组合根未覆盖项已全部补齐正式测试；当前无遗漏。

## 9. 架构验收 A01/A04/A06

- **A01 唯一 owner**：`ConversationRunCoordinator` 继续是唯一准入/取消/删除 owner；dispatcher 只调用 `TryReserve`/`IsRunning`，无自有 busy 表（源码搜索 `_runs.` in `ConversationInputDispatcher.cs`，无 `IsRunning` 写入）。`ConversationInputDispatcher.TryStartConversationAsync` 是唯一 claim→Engine 入口；VM 不再直接 `TryReserve` Direct。
- **A04 事务一致**：`SqliteConversationTurnWriter.StartAsync` 内 `ConsumeFollowUpAsync` 与回合/user/Consumed 同事务；`SqliteConversationInputRepository.AcceptAsync/Edit/Cancel/Hold/Retry/Claim/Recover` 均在单事务内提交状态 + `queue_revision`；故障注入 `A_start_transaction_failure...`、`A_stale_claim_revision...` 证明无半批/孤立 Consumed。
- **A06 单一执行入口**：Direct 空闲/忙碌/恢复均经 `ConversationInputService.SubmitAsync` → `ConversationInputDispatcher`；VM `SubmitDirectAsync` 只受理，不执行；`SubmitPromptAsync...without_executing_a_turn` 断言 runtime 未被直接调用；CLI 保留 `SubmitCliAsync`（不走输入队列，符合边界）。搜索证据：`MainWindowViewModel.cs` 中 `TryReserve` 仅存于 `SubmitCliAsync`；`_turnEngine.ExecuteAsync` 仅存于 `ExecuteAcceptedTurnAsync`（CLI 使用）。

## 10. 结论

P3 实现与自测完成：.NET 全量 1454/0/4（连续两次），Vue 100，Playwright 15，build 0 警告。Q01–Q16 与 A01/A04/A06 均已逐项有正式测试。Queue 可在设置 `SELFCLAW_QUEUE_ENABLED=1` 的验收环境启用。**Steer 未开放。** 本报告不代表独立审核通过；真实 provider/桌面/进程恢复 smoke 仍为未完成门槛。
