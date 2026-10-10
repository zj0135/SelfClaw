# Direct 消息输入：P5 独立审核

日期：2026-10-04。基线 `8d72122eb72a8d7b6c85418142cd6ac71829c46a`。审核对象是该 HEAD 上的 P5 工作区，包括未跟踪的队列界面与宿主桥接。没有修改生产代码，没有启动用户 Desktop，没有提交或推送。

> **开关状态更新（2026-10-08）：** 本文所记 Queue／Steer 功能开关已在 `8ad27da` 删除、两者始终开启；下文保持当时原文。权威记录见 [分阶段实施与验收](direct-message-input-implementation.md) 文末「2026-10-08 功能开关移除」。

**结论：U01–U10 的主路径成立，P5 不能记为无缺陷。** 取消冲突会吞掉后来的「已加入上下文」（IR-P5-01）。这不破坏草稿、投递目标、已领取行或会话隔离，但 P5.6 的消费反馈还没完成。Queue `SELFCLAW_QUEUE_ENABLED` 与 Steer `SELFCLAW_STEER_ENABLED` 仍默认关闭。本轮没有真实 WebView2、真实 provider、toast 或进程恢复，不能据此宣布 Steer 已发布。

## 1. 方法与执行结果

已读设计 §3、§10 和实施文档 §8，并直接读 `useConversationInputs`、`useChatComposer`、`ConversationQueue.vue`、`ComposerPanel.vue`、`ChatView.vue`、`ConversationInputBridge`、`ConversationInputPublisher`、`ConversationInputSteerPolicy`、promote 存储和官方测试。探针导入生产 composable 与生产 `createHostBridge`，不复制一份 reducer。浏览器用例在 Edge 里点击、提交、滚轮、切换会话和重载。

Vue build 写到 `.scratch/direct-message-input/p5-ind-review/vue-dist/`，没有覆盖 Desktop 的运行资源。正式命令都正常退出，没有把挂起的收尾算成通过。

| 执行 | 结果 |
| --- | --- |
| `npm test`（Vitest） | 124 通过 / 0 失败，25 个文件 |
| `npm run build -- --outDir ../.scratch/.../vue-dist` | 成功，2.62 秒 |
| `npm run test:e2e`（官方 Playwright，msedge） | 25 通过 / 0 失败，23.6 秒后进程退出 |
| 生产 composable 探针 | 21 通过 / 1 失败（IR-P5-01） |
| 独立 Playwright，7 个点击用例 | 7 通过 / 0 失败，9.8 秒后进程退出 |

独立探针与浏览器用例在 `.scratch/direct-message-input/p5-ind-review/`。本轮没有重跑 .NET 全量；promote 不复制行、Held 阻挡和暂停后的自动启动按当前源码核对，不把实施者自述的 1549 项折算成这次审核的运行证据。

## 2. IR-P5-01：取消冲突之后不再提示「已加入上下文」

`cancel` 在请求发出之前就把 inputId 放进 `settledLocally`。冲突返回后没有撤销。steer 后来从快照里消失时，`announceConsumedSteers` 把这次消失当成“用户自己取消的”，跳过提示。

```164:167:SelfClaw.TranscriptVue/src/composables/useConversationInputs.js
	const cancel = (inputId, revision) => {
		settledLocally.add(inputId);
		return run('cancel', { inputId, expectedRevision: revision });
	};
```

```85:91:SelfClaw.TranscriptVue/src/composables/useConversationInputs.js
	function announceConsumedSteers(previous, next) {
		const remaining = new Set(next.map(item => item.inputId));
		for (const item of previous) {
			if (item.kind !== 'steer' || remaining.has(item.inputId)) continue;
			if (settledLocally.delete(item.inputId)) continue;
			showNotice('已加入上下文');
		}
	}
```

生产 composable 探针先让取消返回 `queue-revision-conflict`，刷新后该项仍在，再推一份不含该项的快照。界面保留错误「队列已变化，请根据最新列表重试」，`notice` 仍是空字符串。成功取消时抑制提示是对的；失败取消不该留下这张永久免提示名单。官方 Playwright 只覆盖了没有先失败的消费，所以套件是绿的。

这不把提升报成成功，也不改写已领取行。

## 3. U01–U10

| ID | 结论 | 这次实际看到的 |
| --- | --- | --- |
| U01 | 通过 | 忙碌 Direct 入队只发 `send-prompt`，不发 `stop-generation`。ACK 前草稿还在，ACK 后才清。浏览器里原回答「Original response still running.」仍在。 |
| U02 | 通过，见 IR-P5-01 的提示缺口 | `canSteer` 为假时，调整按钮和队列里的提升按钮都还在，但是禁用。标题是宿主原因码 `steer-not-enabled` 映射出的「当前回合未开放调整」。强制点击不发 steer、不发 promote、不出现「已加入上下文」。`promote()` 在宿主判定为假时直接返回，不把操作报成成功。 |
| U03 | 通过 | 编辑冲突保留用户刚打的正文，并把 revision 换成刷新后的 9。请求仍用原来的 inputId 和旧 revision。已领取行的 promote 被拒后列表仍是那一行，状态仍是 claimed。存储层 `PromoteAsync` 只 `UPDATE` 同一条 Pending FollowUp，不插入第二行；Claimed 不在 `status = 0 AND kind = 0` 里。 |
| U04 | 通过 | 切到 B 之后，A 的 ACK 不清 B 的草稿、不调用 transcript follow。A 的 state 和暂停错误也不写入 B。浏览器里 B 的草稿「draft owned by B」和 B 的转录都还在。 |
| U05 | 通过，同号快照另有残留 | 更小的 `queueRevision` 被丢掉。重载后的新 GUID 订阅恢复 Pending 与 Held；浏览器点击了重载，并确认 revision 0 的空快照没有清掉这两项。暂停标志由 composable 对带 `paused: true` 的快照恢复；浏览器用的 fixture 不把暂停写进 localStorage，所以重载后的暂停按钮没有在浏览器里再证一次。 |
| U06 | 通过 | 超时后的第二次 steer 复用同一个 `clientRequestId`、`kind` 和 `targetTurnId`。改成 follow-up 会换新的 id。满额和超长都走拒绝，不调用 `accept()`。宿主对同一 `(conversation_id, client_request_id)` 有唯一约束，重复受理返回已有行而不是再插一行。 |
| U07 | 通过 | 忙碌入队不调用 `resumeFollow()`。空闲发送的 ACK 仍会跟随。浏览器里用滚轮把转录停在顶部后再入队，滚动位置留在顶部，没有被拉到底。 |
| U08 | 沿用官方用例 | 官方 `queue.spec.js` 在审批条可见时提交 steer，审批条仍在；宿主把审批快照改成空之后，允许按钮消失。`useChatApprovals` 丢掉更旧的 approval revision。本轮没有另做一轮审批点击。 |
| U09 | 通过（空队列例外符合设计 §3.1） | 浏览器在暂停后继续入队，两项都保持 pending，不发停止。调度源码里，暂停且新项不是队首时 `IsDispatchable` 返回 false，不会启动。队首若是 Held（status 3），领取语句的 `NOT EXISTS earlier.status IN (0, 1, 3)` 会挡住后面的 Pending。暂停且队列已空时，下一次空闲发送仍会启动并在领取时解除暂停，这是设计 §3.1 的空队列例外，不是绕过 Held。 |
| U10 | 通过 | 忙碌 CLI 没有加入队列、调整或队列面板，停止按钮仍在。父转录不含子活动夹具里的 “Inspect streaming output”。插件 origin 在 `WebViewMessageRouter.RouteAsync` 读 `type` 之前就被 `IsApplicationOrigin` 丢掉；本轮没有再从插件 iframe 发一条真实消息。 |

同 revision 会整包覆盖，探针里一份 revision 相同但 `canSteer: true` 的快照盖掉了原来的 false。宿主在 dispatcher 上发送前重算 `canSteer`，并且不会在已经发布更高 revision 之后再发出更低的。剩下的窗口是同号快照乱序。WebView 单通道按发送顺序投递，这次没有把它记成 U05 失败。

## 4. 和设计对齐的行为

- Direct 忙碌时三个按钮分开：加入队列、调整当前任务、停止。调整是否可点只看宿主的 `canSteer`，不看本地运行标志。
- 宿主 `followup` / `held` 字符串会被收成界面用的 `follow-up` / `held`。订阅 id 是 GUID，桥接能解析。
- 提升成功后界面显示「等待生效」。只有 steer 从列表消失、且不是本地成功取消时，才显示「已加入上下文」。P4 消费没发生时，界面不会把提升说成已经加入上下文。
- `ChatView` 只把队列状态和提交目标接进去。队列组件使用 Lucide，样式是 scoped。

`canSteer` 为假时按钮仍然显示，只是禁用，原因在 `title` 里。这符合设计 §10 的「展示可用性」，不是再提供一次可用的 Steer。

## 5. 没有验证的部分

- 真实 WPF / WebView2、真实 provider、Windows toast、进程恢复。
- 开关关闭时，忙碌 Direct 仍会画出「加入队列」；宿主会拒绝并留下草稿。fixture 总是接受，所以这条拒绝没有在浏览器里点出来。
- .NET 全量没有在这次审核里重跑。

## 6. 状态

P5 可以带着 IR-P5-01 进入 P6 的桌面验收准备。消费提示在取消冲突后的行为还要修。Steer 继续关闭。
