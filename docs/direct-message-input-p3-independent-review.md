# Direct 消息输入：P3 独立审核

日期：2026-10-03。基线：`17243f9602b8e8657d91f7c05f56bd2c99cdac0e`。审核对象为该 HEAD 上全部未提交 P3 工作区，包括未跟踪源码。

**修复更新（2026-10-03，用户授权）：** 以下原审核结论和证据保留为修复前历史。IR-P3-01–11 已修复，最终同一代码连续两次全量 1490/0/4，Vue 106、Playwright 17、原前端探针 3/3；修复落点、测试迁移说明及 SHA-256 见 [修复记录](direct-message-input-p3-review-fixes.md)。这不是新的独立复审结论；Queue 默认关闭，Steer 继续关闭，原生/真实 provider 门槛仍未验证。

**原审核结论：P3 不通过独立审核，不能独立交付，Queue 不应在验收环境作为已验收功能启用；Steer 继续关闭。**

正式测试连续两次为 1454 通过 / 0 失败 / 4 门控跳过，但独立补充的 24 个 .NET 探针为 **14 通过 / 10 失败 / 0 跳过**，另有 3 个直接运行生产 composable 的前端探针全部失败。失败包括真实桥接协议不通、失败终态后抢先领取、Held 越过、关闭开关仍执行、调度饥饿、删除后会话重建及快照验证缺失。正式测试绿色不能替代这些契约。

本轮没有修改生产代码或既有测试，没有清理用户数据、启动用户 Desktop、杀用户进程、提交或推送。只新增审核文档、更新实施文档状态，并在 `.scratch/direct-message-input/p3-ind-review/` 写入临时探针及证据。探针用 MSBuild 额外 Compile 注入到独立输出，不加入正式测试项目。实施者自述未修改。

## 1. 方法、范围与执行结果

已读取 AGENTS.md、设计、实施验收清单、P2 独立审核、P2/P3 实施报告；直接核对 repository、writer、service、dispatcher、唯一 run owner、VM、删除、DI/生命周期、bridge/publisher、Vue composable/组件及测试断言。P3 开始时的跟踪差异为 25 文件，另有未跟踪文件；未把 `git diff` 未显示的新文件排除。

环境：Windows，.NET SDK 10.0.400、Node v22.19.0。正式 .NET 输出为各项目 `bin/p3-ind-review/`，探针为 `bin/p3-ind-probes/`；Vue build 输出改到 `.scratch/direct-message-input/p3-ind-review/vue-dist/`，避免覆盖 Desktop 的运行资源。

| 执行 | 通过 | 失败 | 跳过 | 结果 |
| --- | ---: | ---: | ---: | --- |
| `dotnet build SelfClaw.slnx --no-restore -p:BaseOutputPath=bin/p3-ind-review/ --nologo` | — | 0 | — | 0 警告、0 错误；`build.log` |
| 正式全量第一次 | 1454 | 0 | 4 | `p3-ind-review-full.trx` |
| 正式全量第二次，连续绿色 | 1454 | 0 | 4 | `p3-ind-review-full-2.trx` |
| 独立搜索选出的受影响套件 | 201 | 0 | 0 | `p3-ind-review-targeted.trx` |
| 初版独立探针 | 2 | 5 | 0 | `independent-probes.trx` |
| 扩展独立探针 | 6 | 9 | 0 | `independent-probes-expanded.trx` |
| 增补正常顺序/竞争/容量的探针 | 14 | 9 | 0 | `independent-probes-final.trx` |
| 最终探针，另覆盖成功事件但 Persisted=false 的释放窗口 | 14 | 10 | 0 | `independent-probes-final-2.trx` |
| 九个失败方法分别隔离复跑 | 0 | 9 | 0 | `isolate-<method>.trx`，各 1 项失败 |
| 终态释放窗口的两种结果隔离复跑 | 0 | 2 | 0 | `isolate-terminal-final.trx` |
| `npm --prefix SelfClaw.TranscriptVue test` | 100 | 0 | 0 | 24 文件，`vitest.log` |
| `npm --prefix SelfClaw.TranscriptVue run build -- --outDir ../.scratch/direct-message-input/p3-ind-review/vue-dist` | 成功 | 0 | — | `vue-build.log` |
| Playwright 首次 | 15 个用例已完成 | 0 个用例失败 | 0 | **命令收尾挂起，不算完整成功**；`playwright.log` |
| Playwright 沙箱外复跑 | 15 | 0 | 0 | 正常结束，22.5 秒；`playwright-rerun.log`、`playwright-results.json` |
| 生产 composable 独立 Node 探针 | 0 | 3 | 0 | `frontend-probes-run.log` |
| `git diff --check 17243f9` | 成功 | 0 | — | exit 0，仅 CRLF 提示 |

最终探针的 14 个通过包含一个诊断性断言：确认 stale pause 返回未暂停状态；它不是“stop 竞争契约通过”。所有 TRX 与日志完整 SHA-256、实际测试全名、错误摘录在文末。

执行的主要命令：

```powershell
$env:DOTNET_CLI_HOME = "$PWD/.dotnet"
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_NOLOGO = '1'
dotnet build SelfClaw.slnx --no-restore -p:BaseOutputPath=bin/p3-ind-review/ --nologo
dotnet test SelfClaw.slnx --no-restore -p:BaseOutputPath=bin/p3-ind-review/ --logger 'trx;LogFileName=p3-ind-review-full.trx' --results-directory .scratch/direct-message-input/p3-ind-review
dotnet test SelfClaw.slnx --no-build --no-restore -p:BaseOutputPath=bin/p3-ind-review/ --logger 'trx;LogFileName=p3-ind-review-full-2.trx' --results-directory .scratch/direct-message-input/p3-ind-review
dotnet test SelfClaw.Tests/SelfClaw.Tests.csproj --no-build --no-restore -p:BaseOutputPath=bin/p3-ind-review/ --filter 'FullyQualifiedName~ConversationInput|FullyQualifiedName~ConversationRunCoordinator|FullyQualifiedName~ConversationDeletion|FullyQualifiedName~MainWindowViewModelSubmission|FullyQualifiedName~ShellState|FullyQualifiedName~DesktopServiceRegistration|FullyQualifiedName~WebViewMessageRouter|FullyQualifiedName~DirectInputBoundary|FullyQualifiedName~SqliteConversationTurn|FullyQualifiedName~SqliteSchemaInitialization|FullyQualifiedName~SqliteSubagentTaskRepository|FullyQualifiedName~ContinuationRecovery|FullyQualifiedName~SubagentDeliveryDispatcher|FullyQualifiedName~CliAgentChatRuntime' --logger 'trx;LogFileName=p3-ind-review-targeted.trx' --results-directory .scratch/direct-message-input/p3-ind-review
dotnet test SelfClaw.Tests/SelfClaw.Tests.csproj --no-restore -p:BaseOutputPath=bin/p3-ind-probes/ -p:CustomAfterMicrosoftCommonTargets=D:/Repositories/SelfClaw/.scratch/direct-message-input/p3-ind-review/probes.targets --filter FullyQualifiedName~IndependentQueueProbes --logger 'trx;LogFileName=independent-probes-final-2.trx' --results-directory .scratch/direct-message-input/p3-ind-review
# 隔离复跑使用 --no-build、相同 probe 输出，以 FullyQualifiedName~<失败方法名> 筛选。
npm --prefix SelfClaw.TranscriptVue test
npm --prefix SelfClaw.TranscriptVue run build -- --outDir ../.scratch/direct-message-input/p3-ind-review/vue-dist
npm --prefix SelfClaw.TranscriptVue run test:e2e
node .scratch/direct-message-input/p3-ind-review/frontend-probes.mjs
git diff --check 17243f9
```

Playwright 首次 15 个测试均完成后，服务器收尾持续不退出，进程诊断被沙箱拒绝；仅对本轮启动的命令会话发送 Ctrl-C，随后按工具权限要求在沙箱外复跑成功。未杀用户进程。精确的挂起底层原因未确认，不能断言是产品缺陷或用复跑抹掉首次挂起。Node 探针第一次导入路径多一层 `../`，报 `ERR_MODULE_NOT_FOUND`，修正的仅是临时探针路径；原日志保留为 `frontend-probes.log`，不计产品失败。

## 2. 已复现的阻塞问题

下文相对路径缩写：`Inputs` = `SelfClaw.Desktop/Services/ConversationInputs/`，`Runtime` = `SelfClaw.Desktop/Services/Runtime/`，`Sql` = `SelfClaw.Infrastructure/Data/Sqlite/Repositories/`。探针完整类名为 `SelfClaw.Tests.IndependentReview.IndependentQueueProbes`。

### IR-P3-01：真实 Queue 订阅失败，状态枚举也不兼容（高）

- `SelfClaw.TranscriptVue/src/composables/useConversationInputs.js:78` 生成 `queue-1-<Date.now()>`；`Inputs/ConversationInputBridge.cs:38,145` 用 `Guid.TryParse` 接收 subscriptionId。实际 Vue 请求必然得到 `invalid-subscriptionId`，列表/重载恢复不能工作。
- `ConversationInputPublisher` 直接发送 `ConversationInputQueueItem`；`SelfClaw.Desktop/Services/WebView/WebViewHostChannel.cs:8` 没有枚举字符串 converter，Held 序列化为 `"status":3`。Vue `normalizeItem` 保留数字，`ConversationQueue.vue:13,35` 用 `'held'/'pending'/'claimed'` 判断。即使修复订阅，Held/Claimed 显示、取消/重试按钮仍错误。
- 真实 WPF dispatcher + bridge 探针 `Vue_subscription_identity_must_be_accepted_by_real_bridge`：期望 `conversation-input/state`，实际 `conversation-input/result ... invalid-subscriptionId`；隔离复跑仍失败。
- `Published_item_status_must_match_Vue_string_contract`：期望 `"status":"held"`，实际 `"status":3`；前端直接导入生产 reducer 再证 `3 !== 'held'`。
- 原 host 测试使用 `Guid.NewGuid()`，Vue mock 接受任何 subscriptionId 并返回字符串状态；两边均绿没有证明互通。

### IR-P3-02：Held 不构成 FIFO 屏障（高）

`Sql/SqliteConversationInputRepository.cs:326` 只查询 `kind=0 AND status=0 ORDER BY sequence LIMIT 1`。暂停只是会话级开关，恢复后会跳过前面的 Held，领取后面的 Pending；未排除前序 Claimed 亦有同类风险。

`Held_head_must_block_following_pending_after_resume` 在真实库写入 Held A、Pending B、未暂停队列，再执行领取，实际返回 B 的 `Status=Claimed, Sequence=2`；应返回 null。原 Q08 测试仅在仍然 paused 时检查后项不运行，没有恢复队列。

### IR-P3-03：Engine 释放准入早于失败暂停，扫描可启动 B（高）

`Runtime/ConversationTurnEngine.cs:99` 在 finally `_runs.Complete(handle)`，之后才返回 `ConversationTurnExecutionResult`；`Inputs/ConversationInputDispatcher.cs:224` 收到结果后才 Pause。扫描和新的 Submit 均可在这个窗口再次调用 `TryStartConversationAsync`，其唯一前提只是没有活动句柄及未暂停。

独立探针 `Failed_terminal_must_pause_before_releasing_admission(bool persistFailure)` 用 run coordinator 的完成通知作为 **TCS 同步点**：旧句柄已从注册删除，先阻塞 Engine 返回；另一次真实 dispatcher 启动调用进入。分别验证 A 持久化 Failed、A 发 Succeeded 但终态写入故障两种情况，**B 均返回 Started**。最终/隔离均 2 失败。没有 Sleep 或“任务还没调度所以零调用”的断言。

`MapFailureReason` 的 `!Persisted → persist-failed` 优先级正确，但无法弥补此前已放开的准入窗口。原 Q05 只等 paused 并检查剩余 B，没有运行 background scan 或阻塞完成窗口。

### IR-P3-04：stop/失败暂停忽略 revision 冲突（高，代码核对 + 存储行为证据）

`Sql/SqliteConversationInputRepository.cs:285` 的 CAS 冲突返回当前 state，不抛异常。VM `MainWindowViewModel.cs:193` 及 dispatcher `PauseAsync:273` 都读 revision 后写暂停，却不检查返回的 `Paused`。如果期间入队/消费递增 revision，暂停未落库，VM finally 仍 Stop；dispatcher 也可记录通知后直接返回。若 stop 落在回合结束之后，没有后续 Cancelled 再次暂停兜底。

`Pause_revision_conflict_must_not_be_treated_as_paused` 控制“读 state → 接受 B 递增 revision → 旧 revision pause”，重读确认 `Paused=false`。该诊断测试通过证明冲突返回值，**没有声称已运行完整 VM stop 竞争测试**。删除 `PauseQueueAsync` 虽重试 3 次，耗尽仍静默返回，删除失败后保持暂停也不是无条件保证。

### IR-P3-05：关闭功能开关仍可恢复并执行旧队列（高）

`Inputs/ConversationInputService.cs:116,129` 的 Resume/Retry 无开关检查；`ConversationInputDispatcher.TryStartConversationAsync:149` 同样无检查。关闭只影响 background scan 和成功后的链式启动。

`Disabled_feature_must_not_resume_persisted_backlog`：QueueEnabled=false，已有 Pending，执行启动恢复得到 paused，再调用真实 Resume；实际 `Runs.IsRunning=true`，应不启动。隔离复跑同样失败。原 Q16 只测试恢复后保持 paused，再调用 start，未触发 Resume。

“关闭时空闲普通 Direct 仍经相同链执行”的产品选择可单独讨论，但不能解释关闭后 Resume backlog 的违规；设计 §11 的关闭限制仍未满足。

### IR-P3-06：候选扫描饥饿，且准备异常可中断整轮扫描（高）

`Inputs/ConversationInputDispatcher.cs:133` 每次取得全量候选；`140` 在检查 running/paused 之前累计数量并在 32 截断，没有游标或轮换。repository 候选包括暂停/Held/Claimed。前 32 个长期暂停，后面可执行会话永久没有扫描机会。

`Scan_must_reach_candidate_after_32_paused_parents` 写入 33 个真实库会话，按实际返回顺序暂停前 32 个，完整执行 3 次生产 `ScanOnceAsync`；第 33 个仍未准入。探针调用私有扫描方法仅为了消除计时依赖，执行的仍是生产扫描代码。原 Q15 直接分别调用两个会话的 Submit/Start，根本没有运行扫描。

此外，单候选 Prepare/读取异常没有局部 catch，而由 `ExecuteAsync:102` 捕获整轮异常；持续失败的前项可每轮阻断后项。正常“无进展”有 250ms delay，不判为必然忙循环；但公平性已被实测否定。

### IR-P3-07：删除 tombstone 不约束输入受理，可重建已删除会话（高）

`Inputs/ConversationInputService.cs:74` 先直接 Accept；`Sql/SqliteConversationInputRepository.cs:509` 的 EnsureConversation 使用 `INSERT ... ON CONFLICT DO NOTHING`，不经 run owner 的删除/关闭裁决。直到落库后 dispatcher 才 TryReserve，拒绝已经太晚。

`Deleted_conversation_must_not_be_recreated_by_late_acceptance`：捕获 conversation，建立 tombstone，真实删除 DB 行并保留成功 tombstone，然后模拟延迟准备返回的 Submit（携带原 conversation）。**Accepted=true**，实际会话被重新插入但无法运行。原删除测试只检查删除完成后的数据库和 TryReserve，不测试迟到受理。VM Direct Prepare 使用 `CancellationToken.None` 且准备前没有受理生命周期预留，与 P2 先预留的 CLI 路径不同。

### IR-P3-08：执行快照没有冻结 Agent 定义，Held retry 不重新捕获（高）

`ConversationInputExecutionSnapshot.cs` 只有 AgentId/AgentRevision，没有 Agent 定义。Service `:63` 使用提交的 AgentId 和**当前全局** `_agents.Revision`；dispatcher `:240` 从文件重新 ListAgents，`CreateAgent` 使用当前定义。`AgentSettingsService.cs:32` 的 revision 来自 `ExtensionStateChangeNotifier._revision`，为进程内计数，非每份 Agent 的持久化内容版本；不同 agent/extension 的变化也会使输入 Held，重启后不能据此证明定义未变。

Retry `Service:129 → repository:249` 只把 Held 改 Pending，未更新 snapshot，且保留队列 paused。`Retry_must_recapture_changed_agent_snapshot` 实测重试后旧 revision 1 仍为 1，当前为 0；恢复执行会再次 Held。原 `A_held_item_can_be_edited_back_to_pending_and_retried` 只断言状态变回 Pending，没有检查新快照和随后实际执行。

默认模型确实在 Service:56 解析成具体 ID，权限和 Root ID/path 也有数据字段；这部分正确不等于完整冻结契约通过。重复 Submit 会重新解析默认模型/revision，再比较整个 snapshot，也可能把相同客户端请求判为冲突。

### IR-P3-09：物理 Workspace Root 消失不被 Held 拦截（高）

dispatcher `PrepareAsync:258` 只比较数据库 root 行及字符串路径，没有 `Directory.Exists`，也没有验证 `conversation.WorkspaceRootId` 与快照 root 的归属一致。`Missing_physical_root_must_hold_before_runtime` 使用存在的 DB root 行和不存在的目录，实际返回 Started，期望 Failed/Held。隔离复跑失败。

原报告 Q08 映射没有 Root 丢失测试。这里验证的是运行入口已经放行，不声称已经调用真实文件工具；真实 runtime 后续报错不能替代“消费前 Held，保留输入”契约。

### IR-P3-10：实际发送链没有客户端受理幂等键（高，直接调用链证据）

`useChatComposer.js:17` 仍走 send-prompt；router `WebViewMessageRouter.cs:282` 只把 prompt/workspaceMode/model 传 VM，requestId 仅用于回包。VM `MainWindowViewModel.cs:576` 每次构造新的 `Guid.NewGuid()` 作为 ClientRequestId。因此相同前端 requestId 重投也不会到达 repository 的唯一键；前端超时重试本身也生成新 requestId。

原 Q03 仅直接测试 repository 接口的同 requestId；该存储测试确实通过，但不是实际用户 ACK 丢失重试的证明。无独立端到端重复 user 测试，本项为确定的调用链缺陷，不编造运行计数。

### IR-P3-11：部分操作响应清空队列，入队还强制跟随（中）

bridge `ReplyStateAsync:105` 返回 pause/resume 的部分 result（没有 items/subscriptionId）；composable `run:89` 对任何 response 调用同一 reducer，`reduceQueueState:47` 把缺失 items 解释为 `[]`。独立 Node 探针用真实响应形状证明原 1 项变成 0 项；若 full push 先到、result 后到同 revision，列表可一直丢失到下次变化。

`useChatComposer.js:21` 在任何 Accepted 后无条件 `transcriptScroll.resumeFollow()`；Direct 忙碌入队没有分支，违反队列变化不强制跟随。ACK 才清框的基本顺序正确，但这里没有按 selection generation 防止旧 ACK 调用当前草稿 accept/refresh；原 Q10 测试导航发生在 ACK 后，不能证明导航在 ACK 前的情况。

## 3. 存储、消费与架构核验

| 审核项 | 判定 | 代码及行为证据 | 本轮实际测试计数 |
| --- | --- | --- | --- |
| A 存储整体 | **不通过** | Accept/Edit/Cancel/Hold/Retry/Claim/Recover 的队列修改与 queue_revision 在同一个 SQLite 写事务；容量同事务无淘汰。FIFO Held 查询错误（IR-02）；CAS 调用方错误（IR-04） | 存储正式 11 通过；Held 探针 1 失败；容量额外 2 通过 |
| B 统一消费事务 | **通过（事务边界）** | `SqliteConversationTurnWriter.cs:9,32` 同 connection/transaction 插 turn、真实 user、匹配 claim、Consumed、revision；`TurnRepository:25` commit 后返回；Engine:57 先 await startup，再 Build/Stream；无空 assistant | 正式 claim/start、回滚、stale claim 3 通过；逐表注入 4 通过；相关 P2 回归在 201 套件中通过 |
| C Service | **不通过** | 默认模型具体 ID、权限/root 字段存在，附件拒绝正确；Agent 定义/revision、retry、真实 requestId、删除受理屏障有缺陷 | 正式附件 1 通过、幂等存储 1 通过；retry/删除探针各 1 失败 |
| D Dispatcher | **不通过** | 结果映射优先 Persisted=false 正确；释放窗口、32 项饥饿、暂停 CAS、关闭开关缺陷 | 正式 dispatcher 15 通过；终态窗口 2、扫描 1、开关 1 失败 |
| E 启动恢复 | **不通过（完整要求）** | repository:383 暂停未处理会话、旧 Claimed→Held、不重跑 Consumed；旧目标已终态的 Pending Steer→Held。UI 恢复失效、Held 越过、快照/root 缺陷未满足整体恢复 | 正式 store/dispatcher 恢复各 1 通过；Held/root/retry 各 1 失败 |
| F 桥接/Vue | **不通过** | origin guard、canSteer=false、ACK 前保留草稿成立；真实订阅/状态协议与部分 result 错误 | 正式 host 4、Vitest 100 通过；host/枚举探针 2 失败，Node 3 失败 |
| G 跨会话/容量/快照 | **不通过（完整要求）** | 已接受 input.ConversationId 固定；20/500/UTF-8 限制可证；实际发送未携带客户端 conversationId，旧 ACK 与严格快照上限测试不足 | 容量/UTF-8 额外 2 通过，正式导航 1 通过；不将未覆盖旧 ACK/最大快照算通过 |
| A01 唯一 owner | **不通过（完整裁决契约）** | 运行注册/取消/完成唯一 owner 确实为 RunCoordinator；没有第二张 busy 表。但受理未受 tombstone 管理，失败推进先释放后暂停 | 正式 RunCoordinator 16 通过；真实 Held/paused continuation 1 通过；删除重建 1、释放窗口 2 失败 |
| A04 共用 writer/事务 | **通过** | 开始/边界/终态/delivery 均复用 transaction-only writer；repository 不嵌套提交；detached executor:127 走 ApplyDetachedEventAsync，delivery repository:329 在自身事务 TryWrite 后 resolution | 逐点消费注入 4 通过；`Delivery_terminal_fault_rolls_back_delivery_turn_fragment_and_usage` 及 P2 boundary/detached 回归通过 |
| A06 单一 Direct 链 | **不通过（完整验收）** | VM Direct 确已统一 Service→Dispatcher→RunCoordinator→Engine，无空闲直接执行；但 Q03 用户幂等和生命周期裁决失败，不能仅凭调用点判整项通过 | VM 定向 1、正常 FIFO 独立 2、消费回滚 4 通过；幂等用户入口为代码缺陷 |

消息 ID：`SelfClaw.Core/Runtime/ConversationInputMessageId.cs:13` 对固定格式 claimId+inputId SHA-256 取 16 字节；读取与消费均复用 Compute，独立于 TurnId。未消费行的 message_id 为 NULL；只有同事务 Consumed 写入映射。未建立另一张映射表。

消费故障探针分别在 conversation_turns INSERT、messages INSERT、conversation_inputs UPDATE、conversation_input_state UPDATE 安装**临时库专用触发器**抛 `review-fault`，全部验证 turn/user 为 0，input 仍 Claimed。没有改生产 DDL。原有 before-commit 回滚及 stale claim 用例再次运行通过。写入代码有重复构造 user 的实现片段（ConsumeFollowUpAsync 重复 InsertUserAsync 的构造/WriteMessage/附件逻辑），但最终共用 WriteMessageAsync，未观察到一项写两条 user；建议收敛重复逻辑，不把此观察夸大为已发生重复写。

所有权清单：SQLite input repository 是输入事实 owner；RunCoordinator 的 `_runs/_deletions/_stopping` 和 RunHandle 是运行注册/取消/完成 owner；ConversationRuntimeState/Recorder 归约内容；DirectConversationLoop 管 provider 迭代；DirectToolInvoker 管工具执行；SessionCoordinator 的 `_transcriptLoads` 是历史加载合并，不是 busy 表。Dispatcher 的 `HashSet<Task> _running` 是收尾任务集合，不按会话裁决准入，**不以字段同名认定第二套状态机**；但 Task fault 未统一观测及收尾快照后的新任务风险未有专项验证。

恢复补充限制：`RecoverOnceAsync` 在 await 恢复完成前就把 `_recoveryStarted` 置 1，且 Submit/Resume 直接启动不 await 该恢复；`BackgroundService` 的启动顺序不能等同于恢复完成屏障。Pending Steer 只在目标 `status != Running` 时 Held，旧进程 Running target 的处理未被本轮恢复用例证明。两项为读码风险，未声称执行了进程崩溃测试。

## 4. Q01–Q16 逐项判定

下表用 `D`/`S`/`H` 表示文末完整名称清单中的 ConversationInputDispatcherTests / SqliteConversationInputQueueTests / ConversationInputHostTests；`P` 表示本轮 IndependentQueueProbes。列出的数字为对应本轮一次测试结果，不累加重复全量以放大覆盖。

| ID | 判定 | 实际测试及计数 | 直接代码/缺口 |
| --- | --- | --- | --- |
| Q01 | 通过（合成 runtime） | D.A_successful_turn_advances_the_fifo_one_independent_turn_at_a_time 1 通过；P.Running_A_excludes_B_C_and_each_followup_has_its_own_turn 1 通过 | Engine:57 startup→state→request；A 用显式 gate 保持运行，B/C 只在队列，释放后 user A/B/C 分属 3 Turn。不是实际联网 provider |
| Q02 | 通过（Pending FIFO） | P.Idle_new_D_starts_existing_B_and_preserves_C_D_order 1 通过 | repository:326；D 触发 B，余 C/D。不扩张为 Held FIFO 通过 |
| Q03 | **不通过** | S.Acceptance_is_idempotent_and_rejects_a_changed_payload_as_a_conflict、S.Claim_and_start_atomically_write_one_user_and_mark_the_input_consumed 各 1 通过 | VM:576 每次新 requestId，router:282 丢弃受理幂等键；存储局部通过不代表用户重试 |
| Q04 | 通过（存储裁决） | S.Edit_and_cancel_honor_revision_and_a_claim_makes_the_item_immutable 1 通过；P.Explicit_edit_cancel_claim_order 4 通过 | repository:212/226/304；分别强制 edit/cancel 先于 claim 和 claim 先于 edit/cancel，后者即使使用当前 revision 仍拒绝 |
| Q05 | **不通过** | D.A_terminal_persistence_failure_pauses_the_queue_with_the_persist_reason 1 通过；P.Failed_terminal_must_pause_before_releasing_admission(true) 1 失败 | Engine:99 与 dispatcher:224 的释放窗口；B Started |
| Q06 | **不通过** | D.Non_success_turn_outcomes_pause_the_queue 3 通过；D.A_failed_turn_pauses_the_queue_and_never_starts_the_next_item 1 通过；P.Failed_terminal_must_pause_before_releasing_admission(false) 1 失败；Cancelled 见 Q07 | 简单路径终态映射正确，竞争窗口违反不得启动 B |
| Q07 | **不通过（完整竞争）** | D.Stop_during_a_running_turn_prevents_the_next_item_and_pauses_with_cancelled_reason、D.Stop_after_the_next_item_is_admitted_cancels_that_turn_and_pauses、D.Stop_pauses_the_queue_so_the_next_item_does_not_start 各 1 通过；P.Pause_revision_conflict_must_not_be_treated_as_paused 1 诊断通过 | 前两项有 runtime gate，但未覆盖暂停读写间 revision 变化；VM:193 忽略未暂停返回值，IR-04 |
| Q08 | **不通过** | D.A_disabled_model_holds_the_input_and_does_not_cross_it、D.An_agent_revision_change_holds_the_input_and_pauses 各 1 通过；P.Missing_physical_root_must_hold_before_runtime、P.Retry_must_recapture_changed_agent_snapshot、P.Held_head_must_block_following_pending_after_resume 各 1 失败 | dispatcher:238；Root 丢失/恢复 Held/重新捕获不成立 |
| Q09 | 通过（准入范围） | ConversationRunCoordinatorTests.A_user_queue_blocks_only_its_own_continuation_and_not_another_parent 1 通过；P.Paused_Held_parent_blocks_only_its_own_continuation 1 通过；既有并发准入/读阻塞 TCS 回归通过 | RunCoordinator:59 先同一注册预留，再 HasUnprocessedInputsAsync；repository:125 包括 0/1/3，未依据 paused 排除 |
| Q10 | **不通过（完整跨导航证据不足）** | ShellStateTests.Direct_submission_keeps_its_submit_time_target_after_navigation 1 通过；H.Publisher_ignores_other_conversations_and_refreshes_on_reload 1 通过 | 测试用 FakeInputCoordinator，且导航在 ACK 后；未证明导航在 ACK 前不清 B 草稿、不刷新 B；composer 无 generation guard |
| Q11 | **不通过** | ConversationDeletionServiceTests.A_failed_delete_releases_the_barrier_but_keeps_the_durable_queue_paused、A_successful_delete_removes_the_queue_and_never_reclaims 各 1 通过；P.Deleted_conversation_must_not_be_recreated_by_late_acceptance 1 失败 | Service:74 受理不经 tombstone，Late Accepted=true；关闭期间准备/受理完整生命周期未验证 |
| Q12 | **不通过（完整恢复可用性）** | S.Startup_recovery_holds_old_claims_stale_steers_and_pauses_without_replaying、D.A_restart_holds_a_previous_claim_and_never_replays_it 各 1 通过 | repository:383 的存储路径通过；真实列表订阅失败，启动前恢复屏障/旧 Running Steer 未证，真实进程恢复未跑 |
| Q13 | 通过（存储容量） | S.Acceptance_rejects_oversized_prompts_and_does_not_evict_the_oldest_item、S.Global_capacity_is_enforced_across_conversations 各 1 通过；P.Global_limit_must_reject_501st_in_a_new_conversation、P.Concurrent_acceptance_enforces_20_and_utf8_64KiB 各 1 通过 | 同一 IMMEDIATE 写事务，22 个并发受理恰 20 Accepted/2 Full；全局第 501 条使用第 26 个空会话；中文 65536/65537 字节边界 |
| Q14 | 通过 | S.A_start_transaction_failure_leaves_no_orphan_turn_user_or_consumed_input、S.A_stale_claim_revision_rolls_back_the_start_transaction 各 1 通过；P.Consumption_write_fault_rolls_back_at_each_table 4 通过 | writer:32 同事务，逐表中断无 turn/user/Consumed 半成品；无 provider 请求在 startup 返回前 |
| Q15 | **不通过** | D.One_failing_conversation_does_not_block_another_conversation 1 通过；P.Scan_must_reach_candidate_after_32_paused_parents 1 失败 | dispatcher:140 永远前 32 项；原测试未测 background scan、公平性或扫描频率 |
| Q16 | **不通过** | D.Attachments_are_rejected_explicitly、D.A_disabled_switch_keeps_a_persisted_backlog_but_does_not_schedule_it 各 1 通过；P.Disabled_feature_must_not_resume_persisted_backlog 1 失败 | Service:40 附件明确拒绝；Resume/Retry 绕过 switch |

## 5. 越界和删除清单核对

| 边界 | 搜索/读码证据 | 结论 |
| --- | --- | --- |
| schema v30，无 P3 DDL | `rg -n 'SchemaVersion' .../SqliteDatabase.cs` → :10=30；`git diff 17243f9 -- .../SqliteSchema.cs .../SqliteDatabase.cs` 空 | **通过**。Schema 工作区和基线 Git blob 均 `bdd0f8569919a04234eeb758af213a3a153698e5`；conversation_inputs:344/input_state:380 完全相同，无 §11 新 DDL 理由需要审查 |
| Steer 不执行 | 搜索 `SuspendedBySteer|SupersededBySteer|targetTurn|TryClaim|steer-not-enabled`；bridge:73–75 拒绝 promote/steer；唯一新 claim 为 FollowUp | **未发现 Steer 执行路径**。P2 ReadClaimedBatch/边界消费保留；repository 新通用 Accept 校验仍允许 Steer 载荷，但生产 Service 固定 FollowUp，无 Steer 生产调用者，不仅凭关键词判越界 |
| child 不获得输入 | Schema:415–420 insert/update owner trigger；RunCoordinator:34 拒绝 Subagent origin | **通过实际入口/DB 边界**；P2 `Sqlite_constraints_reject_missing_turn_cross_conversation_and_consumed_without_mapping` 重跑；未把 child 专用 execution session 当重复 owner |
| CLI 不获得队列 | VM:553 按 Mode 分发，SubmitCliAsync:605 保留 TryReserve→Engine；Service:46 拒绝非 Direct | **通过实际发送路径**。内部 store 不理解 CLI 选择，不能把 Interactive DB trigger 说成“数据库知道 CLI”；约束由真实发送路径/Service 实施 |
| 无空闲 Direct 旁路 | `rg -n 'TryReserve|ExecuteAsync' MainWindowViewModel.cs` → TryReserve:614 仅 SubmitCliAsync；Engine:680 只由 CLI ExecuteAcceptedTurnAsync 使用。Direct:563 只受理 | **路径删除通过**，不掩盖受理/删除与终态竞争缺陷 |
| 无第二 input/busy 状态机 | Core DTO/接口用途，RunCoordinator、Engine、SessionCoordinator、dispatcher 字段及消费者核对 | **未发现第二套会话 busy 真值**；输入持久化仍同两表，Dispatcher task 集合用于生命周期收尾。受理绕过唯一裁决点是另外的实际缺陷 |
| P4/P5/P6 | 工具 invoker/审批无 supersede/skip 新路径；Vue 为队列最小入口及 edit/retry service；无完整 Steer UI/发布 smoke | 未发现启用 P4 或完整 P5/P6 的实现。P3 被要求的恢复/列表不因同名目标在后续阶段出现而认定越界 |

附加规范问题：repository 增长至约 900 行，受理、队列、恢复、旧边界和 JSON/SQL 映射集中；内嵌 `StateRow/PayloadV1`、publisher 内嵌 `QueueStateResponse`（带计算 Type 属性）不符合“一文件一 DTO/服务分离”。新增服务均 sealed，Infrastructure await 已见 ConfigureAwait(false)。dispatcher `RunAsync:211` 把 OCE 变成 Pause+return，publisher `PushSafelyAsync` 泛 catch 吞 OCE，不符合 AGENTS 的重抛要求；BackgroundService 的宿主取消 catch 应与业务调用取消分别评估。Vue 队列组件使用 Lucide、scoped styles 和独立 composable，这部分规范符合。审核未按规范要求顺手重构，因为用户明确禁止改生产代码。

快照上限：列表预览 `CreatePreview:692` 按 200 UTF-16 code unit 截断，无整段正文；detail 单独读取。Publisher:128 先测完整 bytes，但 `Truncate:145` 用空 subscription/requestId、revision=0、pauseReason=null 的替身计算，随后未再次验证实际 envelope，**不是严格的最终 256KiB 证明**。正式“bounded”测试只放两条短消息；本轮未构造生产可达的极限错误字段负载，不把理论超限标成已复现，也不将严格上限验收计通过。

## 6. 与实施者报告的差异、测试质量

计数本身复现一致：正式 .NET 1454/0/4 两次、存储 11、dispatcher 15、host 4、删除 5、run coordinator 16、DI 1、Vue 100、Playwright 15。差异主要在**这些测试究竟证明了什么**。

1. 报告称 Q01/Q02 同一测试含 D，但 `ConversationInputDispatcherTests.cs:34` 只有 A/B/C；没有 D，也没有在 A 运行中设置屏障。用 Items.Count==0 等待可能早于最后 runtime 请求/终态（Consumed 即不在列表）。本轮另补两个 gate 测试才验证正常顺序。
2. Q03 没覆盖真实 router/VM requestId；Q04 原测试是顺序执行 edit→claim→edit/cancel，无同时竞争，也缺 cancel 先赢。本轮补 4 个显式先后测试，不修改原断言。
3. Q05 原测试仅等 paused；Q06 Blocked/Truncated 理论各只有 A，没有 B。没有错误实现执行下一项的显式机会。独立完成通知屏障证实该窗口有真实错误。
4. Q08 原 invalid-model 测试只在 paused 状态加后项，不能证明恢复后不越过 Held；Agent 测试人为使用当前 revision+1，未实际变更定义/重启；Root 缺失完全没有对应原用例。
5. Q09 原新增测试直接申请 continuation，并非用户队列与 continuation 同时竞争；P2 的 run-owner 并发 TCS 回归保留，加本轮真实 Held/paused DB 用例可支持唯一 owner 与用户优先的局部结论。
6. Q10 导航在 ACK 后，使用 FakeInputCoordinator，不能证明实际 dispatcher/provider 快照或旧 ACK。既有 ShellState 代理测试把 `Runtime.Requested.Agent.Id` 换成 `Inputs.Submissions.Agent.Id`，断言退到受理 DTO 层。
7. Q11 删除成功测试没有迟到 Submit，Q12 直接调用 RecoverOnce/store，未启动重启进程；Q15 两会话各直接 Start，未跑扫描。Q16 关闭测试用 paused 阻止 claim，没有测试开关本身对 Resume 的阻止。
8. 原全局容量测试第 501 条投给已经满 20 的会话，即使删掉全局限制也可通过。本轮改用第 26 个会话验证，且独立补并发每会话容量和中文 UTF-8 边界，均通过。
9. Q14 原本只有 before-commit 一个故障点和 stale revision，不能称逐点注入；本轮额外 4 个临时触发器覆盖中间写入，均通过。
10. host 名为 `Edit_cancel_pause_and_resume...` 的测试没有执行 cancel。Vue mock 把 pause/cancel 等一律返回 full state，未模拟真实部分 result。15 个 Playwright 用例全在 `tests/e2e/activity.spec.js`，是 Activity/插件回归；`rg 'queue|Queue|conversation-input' tests/e2e` 无 Queue 用例，不能说 Queue 完成浏览器验收。
11. 原 `MainWindowViewModelSubmissionTests` 从“历史加载屏障与连续提交只一回合”改成 FakeInputCoordinator 接收且 runtime 为空；这是职责迁移后的局部测试，**原竞争覆盖已撤掉**，需在真实新链补回。未发现新增 Skip 或修改 expected 为错误值的证据；本轮未放宽既有断言，也未用反复重跑把生产探针失败抹掉。

原 harness 的 `WaitForAsync` 用 10ms 轮询等观测条件，`Dispose` 用 Thread.Sleep 等注册释放；不能把这类等待当并发顺序屏障。两个 stop 用例确有 GatedAgentChatRuntime TCS，但暂停 CAS、完成释放窗口不在其屏障覆盖内。独立探针的竞争顺序使用 TCS/完整扫描调用控制，不靠 Sleep 碰运气。

## 7. 不计通过的未验证项与交付结论

以下均未验证，不折算为通过：真实 provider/Ollama；真实 WPF/WebView2 渲染与窗口交互；toast；原生进程恢复 smoke；设计/实施文档的桌面手动脚本；真实用户数据迁移/清理（本轮禁止且未执行）。WpfDispatcherTest 只是 dispatcher 消息循环，不等于真实 WebView2。

四个门控跳过为：

- `SelfClaw.Tests.Desktop.Services.Activities.ActivityProviderSmokeTests.Configured_provider_reasoning_reaches_the_live_activity_channel`
- `SelfClaw.Tests.Desktop.Services.Activities.ActivityProcessRecoveryTests.Child_process_waits_with_uncommitted_text_and_recorded_tool`
- `SelfClaw.Tests.Desktop.Services.Activities.ActivityProcessRecoveryTests.Forced_process_exit_recovers_only_persisted_history_as_interrupted`
- `SelfClaw.Tests.Desktop.Services.Activities.DesktopActivityWebViewTests.Real_WebView_renders_three_live_children_and_restores_after_navigation`

P3 的正常消费事务与 P2 的唯一运行注册基础可复核，但当前 Queue 在真实 bridge 处即不通，并存在会执行错误后续任务的高优先缺陷。必须修复后重新审核，不允许仅补一轮绿色测试后宣称独立交付。Queue 不作为通过验收的功能启用；Steer 继续关闭。这里不撤销 P2 的独立审核结论。

## 8. 原始证据附录

原始文件目录：`.scratch/direct-message-input/p3-ind-review/`。以下附录由实际 TRX/XML 和文件字节生成；跳过数取 UnitTestResult 的 NotExecuted（TRX Counters.notExecuted 在本环境为 0，不能直接用它漏算 4 项）。

## 原始证据清单（自动从本轮文件计算）

| 文件 | 通过 | 失败 | 跳过 | SHA-256 |
| --- | ---: | ---: | ---: | --- |
| `independent-probes-expanded.trx` | 6 | 9 | 0 | `f75b4b83c4e9ba185a8f212b8dd0207cef504718c3b0b7fbf345bf68b495928c` |
| `independent-probes-final-2.trx` | 14 | 10 | 0 | `6b3ca836940fc61033f32346fe3b95db46bb1d20ec76d81544882733d2121168` |
| `independent-probes-final.trx` | 14 | 9 | 0 | `5fccdcfcba2a1457b7902888e9ac2cc2926325f55365f6557ae6a38a8177a960` |
| `independent-probes.trx` | 2 | 5 | 0 | `8ef1e381383323c2330dbe07323f59304c5b1b4dd7fe648b7109e160329e41a6` |
| `isolate-Deleted_conversation_must_not_be_recreated_by_late_acceptance.trx` | 0 | 1 | 0 | `ff5d88bb4b861b2fe55d85c20156feebde0fb2a4feda3a59cea1834c1a8c3d76` |
| `isolate-Disabled_feature_must_not_resume_persisted_backlog.trx` | 0 | 1 | 0 | `82602b3a05a08b377c3f1e49a65bd5af9f06ff5e8ff5c8c06c92699c0a270adf` |
| `isolate-Failed_terminal_must_pause_before_releasing_admission.trx` | 0 | 1 | 0 | `0b1f76186f0bc76449ef948ae02571a5a054826524e08bffcf4ef29760913b82` |
| `isolate-Held_head_must_block_following_pending_after_resume.trx` | 0 | 1 | 0 | `9b838c019db9e97029d7321a3900e8e9df1ef67b26c1c3c5904d31b094f129bc` |
| `isolate-Missing_physical_root_must_hold_before_runtime.trx` | 0 | 1 | 0 | `4cd5b1173e8dc0d1a48c337a5a455e00dae711c0689467e58831de1644807460` |
| `isolate-Published_item_status_must_match_Vue_string_contract.trx` | 0 | 1 | 0 | `d25cf8c480db4b5a1e8f2f2db55996d28479998af069cb16369197b9df189339` |
| `isolate-Retry_must_recapture_changed_agent_snapshot.trx` | 0 | 1 | 0 | `7688e65a4fa1b32b1580180be5d7af3cdfa7f0c005015eba973e535766f6ba47` |
| `isolate-Scan_must_reach_candidate_after_32_paused_parents.trx` | 0 | 1 | 0 | `d63b2c842b98622c5ad43d8407e9cb292a6022d2b18fed2d141bc0236668093f` |
| `isolate-terminal-final.trx` | 0 | 2 | 0 | `5db8387362b5e25e890d172557751c68535a20781b82ae3c62408a71397974a9` |
| `isolate-Vue_subscription_identity_must_be_accepted_by_real_bridge.trx` | 0 | 1 | 0 | `f3f403f0fe0f9c44042c9bd64fab8c23d3e23fe700e0695ba2393c2d943ef0d6` |
| `p3-ind-review-full-2.trx` | 1454 | 0 | 4 | `5ecd6737b800b2d866df16b6146f942d8c7301c8781e7ac4651a24c275871ed6` |
| `p3-ind-review-full.trx` | 1454 | 0 | 4 | `1609f116a124896cab94c774f73677eb0c6329b64674c6cc740f15dce5f50270` |
| `p3-ind-review-targeted.trx` | 201 | 0 | 0 | `27434bab6ce4923f74b186c4c09863a98f746bb551da0fa9a39880e642de9381` |
| `build.log` | — | — | — | `2368cc9d3e21c51033ed1f07da7419364f7598bf8aaa182c98f756af484909ef` |
| `vitest.log` | — | — | — | `cad1ba53089b99f6724db211e9ec3e5789e1ae52a55edc4a7391ac84083ac9eb` |
| `vue-build.log` | — | — | — | `08133595d8c7a5ced002d1f243864d5f25877eaf16a2c76bf60a30f52a052522` |
| `playwright.log` | — | — | — | `240e3198d4989e9881420d159e4df75c92b6d3059e4a81e0c843fc1f904758cc` |
| `playwright-rerun.log` | — | — | — | `2e6cd060e154de76df849eb1d5c58584c041e34118d2f82ca1af8b4028912358` |
| `playwright-results.json` | — | — | — | `23189029dd982bf2db7028be930ee57f2272e87f298e7d5487983060aaf2f571` |
| `frontend-probes.log` | — | — | — | `07eccc72a6fac8e7b087330514657d48088cbefffa7af518baa0cbab0075059e` |
| `frontend-probes-run.log` | — | — | — | `9391cd898e1d69020d8a36cada351a81e91ec0ef5d87e648165e59b246b73d18` |
| `diff-check.log` | — | — | — | `612bdf25ca04e78361e550fa78b61d13c91d8373778ee2446644d6b4445767c7` |
| `IndependentQueueProbes.cs` | — | — | — | `13e3fccf77ca23800ac027b5e9abe570a35559685a73a44f6d464fa06d89c9c2` |
| `frontend-probes.mjs` | — | — | — | `a05516a081204d8311d26d0c992f1ccf3d4d640cdeb1990d3c34fde7e4a9c89a` |
| `probes.targets` | — | — | — | `ee209938ad25baf44856e9fc52b08373f9800307177a1d0d4554e5ebabf60d11` |

### 本轮定向测试的完整名称与实际结果

此清单来自 `p3-ind-review-targeted.trx`，不是从实施者报告复制；每行计数包括 Theory 展开。

- `SelfClaw.Tests.Desktop.Composition.DesktopServiceRegistrationTests.Desktop_registration_resolves_the_input_queue_graph`：1 通过 / 0 失败 / 0 跳过。
- `SelfClaw.Tests.Desktop.Services.ConversationInputs.ConversationInputDispatcherTests.A_disabled_model_holds_the_input_and_does_not_cross_it`：1 通过 / 0 失败 / 0 跳过。
- `SelfClaw.Tests.Desktop.Services.ConversationInputs.ConversationInputDispatcherTests.A_disabled_switch_keeps_a_persisted_backlog_but_does_not_schedule_it`：1 通过 / 0 失败 / 0 跳过。
- `SelfClaw.Tests.Desktop.Services.ConversationInputs.ConversationInputDispatcherTests.A_failed_turn_pauses_the_queue_and_never_starts_the_next_item`：1 通过 / 0 失败 / 0 跳过。
- `SelfClaw.Tests.Desktop.Services.ConversationInputs.ConversationInputDispatcherTests.A_restart_holds_a_previous_claim_and_never_replays_it`：1 通过 / 0 失败 / 0 跳过。
- `SelfClaw.Tests.Desktop.Services.ConversationInputs.ConversationInputDispatcherTests.A_successful_turn_advances_the_fifo_one_independent_turn_at_a_time`：1 通过 / 0 失败 / 0 跳过。
- `SelfClaw.Tests.Desktop.Services.ConversationInputs.ConversationInputDispatcherTests.A_terminal_persistence_failure_pauses_the_queue_with_the_persist_reason`：1 通过 / 0 失败 / 0 跳过。
- `SelfClaw.Tests.Desktop.Services.ConversationInputs.ConversationInputDispatcherTests.An_agent_revision_change_holds_the_input_and_pauses`：1 通过 / 0 失败 / 0 跳过。
- `SelfClaw.Tests.Desktop.Services.ConversationInputs.ConversationInputDispatcherTests.Attachments_are_rejected_explicitly`：1 通过 / 0 失败 / 0 跳过。
- `SelfClaw.Tests.Desktop.Services.ConversationInputs.ConversationInputDispatcherTests.Non_success_turn_outcomes_pause_the_queue`：3 通过 / 0 失败 / 0 跳过。
- `SelfClaw.Tests.Desktop.Services.ConversationInputs.ConversationInputDispatcherTests.One_failing_conversation_does_not_block_another_conversation`：1 通过 / 0 失败 / 0 跳过。
- `SelfClaw.Tests.Desktop.Services.ConversationInputs.ConversationInputDispatcherTests.Stop_after_the_next_item_is_admitted_cancels_that_turn_and_pauses`：1 通过 / 0 失败 / 0 跳过。
- `SelfClaw.Tests.Desktop.Services.ConversationInputs.ConversationInputDispatcherTests.Stop_during_a_running_turn_prevents_the_next_item_and_pauses_with_cancelled_reason`：1 通过 / 0 失败 / 0 跳过。
- `SelfClaw.Tests.Desktop.Services.ConversationInputs.ConversationInputDispatcherTests.Stop_pauses_the_queue_so_the_next_item_does_not_start`：1 通过 / 0 失败 / 0 跳过。
- `SelfClaw.Tests.Desktop.Services.ConversationInputs.ConversationInputHostTests.Edit_cancel_pause_and_resume_are_revision_bound_and_reject_stale_subscriptions`：1 通过 / 0 失败 / 0 跳过。
- `SelfClaw.Tests.Desktop.Services.ConversationInputs.ConversationInputHostTests.Publisher_ignores_other_conversations_and_refreshes_on_reload`：1 通过 / 0 失败 / 0 跳过。
- `SelfClaw.Tests.Desktop.Services.ConversationInputs.ConversationInputHostTests.Steer_and_promote_are_rejected_explicitly`：1 通过 / 0 失败 / 0 跳过。
- `SelfClaw.Tests.Desktop.Services.ConversationInputs.ConversationInputHostTests.Subscribe_publishes_the_bounded_queue_and_detail_reads_the_restricted_body`：1 通过 / 0 失败 / 0 跳过。
- `SelfClaw.Tests.Desktop.Services.Runtime.ConversationDeletionServiceTests.A_failed_delete_releases_the_barrier_but_keeps_the_durable_queue_paused`：1 通过 / 0 失败 / 0 跳过。
- `SelfClaw.Tests.Desktop.Services.Runtime.ConversationDeletionServiceTests.A_successful_delete_removes_the_queue_and_never_reclaims`：1 通过 / 0 失败 / 0 跳过。
- `SelfClaw.Tests.Desktop.Services.Runtime.ConversationDeletionServiceTests.Concurrent_delete_cannot_remove_the_first_deletions_barrier`：1 通过 / 0 失败 / 0 跳过。
- `SelfClaw.Tests.Desktop.Services.Runtime.ConversationDeletionServiceTests.Delete_cancels_preparation_then_waits_for_run_and_children_before_cascade`：1 通过 / 0 失败 / 0 跳过。
- `SelfClaw.Tests.Desktop.Services.Runtime.ConversationDeletionServiceTests.Failed_child_drain_aborts_cascade_and_releases_only_the_owned_barrier`：1 通过 / 0 失败 / 0 跳过。
- `SelfClaw.Tests.Desktop.Services.Runtime.ConversationRunCoordinatorTests.A_blocked_continuation_input_read_does_not_block_another_conversation`：1 通过 / 0 失败 / 0 跳过。
- `SelfClaw.Tests.Desktop.Services.Runtime.ConversationRunCoordinatorTests.A_user_queue_blocks_only_its_own_continuation_and_not_another_parent`：1 通过 / 0 失败 / 0 跳过。
- `SelfClaw.Tests.Desktop.Services.Runtime.ConversationRunCoordinatorTests.Cancellation_while_reading_continuation_priority_releases_preparing_owner`：1 通过 / 0 失败 / 0 跳过。
- `SelfClaw.Tests.Desktop.Services.Runtime.ConversationRunCoordinatorTests.Children_cannot_be_registered_with_the_interactive_owner`：1 通过 / 0 失败 / 0 跳过。
- `SelfClaw.Tests.Desktop.Services.Runtime.ConversationRunCoordinatorTests.Concurrent_reservations_have_exactly_one_owner_before_preparation`：1 通过 / 0 失败 / 0 跳过。
- `SelfClaw.Tests.Desktop.Services.Runtime.ConversationRunCoordinatorTests.Deletion_blocks_preparation_and_retains_successful_tombstones`：1 通过 / 0 失败 / 0 跳过。
- `SelfClaw.Tests.Desktop.Services.Runtime.ConversationRunCoordinatorTests.Failed_deletion_only_releases_its_own_tombstone`：1 通过 / 0 失败 / 0 跳过。
- `SelfClaw.Tests.Desktop.Services.Runtime.ConversationRunCoordinatorTests.Failed_preparation_releases_the_reservation_without_reporting_a_started_turn`：1 通过 / 0 失败 / 0 跳过。
- `SelfClaw.Tests.Desktop.Services.Runtime.ConversationRunCoordinatorTests.Late_completion_cannot_remove_a_successor_or_execute_an_old_handle`：1 通过 / 0 失败 / 0 跳过。
- `SelfClaw.Tests.Desktop.Services.Runtime.ConversationRunCoordinatorTests.Observer_cancellation_propagates_without_leaking_admission_or_completion`：2 通过 / 0 失败 / 0 跳过。
- `SelfClaw.Tests.Desktop.Services.Runtime.ConversationRunCoordinatorTests.Only_interactive_direct_handles_own_an_input_session`：3 通过 / 0 失败 / 0 跳过。
- `SelfClaw.Tests.Desktop.Services.Runtime.ConversationRunCoordinatorTests.Stop_and_shutdown_include_preparing_reservations_and_wait_for_owner_cleanup`：1 通过 / 0 失败 / 0 跳过。
- `SelfClaw.Tests.Desktop.Services.Runtime.ConversationRunCoordinatorTests.Unprocessed_user_inputs_prevent_a_continuation_and_release_its_reservation`：1 通过 / 0 失败 / 0 跳过。
- `SelfClaw.Tests.Desktop.ViewModels.MainWindowViewModelSubmissionTests.SubmitPromptAsync_accepts_direct_through_the_input_coordinator_without_executing_a_turn`：1 通过 / 0 失败 / 0 跳过。
- `SelfClaw.Tests.Desktop.ViewModels.ShellStateTests.Direct_submission_keeps_its_submit_time_target_after_navigation`：1 通过 / 0 失败 / 0 跳过。
- `SelfClaw.Tests.Infrastructure.Data.Sqlite.Repositories.SqliteConversationInputQueueTests.A_held_item_can_be_edited_back_to_pending_and_retried`：1 通过 / 0 失败 / 0 跳过。
- `SelfClaw.Tests.Infrastructure.Data.Sqlite.Repositories.SqliteConversationInputQueueTests.A_stale_claim_revision_rolls_back_the_start_transaction`：1 通过 / 0 失败 / 0 跳过。
- `SelfClaw.Tests.Infrastructure.Data.Sqlite.Repositories.SqliteConversationInputQueueTests.A_start_transaction_failure_leaves_no_orphan_turn_user_or_consumed_input`：1 通过 / 0 失败 / 0 跳过。
- `SelfClaw.Tests.Infrastructure.Data.Sqlite.Repositories.SqliteConversationInputQueueTests.Acceptance_is_idempotent_and_rejects_a_changed_payload_as_a_conflict`：1 通过 / 0 失败 / 0 跳过。
- `SelfClaw.Tests.Infrastructure.Data.Sqlite.Repositories.SqliteConversationInputQueueTests.Acceptance_rejects_oversized_prompts_and_does_not_evict_the_oldest_item`：1 通过 / 0 失败 / 0 跳过。
- `SelfClaw.Tests.Infrastructure.Data.Sqlite.Repositories.SqliteConversationInputQueueTests.Claim_and_start_atomically_write_one_user_and_mark_the_input_consumed`：1 通过 / 0 失败 / 0 跳过。
- `SelfClaw.Tests.Infrastructure.Data.Sqlite.Repositories.SqliteConversationInputQueueTests.Edit_and_cancel_honor_revision_and_a_claim_makes_the_item_immutable`：1 通过 / 0 失败 / 0 跳过。
- `SelfClaw.Tests.Infrastructure.Data.Sqlite.Repositories.SqliteConversationInputQueueTests.Global_capacity_is_enforced_across_conversations`：1 通过 / 0 失败 / 0 跳过。
- `SelfClaw.Tests.Infrastructure.Data.Sqlite.Repositories.SqliteConversationInputQueueTests.Pause_round_trips_with_monotonic_revision_and_blocks_claims`：1 通过 / 0 失败 / 0 跳过。
- `SelfClaw.Tests.Infrastructure.Data.Sqlite.Repositories.SqliteConversationInputQueueTests.Queue_disabled_rejects_a_busy_conversation_but_keeps_existing_inputs`：1 通过 / 0 失败 / 0 跳过。
- `SelfClaw.Tests.Infrastructure.Data.Sqlite.Repositories.SqliteConversationInputQueueTests.Startup_recovery_holds_old_claims_stale_steers_and_pauses_without_replaying`：1 通过 / 0 失败 / 0 跳过。

### 独立探针完整名称、计数与原始错误

- `SelfClaw.Tests.IndependentReview.IndependentQueueProbes.Explicit_edit_cancel_claim_order(claimFirst: True, cancel: False)`：Passed。
- `SelfClaw.Tests.IndependentReview.IndependentQueueProbes.Global_limit_must_reject_501st_in_a_new_conversation`：Passed。
- `SelfClaw.Tests.IndependentReview.IndependentQueueProbes.Held_head_must_block_following_pending_after_resume`：Failed。

```text
Expected claimed to be <null> because Held A must block B even when the queue is unpaused, but found SelfClaw.Core.Models.ConversationInputRecord

{

    ClaimId = {8ff87d4d-ef09-4867-9186-e111eec3a0a2},

    ClientRequestId = "d18a33238bf54dbfb34e2e215165dad9",

    ConsumedAtUtc = <null>,

    ConsumedTurnId = <null>,

    ConversationId = {3052d2d9-364e-46d0-aa40-9979730c79ca},

    CreatedAtUtc = <2026-10-03 10:31:33.0308112 +0h>,

    ErrorMessage = <null>,

    ExecutionSnapshot = SelfClaw.Core.Models.ConversationInputExecutionSnapshot

    {

        AgentId = "build",

        AgentRevision = 0L,

        Mode = AgentExecutionMode.Direct {value: 0},

        ModelProfileId = {c4877845-69f3-4c36-a93f-35154bcecc31},

        ToolPermissionMode = ToolPermissionMode.RequireApproval {value: 0},

        WorkspaceRootId = <null>,

        WorkspaceRootPath = <null>

    },

    Id = {a78dd9b7-6280-4d3b-a625-e402343f51c7},

    Kind = ConversationInputKind.FollowUp {value: 0},

    MessageId = <null>,

    OwnerRunId = {64ab50f8-5d8f-4ae6-a47f-483473eb0716},

    Prompt = "B",

    ReasonCode = <null>,

    Revision = 2,

    Sequence = 2L,

    Status = ConversationInputStatus.Claimed {value: 1},

    TargetTurnId = <null>,

    UpdatedAtUtc = <2026-10-03 10:31:33.0378256 +0h>

}.
```

- `SelfClaw.Tests.IndependentReview.IndependentQueueProbes.Explicit_edit_cancel_claim_order(claimFirst: False, cancel: False)`：Passed。
- `SelfClaw.Tests.IndependentReview.IndependentQueueProbes.Consumption_write_fault_rolls_back_at_each_table(table: "messages", operation: "INSERT")`：Passed。
- `SelfClaw.Tests.IndependentReview.IndependentQueueProbes.Deleted_conversation_must_not_be_recreated_by_late_acceptance`：Failed。

```text
Expected result.Accepted to be False because late UI preparation must not resurrect a tombstoned conversation, but found True.
```

- `SelfClaw.Tests.IndependentReview.IndependentQueueProbes.Consumption_write_fault_rolls_back_at_each_table(table: "conversation_inputs", operation: "UPDATE")`：Passed。
- `SelfClaw.Tests.IndependentReview.IndependentQueueProbes.Explicit_edit_cancel_claim_order(claimFirst: False, cancel: True)`：Passed。
- `SelfClaw.Tests.IndependentReview.IndependentQueueProbes.Failed_terminal_must_pause_before_releasing_admission(persistFailure: True)`：Failed。

```text
Expected attempt.Status not to be ConversationInputStartStatus.Started {value: 0} because a scan in the completion window must not admit B after A failed, but it is.
```

- `SelfClaw.Tests.IndependentReview.IndependentQueueProbes.Idle_new_D_starts_existing_B_and_preserves_C_D_order`：Passed。
- `SelfClaw.Tests.IndependentReview.IndependentQueueProbes.Running_A_excludes_B_C_and_each_followup_has_its_own_turn`：Passed。
- `SelfClaw.Tests.IndependentReview.IndependentQueueProbes.Published_item_status_must_match_Vue_string_contract`：Failed。

```text
Expected System.Text.Encoding.UTF8.GetString(bytes) "{"inputId":"9fcc9424-8b4b-413b-944b-973f5c050e96","sequence":1,"kind":0,"status":3,"revision":1,"preview":"A"}" to contain ""status":"held"".
```

- `SelfClaw.Tests.IndependentReview.IndependentQueueProbes.Consumption_write_fault_rolls_back_at_each_table(table: "conversation_turns", operation: "INSERT")`：Passed。
- `SelfClaw.Tests.IndependentReview.IndependentQueueProbes.Missing_physical_root_must_hold_before_runtime`：Failed。

```text
Expected attempt.Status to be ConversationInputStartStatus.Failed {value: 3} because a database row does not prove the directory exists, but found ConversationInputStartStatus.Started {value: 0}.
```

- `SelfClaw.Tests.IndependentReview.IndependentQueueProbes.Disabled_feature_must_not_resume_persisted_backlog`：Failed。

```text
Expected h.Runs.IsRunning(id) to be False because a disabled Queue must not dispatch a persisted backlog through resume, but found True.
```

- `SelfClaw.Tests.IndependentReview.IndependentQueueProbes.Scan_must_reach_candidate_after_32_paused_parents`：Failed。

```text
Expected h.Runs.IsRunning(ids[32]) to be True because completed scans must not starve the 33rd parent behind paused parents, but found False.
```

- `SelfClaw.Tests.IndependentReview.IndependentQueueProbes.Failed_terminal_must_pause_before_releasing_admission(persistFailure: False)`：Failed。

```text
Expected attempt.Status not to be ConversationInputStartStatus.Started {value: 0} because a scan in the completion window must not admit B after A failed, but it is.
```

- `SelfClaw.Tests.IndependentReview.IndependentQueueProbes.Explicit_edit_cancel_claim_order(claimFirst: True, cancel: True)`：Passed。
- `SelfClaw.Tests.IndependentReview.IndependentQueueProbes.Concurrent_acceptance_enforces_20_and_utf8_64KiB`：Passed。
- `SelfClaw.Tests.IndependentReview.IndependentQueueProbes.Consumption_write_fault_rolls_back_at_each_table(table: "conversation_input_state", operation: "UPDATE")`：Passed。
- `SelfClaw.Tests.IndependentReview.IndependentQueueProbes.Vue_subscription_identity_must_be_accepted_by_real_bridge`：Failed。

```text
Expected posted.Last() "{"type":"conversation-input/result","requestId":"review","operation":"error","ok":false,"errorCode":"invalid-subscriptionId","error":"\u961F\u5217\u64CD\u4F5C\u5931\u8D25\uFF0C\u8BF7\u91CD\u8BD5\u3002"}" to contain "conversation-input/state" because the exact Vue subscription shape must work against the host.
```

- `SelfClaw.Tests.IndependentReview.IndependentQueueProbes.Pause_revision_conflict_must_not_be_treated_as_paused`：Passed。
- `SelfClaw.Tests.IndependentReview.IndependentQueueProbes.Paused_Held_parent_blocks_only_its_own_continuation`：Passed。
- `SelfClaw.Tests.IndependentReview.IndependentQueueProbes.Retry_must_recapture_changed_agent_snapshot`：Failed。

```text
Expected retried.ExecutionSnapshot!.AgentRevision to be 0L because retry explicitly recaptures the changed definition instead of repeatedly holding the same snapshot, but found 1L (difference of 1).
```


## 9. 用户授权修复及回归

本次用户明确要求修复审核文档中的问题，已修改生产实现并补充正式回归。完整修复映射、行为边界、连续全量结果、哈希与未验证项见 [P3 审核问题修复记录](direct-message-input-p3-review-fixes.md)。原始失败/TRX 不变；待修复后的独立复审，不把自动回归绿色冒充真实桌面或 provider 验收。
