# 输入框图片附件设计

状态：已实施（A1–A6 完成并经独立审核）· 设计冻结（决策 A–G）· 更新：2026-10-18 · schema 仍为 v32（零 DDL）
关系：扩展 [Direct 消息队列与 Steer 设计](direct-message-input-design.md) 的准入、载荷与回合语义；转录渲染沿用 [消息段设计](message-segments-system-design.md) 的既有图片段；压缩相关见 [回合开始压缩](direct-context-compaction-design.md) 与 [回合内压缩](direct-in-turn-compaction-design.md)；子代理不继承父附件见 [Subagent 设计](direct-subagent-system-design.md)。

本文同时取代附件尚未接入时「输入入口一律拒绝」的历史表述：`attachments-unsupported` 只作为老库历史 reason 保留，不再有新的产生点。

## 0. 结论摘要

| 环节 | 接入前 | 最终实现 |
| --- | --- | --- |
| 文件落盘 | 全仓没有代码写 `{AppData}/attachments`，只有测试数据能驱动下游 | 宿主唯一入口 `AttachmentStore`，前端不接触文件系统 |
| 输入入口 | `SubmitAsync` 对任何附件返回 `AttachmentsUnsupported` | 校验后接受：仅图片、仅 Direct、单文件 10 MiB、每条 6 张、模型必须声明多模态 |
| 队列载荷 | `payload_json` 只有文本字段 | `payload_json`（Version 1）增可选附件引用数组，`Version` 不变 |
| 回合写入 | `ConversationTurnStart.Attachments` 无生产者 | 引用在 claim / steer 消费时还原成记录并写入 `message_attachments` |
| 模型调用 | composer 只取 `MarkdownContent`，图片不可达 | `DataContent` + `TextContent` 进入同一用户 unit；计数按常数；压缩避开含图 unit |
| 清理 | 无任何附件回收路径 | 会话删除回收整目录；取消/丢弃即时删单文件；启动时清「会话不存在且 24 小时未动」的目录 |
| 前端 | 无添加入口 | 选择器、粘贴、拖拽、待发送 chip、能力闸门与队列数量徽标 |

## 1. 已确认决策（2026-10-18，用户批准）

| # | 决策 | 值 |
| --- | --- | --- |
| A | 范围 | 只做图片；只做 Direct 的交互式输入（新对话 / 队列 follow-up / promote 成 steer）；CLI 与子代理继续拒绝 |
| B | 上限 | 单文件 ≤ 10 MiB；每条输入 ≤ 6 张；白名单 `image/png`、`image/jpeg`、`image/webp`、`image/gif` |
| C | 落盘布局 | `{AppData}/attachments/{conversationId}/{attachmentId}{ext}` |
| D | schema | 不变，仍是 v32；附件引用走 `conversation_inputs.payload_json` 的可选字段 |
| E | 含图与压缩 | 含图 unit 不参与压缩规划；图片 token 计一个常数 |
| F | 能力闸门 | `IsModelAvailableAsync(modelId) && Configuration.IsMultimodal == true`（可用性与多模态必须同时成立）；前端按 `EnabledModelView.IsMultimodal` 禁用图片入口，宿主在准入时兜底拒绝 |
| G | 清理 | 会话删除回收整目录；待发送附件在取消/丢弃时删除；启动时清理「会话已不存在」的残留目录 |

## 2. 三条容易记错的最终事实

1. **首版仍然要求「图片必须伴随文字」。** 宿主 `ValidateAccept` 仍拒绝空 prompt，前端 `canSend` 也沿用 `hasText` 门槛；「空文本 + 附件」只在 composer 内部作为防御分支保留（steer / 续写路径可能命中），不是可用入口。放宽它需要单独评估队列与 `send-prompt` 的空文本语义，本批不做。
2. **决策 E 的最终形态是「压缩候选集截断到最早含图 unit 之前」**，不是「整盘放弃压缩」。整盘放弃会让「历史里出现过一张图」的会话永久失去回合开始压缩，最终由 `EnsurePromptFitsWindow` 抛错收场；跳过含图 unit 只压缩其它 unit 又会破坏「摘要覆盖连续前缀 + `ThroughSequence` 回放过滤」的不变量。截断候选集同时保住两者；截断后为空才 `Decline`。
3. **启动清理是「目录级 + 24 小时年龄护栏」**，不是「24h 孤儿文件扫描」。新对话的附件目录在会话行创建之前就已存在，而 `ListConversationsAsync` 只返回 `Interactive` 会话；没有年龄护栏就会误删刚加到输入框的图片。运行期也不做按引用集合的逐文件扫描。

## 3. 存储与生命周期

### 3.1 唯一路径权威

`SelfClaw.Infrastructure/Attachments/AttachmentPathResolver.cs`（`public static`）是布局、上限、媒体类型与 URL 规则的唯一来源，写入方、转录投影、桌面桥和清理路径都调它：

- 根目录 `{AppData}/attachments`、会话目录 `{root}/{conversationId}`、目录名字面量只在这里定义一份。
- `MaximumByteLength = 10 * 1024 * 1024` 是宿主唯一权威数值；`AllowedMediaTypes` 是唯一白名单；扩展名与媒体类型双向映射也只在这里。
- `TryResolveExistingFile` / `TryCreateRelativePath` / `TryResolveStoredFile` 共用同一套根内包含性判定（`Path.GetRelativePath` 结果不以 `..` 开头且非绝对路径），因此「能写」「能读」「能投影」不会各有一套规则。
- `CreateRelativePath(conversationId, attachmentId, mediaType)` 由引用推出相对路径：发送侧只报 id + 元数据，路径永远由宿主推导。
- `CreateSourceUrl` / `TryCreateSourceUrl` 生成 `https://attachments.selfclaw.local/...`，与转录图片逐字一致（分隔符统一 `/`、`Uri.EscapeDataString` 后还原 `%2F`）；文件不存在时返回 false，序列化时省略 `previewUrl`，前端显示占位。
- `CreateRecord` 只负责 `AttachmentReference` + 绝对路径 → `MessageAttachmentRecord` 的记录形状；包含性、存在性、跳过策略与时间策略留在各自调用点。

### 3.2 写入与删除

`IAttachmentStore` 只有三个方法，实现 `AttachmentStore` 为 `internal sealed`：

- `IngestAsync`：媒体类型必须在白名单（否则 `ArgumentException`）；按 64 KiB 分块读入，超过 10 MiB 立即拒绝（不让超限上传先占满内存）；magic bytes 必须与声明类型一致（PNG `89 50 4E 47`、JPEG `FF D8 FF`、GIF `47 49 46 38`、WebP `RIFF....WEBP`）；`attachmentId = Guid.NewGuid()`，扩展名由媒体类型决定；文件名只保留末尾段。
- `DeleteAsync`：删除会话目录下该 id 的文件，幂等。
- `DeleteConversationAsync`：删除整个会话目录，目录不存在不算失败。

`ConversationDeletionService.DeleteAsync` 在 SQLite 删除成功之后回收会话附件目录：失败只记警告、不让删除整体失败，但 `OperationCanceledException` 照常重抛。构造函数用可选参数追加依赖，既有 `new ConversationDeletionService(...)` 调用点无需改动。

### 3.3 启动清理

`AttachmentMaintenanceHost`（`BackgroundService`）每次启动跑一次 `RunOnceAsync`：

- 枚举附件根下的目录；目录名是合法 Guid 且仍在 `IConversationRepository.ListConversationsAsync` 结果里 → 保留。
- 其余目录再看年龄护栏：目录内所有文件的最新写入时间（空目录用目录自身时间）早于 24 小时才递归删除；否则保留。
- 运行期失败只记 warning，绝不让宿主启动失败。

单文件回收（取消、移除 chip、提交被拒）由桥的 `discard` 与前端调用负责，不依赖启动清理。

## 4. 桥与前端契约（`attachments/*`）

`AttachmentBridge` 只在 `WebViewMessageRouter.RouteDocumentAsync` 里路由（只有那里带 `ownerHandle`），响应统一为
`{ type: "attachments/result", requestId, operation, ok, attachments?: [{ attachmentId, fileName, mediaType, byteLength, previewUrl? }], error? }`。

| 操作 | 请求 | 行为 |
| --- | --- | --- |
| `attachments/pick` | `{ conversationId, multiple? }` | 宿主文件对话框（owner handle 传入，初始目录「我的图片」）；取消 → `ok: true` 且空列表；逐张 `IngestAsync` |
| `attachments/ingest-bytes` | `{ conversationId, fileName, mediaType, dataBase64 }` | **裸 base64**（`data:` 前缀由前端剥离）；解码前先按长度拒绝超限；解码后交 `IngestAsync` |
| `attachments/discard` | `{ conversationId, attachmentId }` | 删除单个文件，幂等 |

`conversationId` 必须是合法非空 Guid，否则拒绝。桥异常统一转成 `ok: false` + 人话 `error`，不穿到 router 的 `Unsupported message` 分支。应用-origin 守卫仍是唯一信任边界，附件路由不另开入口。

## 5. 队列载荷、准入与能力闸门

- `ConversationInputPayload(Version, Prompt, RequestPrompt, RequestFingerprint, Attachments?)`：`Version` 保持 1，缺字段即空数组，旧行照常读出；`AttachmentReference` 只存相对路径 + 名称 / 媒体类型 / 字节数。
- `ConversationInputRecord` / `ConversationInputQueueItem`（加 `AttachmentCount` 供徽标）/ `ConversationInputDetail` 回填附件；`conversation-input/state` 每项带 `attachmentCount`。
- `ConversationInputService.SubmitAsync` 判定顺序（顺序即语义，短路）：
  1. `submission.Attachments` 非空且 `Agent.Mode != Direct` → `direct-only`（CLI 显式拒绝，不静默丢弃）。
  2. 数量 > 6 → `attachment-limit-exceeded`。
  3. 逐项校验：Kind 必须是 Image、字节数在 `(0, 10 MiB]`、媒体类型在白名单、`StoragePath` 能收敛成根内相对路径且文件存在 → 否则 `attachment-invalid`。
  4. 取到实际要跑的模型快照后，附件非空且 `!await IsModelMultimodalAsync(modelId)` → `image-unsupported-by-model`。
  5. 旧的 `AttachmentsUnsupported` 拒绝分支已删除；常量与前端兜底映射保留给历史 `reason_code`。
- 文案（宿主权威）：「最多可发送 6 张图片。」/「图片已失效，请重新添加。」/「当前模型不支持图片输入，请在模型管理里确认多模态能力。」前端 `conversationInputReasons.js` 同步兜底。
- 能力闸门 `AiProviderSettingsService`：`IsModelAvailableAsync(modelId) && Configuration.IsMultimodal == true`，两者共用 `ResolveUsableModelAsync`（模型启用 → 连接启用 → adapter 支持该 `ApiFormat`），避免前端允许加图而宿主随后拒绝。`ListEnabledModelsAsync` 仍不过滤 adapter 格式：宿主拒绝走可读 reason，前端不得复制可用性逻辑。
- `send-prompt` 的 `attachments` 只接受 `{ attachmentId, fileName, mediaType, byteLength }`；缺字段或非法一律按 `attachment-invalid` 拒绝整条请求；相对路径由 `CreateRelativePath` 推导再核实文件存在。
- 重试（Retry）只换执行快照，附件在 payload 里保持不动；`ConversationInputExecutionValidator` 不新增附件校验（附件是提交期事实）。

## 6. 回合贯通

- 队列里持久化的是 `AttachmentReference`（相对路径），回合需要的是 `MessageAttachmentRecord`（绝对 `StoragePath`）。转换统一走 `AttachmentPathResolver.TryResolveExistingFile` + `CreateRecord`：首发 claim、steer 消费与测试夹具复用同一形状，路径不在 dispatcher 或仓储里手拼。
- 消费时文件已不存在 → 跳过该附件并记 warning（准入已校验过一次，这里是二次兜底，不让整条输入失败；boundary 路径无 logger，静默跳过）。
- `ConversationTurnEngine` 在 `InsertUserAsync` 之前把记录的 `MessageId` 重绑为本回合用户消息 id（`ReplaceAttachmentsAsync` 会校验该相等性）。
- 用户消息落库后 `message_attachments` 出现真实行；附件不进入子代理与 CLI 路径。
- `ConversationInputItem` 的附件按值参与相等性（显式 `Equals` / `GetHashCode`，`GetHashCode` 用附件数量），否则 `RegisterBoundary` 的 `SequenceEqual` 会因引用不同误判。
- 同一 `attachmentId` 落入两条消息会撞主键，但当前没有可达路径（重复消费会先被 claim 的 UPDATE 与事务回滚挡住；前端重试复用同一 `clientRequestId`；重新添加会生成新 id），失败也响亮（`PersistFailed` + 队列暂停），本批不加「引用一经消费即作废」的约束。

## 7. 模型调用

- `AttachmentContentReader` 只为带 `MessageAttachmentKind.Image` 附件的用户消息预读字节；路径经 `TryResolveStoredFile`、媒体类型白名单与体积校验；文件缺失 / 越界 / 超限 / IO 失败 → 跳过该附件并 warning，`OperationCanceledException` 重抛。产物 `DataContent(bytes, mediaType) { Name = fileName }`，随回合上下文存活，不跨回合缓存。
- `DirectPromptComposer.BuildUnitsCore`：有附件时用户 unit 内容是**先图片后文本**；只有图片没有文字也照常产出 unit（当前入口仍要求文字，这是防御分支）。`DirectConversationContext.AppendInputs` 对 steer 用同一形状，steer 仍是受保护 entry。
- `DirectTokenCounter`：`DataContent` 计 `PerImageTokens = 1024`；计数缓存键包含图片数量，避免同一 `MessageId` 的纯文本与含图计数互相污染。
- 压缩：`PlanCompression` 把候选集截断到最早含图 unit 之前（见 §2），后续阈值 / 保留循环 / `prefix` 全部基于截断集合，`Decline` 仍上报未截断候选数；`DirectContextCompressor` 另有防御，把 prefix 里的 `DataContent` 换成 `[image: name]` 占位文本，即使未来放开规划也不会把二进制发给摘要模型。
- 已知成本：每回合最多 128 次 provider 请求会重复发送同一批图片字节；本批不引入跨回合缓存。

## 8. 前端输入框

- `useComposerAttachments(bridge, selectedConversationId)`：只保管宿主回的引用（id + 元数据 + `previewUrl`），提供加图 / 移除 / 清空 / 上限预检；单图 10 MiB、每条 6 张与宿主同值，宿主校验仍是权威。新对话 uuid 提前生成并与 `send-prompt` 共用，保证目录与最终会话一致。
- `FileReader.readAsDataURL` 结果必须剥掉 `data:...;base64,` 前缀；`previewUrl` 缺失时 chip 显示 `ImageOff` 占位而不是破图。
- `ComposerPanel` 在输入框上方渲染 chips；粘贴绑在 textarea（纯文本粘贴不拦截），拖拽绑在 composer 容器并整体高亮。
- 提交 payload 带四个字段；`accept()` 只清文本与 chip（**不删文件**，队列已持有引用），切会话时 `discardAll()` 才删除；retry key 包含附件 id 列表。宿主拒绝时 `error` 优先、缺失时用 `describeInputReason(reason)` 兜底，并保留 chip 以便重试。
- 能力闸门：`agentMode === 'direct' && selectedModelSupportsImages()`；`isMultimodal` 缺失或 `null` 一律按不支持处理，不禁用整个 composer、不抛错。
- 队列卡片在 `attachmentCount > 0` 时显示「· N 张图」；`normalizeItem` 用 `Number(item.attachmentCount) || 0` 归一。

## 9. 不变量与已知限制

- schema 仍是 v32：不新增表、不改列、不做迁移；附件引用只活在 `payload_json`。
- 仅图片、仅 Direct 交互式输入；CLI 与子代理继续拒绝，父会话附件不泄漏给 child。
- 前端闸门只读 `isMultimodal`；宿主是唯一裁决点，非法附件不会误发。
- 首版不支持「只有图片、没有文字」的提交。
- 启动清理只回收「会话不存在且 24 小时未动」的目录，不保证进程被强杀后立刻清空残留。

## 10. 手工验收清单

1. **能力闸门**：选中未标记多模态的 Direct 模型 →「添加图片」禁用并给出原因；在设置里把该模型标为支持图像输入 → 入口可用。
2. **发送**：加 1 张图 + 文字发送 → 转录里用户消息显示缩略图，点击可放大；模型回复能引用图中内容。只有图片、没有文字时发送按钮不可用（首版限制，宿主 `ValidateAccept` 仍拒绝空 prompt）。
3. **队列**：忙时加图发送 → 队列卡片显示图片数量；该输入被消费后转录出现图片。
4. **steer**：turn 运行中带图输入并「调整当前任务」→ 该回合的下一请求能看到图片（模型回答体现）。
5. **限制与体积**：选择第 7 张时被拒并提示；单图 > 10 MiB 被拒。⚠ 还需实测**粘贴**通道的真实上限：用一张接近 10 MiB 的图走 Ctrl+V（base64 要过 WebView2 消息通道），若失败则把实测结论与可用上限写回本文，并把前端预检与宿主常量一起下调（宿主数值是权威）。
6. **CLI**：切到 CLI 模式 → 图片入口禁用；若绕过前端提交带附件请求，宿主返回可读拒绝。
7. **清理**：删除带图会话 → `{AppData}/attachments/{conversationId}` 目录消失（会话删除路径是即时回收）。启动清理带 24 小时年龄护栏：**刚产生的**孤儿目录会被保留，只有「会话不存在且目录内最新写入早于 24 小时」的目录才会在下次启动被清掉 —— 手工验证时请先把目录写入时间改到 25 小时前再重启，不要期待新目录当场消失（这是为了避免误删「新对话尚未提交」的附件而刻意加的）。
8. **不落库回归**：带图回合的 schema 版本仍为 32，`documents` 无迁移痕迹（`PRAGMA user_version` 不变）。
