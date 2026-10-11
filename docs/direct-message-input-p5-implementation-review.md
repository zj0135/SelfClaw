# P5：完整 Vue 交互和传输恢复 — 实施与自测报告

状态：实现与自测完成，独立审核发现 IR-P5-01（取消冲突吞掉后续「已加入上下文」）已修复并加入回归。基线 `8d72122`（P4 交付提交），P5 未改 schema（仍为 v30）、未新增迁移；Queue `SELFCLAW_QUEUE_ENABLED` 与 Steer `SELFCLAW_STEER_ENABLED` 默认关闭。本报告只记录实施者自测证据；真实桌面 WebView2、真实 provider、toast 与进程恢复留给 P6，均不计为通过。P4 的后端仍**待独立复审**，本报告不改变该结论。独立审核原文见 [P5 独立审核](direct-message-input-p5-independent-review.md)。

> **开关状态更新（2026-10-08）：** 本文所记 Queue／Steer 功能开关已在 `8ad27da` 删除、两者始终开启；下文保持当时原文。权威记录见 [分阶段实施与验收](direct-message-input-implementation.md) 文末「2026-10-08 功能开关移除」。

## 1. 范围与开放条件

- 对应设计：[Direct 消息队列与 Steer 设计](direct-message-input-design.md) §3、§10；任务见[分阶段实施与验收](direct-message-input-implementation.md) §8。
- 交付：编辑／取消／提升 Steer／Held 重试／暂停／恢复／受限 detail；宿主发布 `canSteer` + reason；订阅与重载恢复；ACK 后清稿、超时重试复用 `clientRequestId`；模式退回、等待生效、已加入上下文等反馈；ChatView 只做布局与接线。
- 前置：P4 的 Steer 领取／消费／工具抢占／审批 supersede 链路未改动；P5 只补充“把队列项原子提升为 steer”的存储与桥接操作，消费仍由 P4 的边界协议完成。
- 功能开关：Queue 与 Steer 均默认关闭。关闭时 `promote` 返回 `steer-not-enabled`，空闲发送仍经同一受理链。

## 2. 任务落点

| 任务 | 实现落点 |
| --- | --- |
| P5.1 独立 composable／组件 | `useConversationInputs` 持有状态与关联请求，`ConversationQueue.vue` 只渲染；`ChatView.vue` 只接线。 |
| P5.2 编辑／取消／提升／重试／暂停／恢复／detail | 桥接新增 `conversation-input/promote`；`ConversationInputQueueService.PromoteAsync` 在 `DirectTurnInputSession` 的同一 `_claimGate` 下调用新存储方法 `PromoteAsync`，原子改写原行（保留 inputId/sequence），不复制输入。 |
| P5.3 宿主发布 canSteer | `ConversationInputSteerPolicy` 成为唯一判定；`ConversationInputPublisher` 在每次推送时按当前活动句柄计算 `canSteer / steerReason / steerTargetTurnId`，并订阅 `ConversationRunCoordinator.Changed` 在回合开始／结束时重新推送。提交与 promote 在宿主侧复核同一策略。 |
| P5.4 订阅身份与重载 | `subscribe` 使用 `subscriptionId + conversationId`；`get-state` 拉完整快照；`queueRevision` 单调；旧订阅、旧 revision、其他会话的推送与结果被丢弃。 |
| P5.5 ACK 与草稿 | `send-prompt` ACK 到达后才 `accept()` 清稿；超时重试复用原 `clientRequestId`、`conversationId` 与 steer 路由；忙碌入队不恢复 transcript 跟随。 |
| P5.6 反馈 | 预览、满额／拒绝用宿主文案，编辑冲突保留正文并刷新 revision，steer 退回 FollowUp 显示宿主 reason，steer 列表显示“等待生效”，被消费后显示“已加入上下文”；图标只用 `lucide-vue-next`，样式 scoped。 |

## 3. 职责与边界

- 权限唯一来源：`ConversationInputSteerPolicy.Evaluate`（受理回退、promote 校验、状态发布三处共用）。Vue 只用宿主下发的 `canSteer`／`steerReason` 控制可用性与文案，不读取运行标志自行判断。
- 提升只允许 `Pending FollowUp`：`Held` 需要先 retry 重新校验冻结定义（模型、Agent、Workspace Root），不静默提升。`Claimed/Consumed/Cancelled` 返回 Conflict，界面隐藏对应操作。
- 提升与边界关闭共用一个 gate：intake 已关闭时 promote 返回 `target-turn-closed`，原项保持 FollowUp；不会绑到后来启动的另一个回合。
- 显式 Skill token 的 promote 在存储层按与受理／领取相同的规则拒绝（`SkillRequiresNewTurn`），避免提升后才在领取时被转回。
- 未新增 ACK 表、第二套队列状态机或前端调度副本；`conversation-input/steer` 旧操作名继续显式拒绝，正式提交仍走 `send-prompt kind=steer`。

## 4. 验收用例证据（U01–U10）

新增／更新用例：.NET 7 项、Vitest 4 文件（新增 20 项）、Playwright 8 项。全部在本文记录的命令中通过。

| ID | Vitest | Playwright / .NET |
| --- | --- | --- |
| U01 | `chatComposer.test.js`：`sends a steer bound to the host turn and clears the draft only after the ACK`；`accepting a queued draft preserves the transcript reading position` | `queue.spec.js`：`queues while busy, preserves the draft until ACK, and keeps list after pause/resume results` |
| U02 | `conversationQueueUi.test.js`：`offers edit, promote and cancel for a pending follow-up only when the host allows steering`、`disables steering with the host reason and renders the fallback notice`；`conversationInputs.test.js`：`promotes with the host-published target and refuses locally when the host says steer is unavailable` | `queue.spec.js`：`enables adjustment only after the host publishes canSteer and reports consumed steers`；.NET `Promote_publishes_the_host_verdict_and_keeps_the_input_identity` |
| U03 | `conversationInputs.test.js`：`keeps the edited body on a conflict, refreshes the list and adopts the new revision` | `queue.spec.js`：`keeps the edited body on a conflict and saves again with the refreshed revision`、`promotes a pending follow-up without duplicating the input`；.NET `Promote_converts_the_same_pending_row_and_rejects_closed_or_skill_targets`、`Promote_rejects_a_stale_revision_or_a_target_that_is_not_the_live_turn` |
| U04 | `conversationInputs.test.js`：`ignores an old conversation result after switching subscriptions`；`drops state pushed for a conversation that is no longer selected`；`chatComposer.test.js`：`does not clear another conversation draft or change its scroll when an old ACK arrives` | `queue.spec.js`：`an old conversation ACK cannot clear the new conversation draft` |
| U05 | `conversationInputs.test.js`：`rejects a push from another conversation or an older revision`、`restores state on reload by resubscribing to the selected conversation` | `queue.spec.js`：`reload restores the accepted queue through a new GUID subscription` |
| U06 | `chatComposer.test.js`：`keeps the full draft and the retry identity when the host rejects for capacity`、`reuses the steer identity and route after a timeout and never reuses it for a follow-up` | 既有 .NET Q13 容量／64 KiB 边界用例（未改动） |
| U07 | `chatComposer.test.js`：`accepting a queued draft preserves the transcript reading position`；`conversationInputs.test.js`：`never forces the transcript reading position when queue state changes around a queued ACK`（队列推送与 ACK 交错也不调用 follow） | 既有 transcript 跟随偏好用例（未改动） |
| U08 | `conversationQueueUi.test.js`：`offers separate queue, steer and stop actions while a Direct turn is busy`（审批条与 steer 并存） | `queue.spec.js`：`keeps the approval bar independent from steering and drops it after supersede`；.NET P4 `DesktopToolApprovalHandlerTests.Supersede_wins_exactly_once_and_a_late_click_cannot_execute` |
| U09 | `conversationQueueUi.test.js`：`shows Held items as blocking with their host reason and a retry path` | `queue.spec.js`：`held items stay blocking after resume and retry reuses the same input`；.NET `Held_head_must_block_following_pending_after_resume` |
| U10 | `conversationQueueUi.test.js`：`never renders queue or steer actions for a busy CLI turn` | `queue.spec.js`：`a busy CLI turn never offers queue or adjustment actions`；.NET `RouteAsync_drops_conversation_input_actions_from_a_plugin_view_origin`；child Activity 无输入面板沿用既有用例 |

补充的宿主管线用例：

- `ConversationInputSteerServiceTests.Promote_converts_a_pending_follow_up_into_a_steer_for_the_live_turn`（Applied、同 inputId/sequence、只一行、pulse 唤醒）。
- `Promote_respects_the_published_policy_and_the_explicit_skill_rule`（hook 策略拒绝、`[/pdf]` 保持 FollowUp）。
- `Promote_that_loses_the_close_race_stays_a_follow_up_and_is_rejected_when_disabled`（最终边界关闭后拒绝；开关关闭抛 `steer-not-enabled`）。
- `ConversationInputHostTests.Steer_and_promote_are_rejected_while_the_switch_is_off`（canSteer=false、无 steerReason 泄露、两个操作都拒绝）。
- `SqliteConversationSteerRepositoryTests.Promote_converts_the_same_pending_row_and_rejects_closed_or_skill_targets`（同一事务行内改写、stale revision 冲突、Skill／已关闭目标无效）。

## 5. 传输恢复细节

- `reduceQueueState` 只接受 `conversation-input/state` 完整快照；局部操作结果永不清空列表。订阅身份不匹配、会话不匹配、revision 回退均丢弃。
- 切换会话时 composable 递增 generation；旧请求的 ACK/错误/冲突不会写入新会话的 error、编辑器或列表；`subscribe` 重置编辑态与提示。
- 编辑冲突：保留用户正文，`get-state` 刷新列表并采用最新 item revision，用户可直接再次保存；成功保存提示“已更新待发送内容”。
- steer 消费检测：仅当宿主推送中某条 `kind=steer` 从列表消失且不是本地 cancel 时提示“已加入上下文”；正在执行（Claimed）的项已不再出现在队列卡片中，Held 仍显示阻塞原因。

## 6. 构建与测试

执行环境：Windows；.NET SDK 10.0.400 / 运行时 10.0.11；Node/npm 与仓库既有依赖；Playwright 使用 msedge。

```powershell
dotnet build SelfClaw.slnx --no-restore
dotnet test SelfClaw.Tests/SelfClaw.Tests.csproj --no-build --no-restore
npm test
npm run build
npm run test:e2e
```

| 执行 | 结果 |
| --- | --- |
| .NET build | 0 警告 / 0 错误 |
| .NET 定向（ConversationInput / Steer / Router） | 185 通过 / 0 失败 |
| .NET 全量（`.scratch/p5/p5-full.trx`） | **1549 通过 / 0 失败 / 4 门控跳过**（基线 1542，新增 7 项） |
| Vitest | **126 通过 / 0 失败**（25 文件；基线 106，新增 2 个文件、20 项） |
| Vite build | 成功 |
| Playwright | **25 通过 / 0 失败**（queue 10 + activity 15；基线 25 中 queue 2，本轮新增 8 项） |

未运行：真实 provider、真实 WPF/WebView2 核查、Windows toast、进程恢复 smoke、桌面手动脚本；4 项门控 smoke 跳过。以上均不折算为通过；桌面真实 WebView2 属 P6。

## 7. 独立审核问题修复（IR-P5-01）

独立审核在基线工作区用生产 composable 探针发现：`cancel` 在请求发出前就把 inputId 放进抑制名单，取消返回 Conflict 后未撤销；steer 后来被宿主消费并从快照消失时，「已加入上下文」被误判为本地取消而吞掉。

修复：抑制名单只在取消成功时保留，任何非 `ok` 结果（冲突、超时、异常）立即撤销；本地成功取消仍不提示。新增回归 `conversationInputs.test.js`：`does not announce a locally cancelled steer as consumed`、`still reports consumption after a cancelled steer loses the revision conflict`。

复核：原样运行审核方的生产 composable 探针（`.scratch/direct-message-input/p5-ind-review/run-probes.mjs`）**22 通过 / 0 失败**，含原失败项 `failed cancel still announces a later consumed steer`；完整 Vitest 126 通过、Playwright 25 通过。独立审核文件保持原文，不改为“审核通过”。

## 8. 设计偏差与遗留

- `ConversationInputQueueState` 增加 `SteerReason`、`SteerTargetTurnId`；`conversation-input/state` 快照相应增加 `steerReason`、`steerTargetTurnId`，用于让 UI 把 promote 绑定到宿主当前活动回合。设计 §10 的字段集合是下限，未改变语义。
- Promote 仅接受 `Pending FollowUp`；`Held` 必须先 retry 重新校验。这是对 §3.1“编辑／删除只允许尚未领取的项”的收紧，原因是避免提升跳过定义／模型／Root 校验。
- 编辑冲突时宿主在 `ConversationInputUpdateResult` 无 reason 的 Conflict 上补 `queue-revision-conflict`，让界面展示统一文案。
- P4 仍待独立复审；本阶段只验证了到“durable steer + pulse + 边界领取入口”为止的宿主行为，真实 provider 回合内的 steer 消费不在 P5 范围。

## 9. 结论

P5 六项任务完成，U01–U10 有 Vitest／Playwright／.NET 证据，IR-P5-01 已修复，Vue build 通过；Queue 与 Steer 开关保持默认关闭，候选版本进入 P6 桌面验收。**不得据此声明 Steer 已发布或桌面行为已验证。**
