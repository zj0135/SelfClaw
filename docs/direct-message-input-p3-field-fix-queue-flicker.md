# Direct Queue：取消回合后队列闪烁与后续输入不生效（现场缺陷修复）

日期：2026-10-03。基线：`17243f9` 上的未提交 P3 工作区（含独立审核 IR-P3-01–11 修复）。本记录为修复与自测，不代表新的独立复审；Queue 仍默认关闭，Steer 未开放，P4–P6 未开始。

> **开关状态更新（2026-10-08）：** 本文所记 Queue／Steer 功能开关已在 `8ad27da` 删除、两者始终开启；下文保持当时原文。权威记录见 [分阶段实施与验收](direct-message-input-implementation.md) 文末「2026-10-08 功能开关移除」。

## 1. 现场现象

真实使用（Queue 开关启用）复现：

1. 会话中 A 执行中，依次发送 B、C（入队）。
2. A 执行过程中点击“停止生成”。
3. 渲染区开始持续闪烁。
4. 之后再次输入无效果。

## 2. 根因

`ConversationInputDispatcher.ScanOnceAsync` 每 250 ms 运行一次，候选来自
`ListConversationIdsWithUnprocessedInputsAsync`：

```sql
SELECT DISTINCT conversation_id FROM conversation_inputs WHERE status IN (0, 1, 3);
```

该查询把**暂停队列、Held 头、Claimed 头**的会话也当作候选。对一个“已暂停且有 Pending 项”的会话：

- `TryStartConversationAsync` 的 `IsRunning` 为 false，于是 `TryReserve` **成功**（发布一次 `ConversationRunChange`）；
- `TryClaimNextFollowUpAsync` 因 paused 返回 null，随后 `finally _runs.Complete(handle,false)`（再发布一次）；
- `ConversationSessionCoordinator.OnRunChanged` 对所选会话的每次 run change 都
  `_transcriptChangeSink.PublishNow(true)` 并触发 `SelectedStateChanged`。

于是每 250 ms 出现 2 次全量 transcript 重新发布 + VM `OnPropertyChanged(string.Empty)`，形成持续闪烁。
同一暂停状态下新输入会被受理并排队，但不会被调度（扫描只做无意义的 reserve/release），表现为“再次输入无效”。

## 3. 修复

### 3.1 候选只返回真正可领取的会话

`IConversationInputSchedulerStore.ListConversationIdsWithUnprocessedInputsAsync`
重命名为 `ListDispatchableConversationIdsAsync`，并收窄语义：

```sql
SELECT DISTINCT i.conversation_id
FROM conversation_inputs i
JOIN conversations c ON c.id = i.conversation_id AND c.kind = 0
LEFT JOIN conversation_input_state s ON s.conversation_id = i.conversation_id
WHERE i.status = 0 AND i.kind = 0
    AND COALESCE(s.paused, 0) = 0
    AND NOT EXISTS (SELECT 1 FROM conversation_inputs earlier
        WHERE earlier.conversation_id = i.conversation_id
            AND earlier.status IN (0, 1, 3)
            AND earlier.sequence < i.sequence);
```

即“未暂停 且 最早未处理项是 Pending FollowUp”。暂停队列与 Held/Claimed 头不再进入候选，扫描不再为无法启动的工作反复领取运行句柄。

### 3.2 领取前先判定可调度

`ConversationInputDispatcher.TryStartConversationAsync` 在 `TryReserve` **之前**读取一次队列状态并判定：
暂停、无项、FIFO 头非 Pending FollowUp 时直接返回 `Blocked`，不占用运行句柄。
关闭开关时仍只允许“刚受理的唯一空闲输入”（`immediateInputId`）通过。

这样即使用户在暂停状态下继续输入，也只会在队列尾排队，不会产生一次 reserve/release 闪烁。

`ScanOnceAsync` 由 `private` 改为 `internal`，便于确定性测试直接调用（不影响既有反射用例）。

## 3.3 队列 UI 布局错乱

现象：队列卡片横跨整个窗口宽度，且把 composer 推离底部、下方留下大片空白。

根因：`ChatView.vue` 的 `.workspace` 是 3 行 grid（`minmax(0,1fr) auto 0`），原本 3 个子项（ActivityStage / ComposerPanel / TerminalPanel）。P3 把 `<ConversationQueue>` 直接插成第 4 个 grid 子项，导致行映射错位（ComposerPanel 落到 0 高行，TerminalPanel 进入隐式行），同时队列是整宽而 composer 是居中固定宽，两者不对齐。

修复：`ComposerPanel.vue` 在 `.composer-stack` 内新增 `<slot name="queue" />`（位于 composer 卡片上方），`ChatView.vue` 通过该插槽渲染队列。这样 grid 子项恢复为 3 个，队列自动继承 composer 的居中宽度（`min(calc(100% - 72px), 728px)`）；`ConversationQueue.vue` 改为与 composer 一致的卡片样式（圆角、边框、8px 间距），不再是整宽色带。

验证：Playwright `queue.spec.js` 两个用例通过；临时用 fixture host 渲染 3 条队列项截图确认与 composer 对齐、无空白错位（截图见 `.scratch/direct-message-input/p3-flicker/queue-layout.png`，临时 spec 已删除）。

## 4. 验收覆盖

新增 5 个正式测试（`ConversationInputDispatcherTests`）：

| 测试 | 断言 |
| --- | --- |
| `Stopping_a_running_turn_then_resuming_delivers_queued_items_in_order_without_scan_churn` | 复现完整现场：A 运行中被停止 → 队列暂停且 B/C 保留 → 3 次扫描 0 次 run change（不闪烁）→ 恢复后按 A、B、C 执行 |
| `A_paused_queue_is_not_a_scan_candidate_and_never_churns_admission` | 暂停前是候选、暂停后不是；4 次扫描 0 个 run change |
| `A_held_fifo_head_conversation_is_not_a_scan_candidate` | Held 头阻挡后仍不是候选；扫描 0 个 run change |
| `A_paused_conversation_accepts_a_later_input_but_does_not_reserve_a_run` | 暂停时新输入入队、`Blocked`、0 个 run change |
| `Resuming_a_paused_queue_runs_its_pending_items_again` | 恢复后重新调度并执行积压项 |

### 反向验证

临时还原（候选查询恢复为 `status IN (0,1,3)` 且移除领取前判定）后，上面前三项均失败：
候选包含暂停会话、扫描产生 run change。确认测试确实捕获该缺陷，随后恢复修复。

## 5. 命令与计数

隔离输出 `bin/p3fix2/`：

| 执行 | 通过 | 失败 | 跳过 |
| --- | ---: | ---: | ---: |
| build | — | 0 | — |
| .NET 全量 `p3-flicker-1.trx` | 1495 | 0 | 4 |
| .NET 全量 `p3-flicker-2.trx`（连续第二次） | 1495 | 0 | 4 |
| Vue `npm test`（连续 4 次） | 106 | 0 | 0 |
| Vue `npm run build` | 成功 | 0 | — |
| Playwright | 17 | 0 | 0 |

（首次并行运行 Vue 与 .NET 全量时有 2 项 Vitest 超时，单独连续 4 次均为 106/0；该并行竞争与本次改动无关，未改动任何 Vue 代码。）

## 6. 边界与遗留

- 仍为 schema v30，无 DDL；未新增状态机；`ConversationRunCoordinator` 仍是唯一运行所有者。
- 停止后队列保持暂停、需用户“恢复”是设计 §3.1/§6.3 的既定行为；本次修复让该状态不再闪烁，恢复按钮可正常使用。
- 未运行真实 provider、真实 WPF/WebView2、toast、原生进程恢复 smoke；这些仍为未验证门槛。
- 未经用户指示未提交、推送或清理数据。
