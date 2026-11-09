# 会话自动标题总结设计

> 适用范围：Direct 交互会话的标题。第一条用户消息 durable 落库后，用当前会话的模型生成总结标题并实时更新前端；数据库 schema 仍 v32，无迁移、无新 Wire 协议。

## 1. 结论

1. **触发语义**：第一条用户消息落库（`ConversationTurnEngine.StartTurnAsync` 成功）后立即并行生成，不等回合结束；同一会话只触发一次，后续消息不重跑，失败也不重试。
2. **范围**：仅 `AgentExecutionMode.Direct` 的交互回合。CLI（认证与模型配置在子进程里，宿主无法用「CLI 的模型」调 API）、子代理、continuation、自动化各自走别的引擎/kind，全部不参与。
3. **模型**：composer 随首条消息捕获的 `ModelProfileId`（null 由 `AiChatClientFactory.PrepareAsync(null)` 解析为 desktop-default）。
4. **不传思考等级，但输出预算要够思考**：标题调用只给一个只含 `MaxOutputTokens = 1024` 的全新 `ChatOptions`，不重建模型档案的采样与 reasoning 配置，走 provider 默认行为——与 `DirectContextCompressor` 同一纪律（该调用同样用流式取响应，绕开 OpenAI 兼容端「非流式 200 但 choices 为空」的坑）。推理模型把**思考与正文算在同一份输出预算里**（`cn:deepseek-v4.1-flash` 在第一条用户消息上要先花 100–800 个输出 token 才写出第一个标题字符）；预算给到 256 时，重一点的思考会把预算吃光，provider 回 `finish_reason=length` 且**正文一个字都没有**，调用方只能理解为「没有标题」——2026-11-09 那次「标题没总结」正是这条路径。1024 同时也落在 20 秒生成超时之内（按该 provider 的吞吐约 15 秒上限）。
5. **写库是 CAS**：写前重读会话；标题仍等于 kick-off 时的截断标题才写。用户已手动重命名 → 放弃；会话已删除 → 放弃（不复活）。
6. **不动 `updated_at_utc`**：标题变化不是会话活动，侧栏排序与「归档」判定读的是真实最后对话时间（与手动重命名先例 `RenameConversationAsync` 相同）。
7. **失败静默，但日志必须留痕**：超时（20 秒）、provider 失败、模型被停用、空响应 → 保留截断标题；回合永不受影响，异常绝不外抛。静默指界面无提示，**不是日志无声**：空响应用 warning 带上 `finish_reason`、实际输出 token 与预算（否则「思考被截断」和「模型什么都没答」在日志里长得一模一样），生成超时用 warning 指名超时（关闭时的取消不算故障、不记）；只有「会话已删除 / 已被手动改名所以不写」这类预期跳过用 debug。2026-11-09 的排查就卡在这里：一次彻底失败的标题生成在日志里一条记录都没有。

## 2. 链路

| 位置 | 职责 |
|---|---|
| `ConversationTurnEngine.ExecuteAsync` | `PrepareRuntimeStateAsync` 之后、并入本条消息之前捕获 `state.Messages.Count == 0`（StartTurn 提交物只含本条新消息，不能用作判据）；`Direct` 模式下 `IConversationTitleStarter.TryStart(...)` fire-and-forget |
| `SelfClaw.Core/Interfaces/Conversations/IConversationTitleGenerator.cs` | 领域契约：`string? GenerateTitleAsync(prompt, modelProfileId, ct)` |
| `SelfClaw.Infrastructure/Agents/Conversations/ConversationTitleGenerator.cs` | 一次性模型调用：System 指令（同语言、≤48 字符、单行、无引号）+ 首条消息（截断 4 000 字符）→ 流式文本 → sanitize（压平换行 / 去包裹引号 / 折叠空白 / 截 48） |
| `SelfClaw.Desktop/Services/Runtime/ConversationTitleService.cs` | 后台任务登记与关闭取消、超时、CAS 写库、`TitleChanged` 事件；`IAsyncDisposable` 由 host dispose 收尾 |
| `MainWindowViewModel` | 订阅 `TitleChanged` → `RunOnUi` + 重载会话列表 → `PublishShell`；新标题随 transcript payload 的 `conversations` 到 Vue |
| `useConversationNavigation.js`（既有） | 已在消费 `payload.conversations`，侧栏标题即时刷新，无需前端改动 |

## 3. 陷阱

1. **不能用 StartTurn 的提交物判定「第一次」**：`SqliteConversationTurnWriter.StartAsync` 只返回本条用户消息；判定必须看 `PrepareRuntimeStateAsync` 装载的历史是否为空，且要在消息并入状态之前取。
2. **`UpdatedAtUtc` 必须原样带过**：写标题用 `current with { Title = … }`，`current` 是写前重读的库内记录；改时间戳会把旧会话从「归档」里拽出来或把排序打乱。
3. 生成标题 ≤48 字符与转录截断（`ConversationTitles.MaximumLength`）对齐；手动重命名上限仍是 120 字符，两者互不干扰。
4. 残余竞态只有毫秒级「重读之后、写入之前会话被删」窗口，与既有重命名/通知路径同等级，不额外加锁。
5. 关闭顺序：服务在 host dispose 时先 cancel 再限时等待在途生成，落在 App 的 20 秒关闭预算内；生成完成后的 CAS 读写用 `CancellationToken.None`（本地 SQLite 快操作），不会让关闭悬挂。
6. **标题只能取列表里那份记录，composer 快照永不回填**：`ApplyAdmittedConversation` 拿到的是提交时带的 `ConversationRecord`，它是「选中该会话那一刻」的副本；总结标题在回合中途写库后只靠 `ReloadConversationsAsync` 进列表。若 `ApplyConversationFilter` 在选中会话被重载时提前返回（只认 id 相同），`SelectedConversation` 会一直停在旧记录上，紧接着再发一条（steer 走的就是这条入口）就会把首条消息的截断标题写回侧栏，而且此后没有任何一次重载能纠正它——标题变化的 `TitleChanged` 已经用掉了。所以两条都要守：选中会话被重载时采用新记录；对已存在的会话，`ApplyAdmittedConversation` 只沿用列表里的标题，只刷新活动时间。
7. **`MaxOutputTokens` 不是「标题长度上限」，是「思考 + 标题」的共享预算**：推理模型可能花 800 个输出 token 才写出第一个标题字符，预算给小了会换回 `finish_reason=length` 且正文为空——那不是错误，只是「没有标题」，于是侧栏安静地停在截断标题上。改这个常量前先读 §1.4；上限同时受 20 秒生成超时约束（给到几千 token 只会把失败从「空响应」换成「超时」）。

## 4. 验收

- 新建会话发第一条消息 → 数秒内侧栏标题变为总结标题（与用户消息同语言）；切走再回来保持。
- 总结标题出现后、回合仍在跑时再发一条（steer）→ 侧栏标题保持总结标题，不退回首条消息的截断标题。
- 首条消息发出后立即手动重命名 → 生成完成不覆盖手动标题。
- 模型档案思考等级设为 high → 标题调用不携带 reasoning/thinking 参数（provider 默认）。
- 停用模型或断网 → 截断标题保留、回合正常、日志仅 warning。
- 长首条消息（数千字符）＋思考等级高的模型 → 仍必须出标题；真出现空响应时日志里必须有 warning，带 `finish_reason`、实际输出 token 与预算三者之一可分辨（`finish_reason=length` 且输出等于预算 = 思考吃满预算）。
- 生成被 20 秒超时打断 → 截断标题保留、日志 warning 指名「timed out」；关闭应用导致的取消不记 warning。
- 同会话第二条消息不触发；生成返回前删除会话 → 不复活、列表不出现。
- 生成在途时关闭应用 → 关闭在预算内完成，无未观察异常。
