# 生图工作台设计

OpenAI Images 协议（`AiProviderApiFormat.OpenAIImages`）在 SelfClaw 里的落地：协议适配、能力协商、
长请求的超时与取消、产物落盘与保留。

## 1. 分层

| 层 | 文件 | 职责 |
| --- | --- | --- |
| Core | `Models/ImageGeneration/*`、`Interfaces/ImageGeneration/*` | 纯 DTO 与 3 个方法的契约 |
| Infrastructure | `ImageGeneration/ImageGenerationService` | 解析 profile → connection → 凭据，建 client，管超时 |
| Infrastructure | `ImageGeneration/OpenAiImageGenerationClient` | 请求形状、按模型能力裁剪参数、结果映射 |
| Infrastructure | `ImageGeneration/OpenAiImageModelCapabilities` | 模型 → 可接受参数集的唯一事实来源 |
| Infrastructure | `ImageGeneration/GeneratedImagePathResolver` | 产物目录布局与虚拟主机 URL |
| Desktop | `Services/ImageGeneration/ImageGenerationBridge` | `image-generation/*` 协议、入参加固、落盘、取消 |
| Vue | `composables/useImageStudio.js` + `components/image-generation/*` | 工作台 UI，控件由能力驱动 |

出站一律走 `AiProviderHttpClientProvider.CreateTurnClient`（`AGENTS.md` 不变量 13），
凭据只在 Infrastructure 内解密（不变量 1）。

## 2. 能力协商：为什么不能一套参数打天下

三个模型家族对同一组字段的要求互不相容：

| 字段 | dall-e-2 | dall-e-3 | gpt-image-1 |
| --- | --- | --- | --- |
| `size` | 256/512/1024 方图 | 1024²、1024×1792、1792×1024 | 1024²、1024×1536、1536×1024 |
| `quality` | `standard` | `standard`、`hd` | `low`、`medium`、`high`、`auto` |
| `style` | 不支持 | `vivid`、`natural` | 不支持 |
| `response_format` | 支持 | 支持 | **不支持，传了直接 400** |
| `n` | ≤10 | **只能 1** | ≤10 |

所以：

1. `OpenAiImageModelCapabilities.Resolve(model)` 按模型 id 返回可接受集合（含 `MaxCount`）。
   未知 id 视为第三方网关，返回宽松超集 —— 猜窄会弄坏能用的网关，猜宽最多换来看得懂的报错。
2. `ImageGenerationModelCapabilities` 随 `image-generation/list-models` 下发给渲染层，
   尺寸/画质/风格/数量控件全部由它生成，换模型时就地收敛（见 `useImageStudio` 的 `watch`）。
3. `OpenAiImageGenerationClient` 再校验一次：超范围抛可读的 `ArgumentException`/`ArgumentOutOfRangeException`，
   而不是把非法组合发给上游换一个 400。
4. `ImageGenerationQuality` 的取值是中立的线上词汇（`auto/low/medium/high/standard/hd`），
   由客户端在边界翻译成 SDK 的 `GeneratedImageQuality`；不存在「`Hd` 恰好等于 dall-e-3 的 hd」这种隐式绑定。

`response_format` 与 `style` **只在能力允许时赋值**：SDK 的 `ImageGenerationOptions` 里这两项为 null 时
不会出现在报文体中（已用抓包验证）。这是 gpt-image-1 能跑通的前提。

## 3. 长请求：超时与取消

生图是单次长请求，不是 token 流。

- **前端**：`useImageStudio` 对 generate/edit 显式传 `timeout: 6 分钟`。`hostBridge` 默认 30 秒对
  1792×1024 的渲染根本不够；超时只会让前端先放弃、后端继续烧钱，用户看不到结果也无法重试出结果。
- **后端**：`ImageGenerationService.ExecuteAsync` 用 `CancellationTokenSource.CreateLinkedTokenSource`
  + `CancelAfter(5 分钟)`，超时走 `TimeoutException` → 中文提示；**调用方取消仍原样抛
  `OperationCanceledException`**（`AGENTS.md` 代码约定 6），两者用
  `when (!cancellationToken.IsCancellationRequested)` 区分。
- **取消**：渲染层每次生成带 `jobId`，`image-generation/cancel` 按 id 命中桥接层的
  `ConcurrentDictionary<string, CancellationTokenSource>` 并 `Cancel()`。被用户取消的请求回
  `{ ok: false, cancelled: true }`，是一个已结算的结果而不是失败。
- **关闭期**：宿主 `CancellationToken` 触发时异常照旧上抛，不伪造失败结果。

## 4. 产物：URL 而不是 base64

图片字节写到 `{AppData}/generated-images/<yyyyMMdd>/<guid>.<ext>`，经 WebView2 虚拟主机
`https://generated-images.selfclaw.local/...` 提供给渲染层，与附件（`attachments.selfclaw.local`）同构。

- 一批 4 张 2 MB 的图不再以 base64（×1.37）穿过 `postMessage`，UI 线程不会因为复制十几兆字符串卡住；
- 渲染层只拿到 URL，**永远不接触文件系统路径**（`AttachmentBridge` 的同一条纪律）；
- 扩展名由魔数嗅探（PNG/JPEG/WEBP）决定，网关返回 JPEG 时不会硬标 `.png` 让虚拟主机发错 media type；
- 保留策略：每次生成前删掉日期目录里超过 7 天的整天目录。历史卡片若指向已被清掉的文件，
  `<img>` 的 `error` 事件会把它从列表里移除，不留破图。

渲染层的历史（id/url/prompt/时间）存在 `localStorage`（上限 60 条），所以切走视图、重启应用都不会丢；
真正的文件仍在磁盘上，URL 不变即可继续显示。

## 5. 桥接层的入参加固

对齐 `AttachmentBridge` 的既有做法，宿主不信任渲染层：

- base64 长度在解码前按 `AttachmentPathResolver.MaximumByteLength`（10 MB）预检；
- `Convert.FromBase64String` 的 `FormatException` 转成中文提示；
- `prompt` 上限 4000 字符、`count` 限定 1–10（再按模型 `MaxCount` 收敛）；
- 未知子类型抛 `ArgumentException`（与其他 bridge 一致），不再返回一个假的 error 对象；
- 错误分级：用户输入/配置类问题记 `LogWarning` 并原样回中文；未预期异常记 `LogError` 并加中文前缀。

## 6. 与聊天链路隔离

`list-models` 只回 `apiFormat == OpenAIImages` 的模型；反向的隔离在
`AiProviderSettingsService`：

- `ListEnabledModelsAsync`（composer 的模型下拉）按 `AiProviderApiFormatExtensions.IsChatFormat()` 过滤，
  生图模型不会出现在聊天模型列表里；
- `ResolveUsableModelAsync`（回合准入、自动化、压缩、子代理预检共用的「这个模型能跑吗」）
  同样要求 chat 格式，避免生图 profile 通过预检后在 `CreateChatClient` 里炸掉；
- 连通性检查对生图模型直接拒绝并提示去工作台验证 —— 用 chat ping 测生图模型只会永远失败。

## 7. 刻意不做

- **`turn_usage` 记账**：`turn_usage` 是 conversation + turn 维度的，生图没有会话也没有回合；
  为它伪造一条会话行会污染会话列表与用量口径。记账需要 schema 变更，而当前 schema 冻结在 v32
  （`AGENTS.md` 不变量 9：绝不迁移）。因此生图费用目前**不进**用量仪表盘，等 schema 升版时再补
  「非会话型开销」表。
- **mask / 图生图蒙版**：协议支持 `mask`，但工作台没有画蒙版的入口。留着一条没有调用方的通路
  等于死代码，等真有入口时再加。
- **`response_format=url`**：字节模式是唯一需要的形状（要落盘）。若网关无视请求直接返回 URL，
  桥接层原样透出该 URL（不缓存，因为不是我们的文件）。
