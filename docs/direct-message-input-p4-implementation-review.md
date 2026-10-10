# P4：Steer 执行语义与竞争处理 — 实施与自测报告

状态：实现与自测完成；独立审核发现 IR-P4-01（S06 末尾竞争）与 IR-P4-02（S11 显式 Skill token）已修复并加入回归，**待独立复审**。基线 `17b73a4`（P3 验收修复提交），P4 未改 schema（仍为 v30）、未新增迁移、未开放 Queue/Steer 默认开关、未开始 P5/P6。本报告只记录实施者自测证据，不代表独立审核通过。

> **开关状态更新（2026-10-08）：** 本文所记 Queue／Steer 功能开关已在 `8ad27da` 删除、两者始终开启；下文保持当时原文。权威记录见 [分阶段实施与验收](direct-message-input-implementation.md) 文末「2026-10-08 功能开关移除」。

## 1. 范围与开放条件

- 对应设计：[Direct 消息队列与 Steer 设计](direct-message-input-design.md) §3、§6、§7、§8；任务见[分阶段实施与验收](direct-message-input-implementation.md) §7。
- 交付：Steer 的安全边界消费、工具阶段唯一执行准入、审批 Approved/Denied/Superseded、未执行调用的 SupersededBySteer 闭合、策略受阻回退 FollowUp、stop/失败/Length/预算/崩溃的未消费项 Held。
- 功能开关：Queue 仍为 `SELFCLAW_QUEUE_ENABLED`，Steer 新增 `SELFCLAW_STEER_ENABLED`，两者默认关闭。`DesktopServiceRegistrationTests` 断言默认关闭。
- 宿主关联请求：`send-prompt` 新增可选 `kind=steer` 与 `targetTurnId`；Vue 不发送该字段，完整 UI 属 P5。

## 2. 任务落点

| 任务 | 实现落点 |
| --- | --- |
| P4.1 高水位领取、target 校验、末尾原子关闭 | `IConversationInputSteeringStore` + `SqliteConversationInputRepository.Steering.cs`：`TryClaimPendingSteersAsync` 单事务领取该 turn 的全部 Pending Steer 并推进 queue_revision；`ReleasePendingSteersToFollowUpAsync`（仅 Pending）；`HoldUnconsumedSteersAsync`（Pending+Claimed）；`SettleSteerIntakeAsync` 在终态事务内把未消费 steer 一律 Hold（不再重开为 FollowUp）。`DirectTurnInputSession.ReadFinalBoundaryAsync` 在 `_claimGate` 内“空领取即关闭入口”，`TryAcceptSteerAsync` 用同一 gate 提交受理：关闭前受理的由关闭领取并在本轮消费，关闭后的转 FollowUp。 |
| P4.2 边界协议与预算 | `DirectConversationLoop` 在每个 provider 请求前/工具结果闭合后经 `ReadBoundaryAsync` 消费；`FitsBudget` 复用 `DirectPromptComposer` 估算，超预算调用 `HoldUnconsumedAsync(ContextBudgetExceeded)` 并终止。 |
| P4.3 唯一工具阶段 | `DirectToolInvoker` 独占 Preparing → AwaitingApproval → ReadyToExecute → Executing → Terminal：入口检查 `HasSupersedingSteer`、前置 hook 后审批/准入、`IDirectTurnSteering.TryBeginToolExecutionAsync` 为唯一执行许可线性化点。 |
| P4.4 显式审批结果 | `ToolApprovalDecision`（Approved/Denied/Superseded），`IToolApprovalHandler.RequestApprovalAsync` 返回决策、`TryResolve` 原子关闭；`DesktopToolApprovalHandler` 唯一裁决 pending；Vue/toast 过期点击返回 false。 |
| P4.5 未执行调用闭合 | `DirectToolInvoker.SupersededBySteerResult`：Canceled + `SupersededBySteer`，既不伪造 shell 返回也不计工具故障；已运行前置 hook 时 `toolExecuted` 审计为 canceled/`deniedBy=steer`，未运行 hook 时不伪造配对。 |
| P4.6 情形回退 | `DirectAgentChatRuntime` 在 runStarting 前发布策略：runStarting hooks → `HookRequiresNewTurn`，显式 Skill 激活（`DirectTurnCapabilityLease.ExplicitSkillIds`）→ `SkillRequiresNewTurn`；Service 对 continuation/陈旧 target/无活动 turn 直接转 FollowUp。正文含显式 Skill token（`ISkillTokenDetector`）的 steer 在受理（`AcceptAsync`）和领取（`TryClaimPendingSteersAsync`，覆盖绕过 Service 的直接写入）两处都转 FollowUp `SkillRequiresNewTurn`，绝不进入当前回合。 |
| P4.7 stop/失败/Length/预算/崩溃 | Stop 先 pause → `HoldUnconsumedSteersAsync(TurnCancelled)` → 再取消；引擎在取消/异常路径先 Hold 再终结；终态事务 `SettleSteerIntakeAsync` 对 Succeeded 释放为 FollowUp、其余按 turn 状态 Hold；启动恢复保留既有 StaleSteerTarget 行为。 |
| P4.8 复用与去重 | Steer 使用 P2 的 `DirectTurnInputSession`、`SqliteConversationTurnWriter`（`CommitBoundaryAsync`）与同一回合模型；loop/approval handler 不含第二套执行准入；TurnId 不变。 |

## 3. 职责与删除

- 输入受理：`ConversationInputService`；运行注册/取消所有权不变（`ConversationRunCoordinator` / `ConversationRunHandle`）。
- 工具调度唯一 owner：`DirectToolInvoker`；输入控制端（`DirectTurnInputSession`）只发放一次“可开始”许可。
- 终端关闭唯一 owner：`SqliteConversationTurnRepository.TryFinalizeTurnAsync` 事务内调用 `SettleSteerIntakeAsync`。
- 删除：`IConversationInputRepository.ReadClaimedBatchAsync`（P2 fixture 读取路径）及其实现；Claim 改由输入会话在边界发起。未新增 ACK 表、投递队列或第二套状态机。

## 4. 验收用例证据（S01–S15）

新增 41 项自动用例（初版 36 项 + 复核回归 5 项；同一代码全量通过时执行）：

| ID | 结果 | 用例（类.方法） |
| --- | --- | --- |
| S01 | 通过 | `DirectSteerLoopIntegrationTests.A_steer_during_streaming_is_consumed_after_the_tool_unit_in_the_same_turn`（流未取消、ToolUnit 后加入、TurnId 不变） |
| S02 | 通过 | `DirectSteerLoopIntegrationTests.Only_the_executing_tool_finishes_and_unstarted_calls_become_superseded_results_before_the_steer`；`DirectToolInvokerSteerTests.A_call_already_executing_is_not_revoked_by_a_later_steer_claim` |
| S03 | 通过 | `DirectToolInvokerSteerTests.A_steer_claim_while_approval_waits_revokes_the_pending_approval_and_a_late_click_fails`；`A_failed_steer_claim_does_not_close_the_pending_approval`；`SqliteConversationSteerRepositoryTests.Pending_steers_are_claimed_as_one_ordered_high_water_batch_and_later_inputs_stay_pending` |
| S04 | 通过 | `DirectToolInvokerSteerTests.An_approved_call_still_loses_to_a_steer_claim_at_the_execution_admission`（允许先赢、准入输）；`A_steer_claim_while_approval_waits_...`（claim 先赢）；`A_call_already_executing_...`（Executing 不撤销）；`DesktopToolApprovalHandlerTests.Supersede_wins_exactly_once_and_a_late_click_cannot_execute` |
| S05 | 通过 | `DirectToolInvokerSteerTests.A_steer_claim_during_the_pre_hook_finishes_the_hook_then_skips_approval_and_execution`；`A_steer_claimed_before_the_call_starts_skips_it_without_running_any_hook`（后置审计 canceled/steer，未跑 hook 不伪造） |
| S06 | 通过 | `DirectSteerLoopIntegrationTests.A_steer_accepted_during_the_final_close_check_is_still_consumed_this_turn`（IR-P4-01 回归：空领取后、仍是 Running 时受理的 steer 由原子关闭领取并在本轮消费）；`A_steer_during_streaming_...`（结束前消费）；`ConversationInputSteerServiceTests.A_steer_submitted_after_the_session_close_becomes_a_follow_up`（关闭后转 FollowUp）；`SqliteConversationSteerRepositoryTests.Steer_acceptance_falls_back_to_a_follow_up_when_the_target_is_not_a_live_direct_turn`、`A_successful_close_holds_unconsumed_steers_instead_of_reopening_them`（未消费项 Held，Claimed 不重开） |
| S07 | 通过 | `DirectSteerLoopIntegrationTests.Only_the_executing_tool_...`（两次 boundary、U3 不进入已领取批次、每个输入恰好一次 Consumed）；`Pending_steers_are_claimed_as_one_ordered_high_water_batch_...` |
| S08 | 通过 | `DirectSteerLoopIntegrationTests.A_pending_steer_without_new_tools_extends_the_same_turn_instead_of_ending_it`；`A_steer_during_streaming_...`（唯一终态、唯一 usage、同一 TurnId） |
| S09 | 通过 | `DirectSteerLoopIntegrationTests.A_steer_batch_that_cannot_fit_the_context_budget_is_held_without_an_extra_provider_request`（0 次 provider 请求、Held/context-budget-exceeded、不标 Consumed） |
| S10 | 通过 | `DirectSteerLoopIntegrationTests.A_steer_does_not_reset_the_128_request_budget`（128 请求、127 执行）；`DirectToolInvokerSteerTests.A_superseded_call_neither_resets_nor_increments_the_consecutive_fault_budget` |
| S11 | 通过 | `DirectSteerLoopIntegrationTests.An_explicit_skill_token_steer_is_not_injected_and_waits_as_a_follow_up`（IR-P4-02 回归）；`SqliteConversationSteerRepositoryTests.An_explicit_skill_token_steer_is_converted_to_a_follow_up_at_acceptance`、`An_explicit_skill_token_steer_is_converted_to_a_follow_up_at_claim_time`；`DirectSteerPolicyTests.RunStarting_hooks_block_steering_for_the_whole_turn`、`Explicit_skill_activation_blocks_steering_without_a_run_starting_hook`、`A_plain_turn_publishes_an_allowing_policy`；`ConversationInputSteerServiceTests.A_blocked_steer_policy_accepts_the_input_as_a_follow_up` |
| S12 | 通过 | `ConversationInputSteerServiceTests.A_steer_without_a_matching_live_turn_becomes_a_follow_up`、`A_steer_during_a_continuation_becomes_a_follow_up`、`A_cli_agent_cannot_submit_a_steer`；输入会话/Steering 只发给 Interactive Direct（`DirectSteerPolicyTests.A_non_interactive_turn_cannot_carry_steering`；child/continuation 无 InputSession 不产生边界） |
| S13 | 通过 | `ConversationInputSteerServiceTests.Stop_pauses_and_holds_unconsumed_steers_before_cancelling_the_run`、`A_provider_failure_holds_an_unconsumed_steer_and_pauses_the_queue`；`SqliteConversationSteerRepositoryTests.An_abnormal_close_holds_pending_and_claimed_steers_with_the_mapped_reason`（Cancelled/Failed/Truncated/Blocked/Interrupted） |
| S14 | 通过 | `DirectSteerLoopIntegrationTests.A_steer_during_streaming_...`（reasoning ProtectedData 与 callId 保留、用户文本为 User 角色） |
| S15 | 通过 | `DirectToolInvokerSteerTests.Denied_approval_still_returns_the_user_canceled_result_without_executing`、`A_superseded_call_...`、既有 `DirectToolInvokerTests.InvokeAsync_PropagatesCancellationWithoutInvokingTheTool`（OCE 直抛）与 hook 审计断言 |

竞争顺序由 `TaskCompletionSource` 显式同步（流门、工具门、审批门、claim 后注种）；未使用 `Sleep` 碰运气。S03/S04 的“先 claim 后点击/先点击后 claim”分别由上述用例固定。

## 5. 架构验收

### A01 状态与运行所有权

- 输入受理：`ConversationInputService`；运行注册/唯一准入：`ConversationRunCoordinator`；取消/完成句柄：`ConversationRunHandle`；内容事件流：`DirectAgentChatRuntime` channel + `ConversationTurnRecorder`；工具调度与阶段：`DirectToolInvoker`；Steer 领取/持有：`DirectTurnInputSession` + SQLite 事务。
- `ConversationRunCoordinator` 仍是 `_runs` 唯一注册源；Engine/SessionCoordinator/dispatcher 未引入可写 busy 表。证据：`ConversationRunCoordinatorTests`、`ConversationSessionCoordinatorTests`、`DesktopServiceRegistrationTests`（含 `IConversationInputSteeringStore` 单例）。
- 工具准入不在 loop/approval handler 中重复：`DirectConversationLoop` 只负责边界消费与结果闭合，`DesktopToolApprovalHandler` 只做决策。

### A04 提交职责与事务一致

- 边界消费沿用 `ConversationInputRepository.CommitBoundaryAsync` → `SqliteConversationTurnWriter.TryWriteAsync`，同一事务封存片段、写 user、标 Consumed、推进 usage/queue_revision。
- Steer 收口与回合终态在同一事务：`SqliteConversationTurnRepository.TryFinalizeTurnAsync` 调用 `SettleSteerIntakeAsync`；成功→FollowUp，异常→Held，无半批状态。
- 证据：`SqliteConversationSteerRepositoryTests`（高水位、释放/持有、失败回滚语义）、既有 `SqliteConversationInputRepositoryTests.Boundary_before_commit_fault_rolls_back_...`、`SqliteConversationTurnRepositoryTests`。

### A07 输入提交屏障

- 每回合仍最多一个 boundary waiter（`DirectTurnInputSession`/`RegisterBoundary` 与既有早/晚注册、第二 boundary 拒绝用例）。
- 无 ACK 表、无重试 dispatcher、无回放队列：Steer 只复用 claimId、`conversation_inputs` 状态列与消费映射；`IConversationInputSteeringStore` 仅 3 个事务方法。
- 取消/关闭/失败会结束 waiter 并 Hold 未消费项；`DirectTurnInputSessionTests`（waiter 终结）与 S13 用例共同覆盖。

## 6. 构建与测试

执行环境：Windows；.NET SDK 10.0.400 / 运行时 10.0.11；M.E.AI 10.10.0；临时 SQLite 库；schema v30（无 DDL/迁移变更）。

```powershell
dotnet build SelfClaw.slnx --no-restore          # 0 警告 / 0 错误
dotnet test SelfClaw.Tests/SelfClaw.Tests.csproj --no-build --no-restore
```

- 受影响套件（Direct / ConversationInput / 审批 / 运行协调 / 注册）筛选：修复前 591 通过 / 0 失败；修复后 P4 定向类 45 通过 / 0 失败。
- 全量：修复前连续三次 **1537 通过 / 0 失败 / 4 门控跳过**；复核修复后 **1542 通过 / 0 失败 / 4 门控跳过**（跳过项为 `SELFCLAW_DESKTOP_SMOKE` / `SELFCLAW_PROVIDER_SMOKE` 门控的 WPF/真实 provider smoke）。
- 独立审核探针：S06 末尾竞争与 S11 显式 Skill token 两个场景由上述等价回归固定；真实 provider / WPF 探针仍未运行。
- 未运行：真实 provider、原生 WPF/WebView2、Windows toast、进程恢复 smoke、桌面手动脚本；Vue 无产品代码改动，本轮未跑 Vitest/Playwright。以上均不折算为通过。

## 7. 独立审核问题修复（IR-P4-01 / IR-P4-02）

- **IR-P4-01（S06）**：旧实现把成功收口当作“把残留 steer 释放为 FollowUp”，导致空高水位领取返回后、回合仍 Running 时受理的 steer 既不消费也不 Held。修复：
  - `DirectTurnInputSession.ReadFinalBoundaryAsync` 与 `TryBeginToolExecutionAsync` 共用 `_claimGate`；最终读在一次临界区内“领取到批次或关闭入口”，循环在无工具响应后用该读取代原先的“空即结束”。
  - `ConversationInputService` 的 steer 受理经 `TryAcceptSteerAsync` 在同一 gate 内提交；关闭后的受理直接转 FollowUp（`TargetTurnClosed`）。
  - `SettleSteerIntakeAsync` 不再把 Pending/Claimed 释放为可取消的 FollowUp；未消费项一律 Held（成功关闭原因 `TargetTurnClosed`，异常关闭沿用 turn 状态原因）。
- **IR-P4-02（S11）**：旧实现只看回合 lease 的 `ExplicitSkillIds`，不检查 steer 正文，`[/pdf]` 被当作本轮 User 消息消费。修复：
  - `ISkillTokenDetector`（Core）+ `SkillTokenParser` 实现；Repository 在 `AcceptAsync` 与 `TryClaimPendingSteersAsync` 两处检测，命中即转 FollowUp `SkillRequiresNewTurn`，覆盖经 Service 与直接写库的受理路径。

## 8. 设计偏差与遗留

- `send-prompt` 增加 `kind`/`targetTurnId` 作为 P4 的关联请求验证入口；Vue 完整交互（编辑/提升/Held 重试/反馈）仍属 P5，`conversation-input/steer|promote` 桥接操作仍显式拒绝。
- Held Steer 的 Retry/转 FollowUp 语义属 P5；P4 只保证 Held 可见、队列暂停、不自动重跑。
- 真实 provider 的 reasoning/工具/steer 后续请求未验证，列为 P6 门槛；离线 fixture 不冒充真实协议验证。
- 结论：P4 自测完成，Queue/Steer 默认开关保持关闭，待独立审核。
