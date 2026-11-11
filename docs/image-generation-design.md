# 生图工作台设计

OpenAI Images 协议（`AiProviderApiFormat.OpenAIImages`）在 SelfClaw 里的落地：协议适配、能力协商、
长请求的超时与取消、产物落盘与保留。

## 1. 分层

| 层 | 文件 | 职责 |
| --- | --- | --- |
| Core | `Models/ImageGeneration/*`、`Interfaces/ImageGeneration/*` | 纯 DTO 与 4 个方法的契约 |
| Infrastructure | `ImageGeneration/ImageGenerationService` | 解析 profile → connection → 凭据，建 client，管超时 |
| Infrastructure | `ImageGeneration/OpenAiImageGenerationClient` | 请求形状、按模型能力裁剪参数、结果映射 |
| Infrastructure | `ImageGeneration/OpenAiImageModelCapabilities` | 模型 → 可接受参数集的唯一事实来源 |
| Infrastructure | `ImageGeneration/GeneratedImagePathResolver` | 产物目录布局与虚拟主机 URL |
| Desktop | `Services/ImageGeneration/ImageGenerationBridge` | `image-generation/*` 协议、入参加固、落盘、取消 |
| Vue | `composables/useImageStudio.js` + `components/image-generation/*` | 工作台 UI，控件由能力驱动 |

出站一律走 `AiProviderHttpClientProvider.CreateTurnClient`（`AGENTS.md` 不变量 13），
凭据只在 Infrastructure 内解密（不变量 1）。

## 2. 能力协商：为什么不能一套参数打天下

同一个 OpenAI Images 协议下，模型家族对同一组字段的要求并不一致，用一套写死的参数打天下必然 400。
当前只精确识别 **`gpt-image` 家族**（含 `gpt-image-1`、`gpt-image-1-mini`、`gpt-image-1.5`、
`gpt-image-2`/`2.5`/`2.6` 系列与 `chatgpt-image-latest` 等按 `Contains("gpt-image")` 命中的变体）。
**整个家族统一一套增强能力**，不按 2.5 / 2.6 细分：文档里 2.5 系列专属的 `xhigh`/`max` 与自定义尺寸
对全家族开放，代价是 2.6 上可能选到上游不认的档位，由上游的可读报错兜底。其他模型 id 一律视为
第三方网关，走保守的旧参数集：

| 字段 | `gpt-image` 家族 | 其他 id（网关兜底） |
| --- | --- | --- |
| `prompt` 上限 | **32000 字符** | **1000 字符**（`DefaultMaxPromptLength`） |
| `size` | `auto` + 10 个预设（1:1 / 3:2 / 2:3 / 16:9 / 9:16 / 4:3 / 3:4 / 2:1 / 1:2），另允许自定义 `WIDTHxHEIGHT` | 7 个旧预设，无自定义 |
| `quality` | `auto`、`low`、`medium`、`high`、`xhigh`、`max` | 6 档旧词汇全量 |
| `style` | 不支持 | `vivid`、`natural` |
| `background` | `auto`、`transparent`、`opaque` | 不支持（不下发控件） |
| `output_format` | `png`、`jpeg`、`webp` | 不支持（不下发控件） |
| `output_compression` | 0–100，仅 jpeg/webp 生效 | 不支持 |
| `moderation` | `auto`、`low`（仅生成，编辑无此字段） | 不支持 |
| `response_format` | **不支持，传了直接 400** | 从不发送（见下） |
| `n` | ≤10 | ≤10 |

自定义尺寸约束（`ImageGenerationCustomSizeRules.GptImage`）：宽高均为 **16 的倍数**、宽高比在
**1:3 ~ 3:1** 之间、最高 **3840×2160**。前端与宿主用同一组规则各校验一次（宿主不信任渲染层），
SDK 侧用 `new GeneratedImageSize(w, h)` 序列化成 `"WxH"`。

`background=transparent` 需要带 alpha 的输出格式：前端在透明背景下摘掉 `jpeg` 选项并把残留的
`jpeg` 收敛回 `png`，`OpenAiImageGenerationClient` 再独立校验一次，避免把必然 400 的组合发出去。

所以：

1. `OpenAiImageModelCapabilities.Resolve(model)` 按模型 id 返回可接受集合。命中 `gpt-image` 用增强集；
   **未知 id 返回旧参数超集并禁用全部 GPT Image 专属旋钮** —— 猜窄会弄坏能用的网关，把 GPT 专属
   字段发给任意网关也容易踩 400。要接回一个已移除的家族，只需在这里加一条分支和一份预设，前端无需改动。
2. `ImageGenerationModelCapabilities` 随 `image-generation/list-models` 下发给渲染层，
   尺寸/画质/风格/背景/输出/过滤/数量/**prompt 上限**控件全部由它生成，换模型时就地收敛
   （见 `useImageStudio` 的 `watch`）。能力契约新增字段都是可选带默认值的，旧网关不填即为空数组/false/null。
3. `OpenAiImageGenerationClient` 再校验一次：超范围抛可读的 `ArgumentException`/`ArgumentOutOfRangeException`，
   而不是把非法组合发给上游换一个 400。预设尺寸与自定义尺寸共用 `WIDTHxHEIGHT` 解析 +
   `new GeneratedImageSize(w, h)`，不再走固定常量的 switch（抓包确认 SDK 会原样序列化任意 `WxH`），
   因此往能力表里加一个比例就是完整的端到端改动，无需再碰客户端。
4. `ImageGenerationQuality` 的取值是中立的线上词汇超集（`auto/low/medium/high/standard/hd/xhigh/max`），
   由客户端在边界翻译成 SDK 的 `GeneratedImageQuality`；`xhigh`/`max` 走 SDK 的可扩展字符串构造
   （抓包确认序列化为 `quality: "xhigh"`）。某个模型实际接受哪一档由能力表决定，
   不存在「枚举值恰好等于某模型的取值」这种隐式绑定。

**报文字段的裁剪**：`style` 只在 `Styles.Count > 0` 时才赋值；背景/输出格式/压缩/moderation 只在
能力清单允许时赋值；`response_format` 则**从不赋值**——SDK 的 `ImageGenerationOptions` 里该项为 null
时不会出现在报文体中（已用抓包验证），工作台始终只收字节。这是 `gpt-image` 家族能跑通的前提。

**prompt 上限不下放到前端硬编码**：`ImagePromptSection` 的 `maxlength` 和字数计数都取
`capabilities.maxPromptLength`，切到网关模型时自动变回 1000 并裁掉超长文本。宿主
`ImageGenerationBridge` 提交前用 `IImageGenerationService.GetCapabilitiesForProfileAsync(profileId)`
解析**同一次请求真正会用的 profile** 再校验，保证「按 A 的能力校验、实际用 B 执行」不会漂移；
未带 profileId 时该接口复用 `ResolveProfileAsync` 的默认选择逻辑。

## 3. 长请求：超时与取消

生图是单次长请求，不是 token 流。

- **前端**：`useImageStudio` 对 generate/edit 显式传 `timeout: 6 分钟`。`hostBridge` 默认 30 秒对
  1536×1024 的渲染根本不够；超时只会让前端先放弃、后端继续烧钱，用户看不到结果也无法重试出结果。
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
- `prompt` 上限**按所选模型能力动态取值**（GPT Image 32000 / 网关 1000），不再是固定 4000；
  超限提示里带具体上限数字；
- `count` 限定 1–10（再按模型 `MaxCount` 收敛）、`size` 限 32 字符（自定义尺寸最长 `"3840x2160"`）；
- 枚举参数走 `ReadOptionalEnum`，非法取值在宿主侧拒绝，不交给 SDK 猜；
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
- **`partial_images`**：SDK 2.13 / 2.15 都只在流式事件的内部类型里支持中间图，非流式
  `ImageClient.GenerateImagesAsync` 没有公开入口；工作台本身是单次长请求，也不展示中间态。
  补它等于绕开 SDK 自己构造流式请求，收益为零。
- **`safety_identifier`**：SDK 只暴露 `EndUserId`，且它序列化成**已废弃的 `user`** 字段，与文档要求
  的 `safety_identifier` 不是同一个东西。应用侧也没有稳定的终端用户标识来源；等 SDK 提供公开 API
  且产品确定身份口径后再接。
- **按代际细分能力**：文档里 2.5 与 2.6 的参数集有差异，但当前全家族共用一套增强能力，
  用上游报错兜底非法组合。若后续需要精确收敛，在 `Resolve()` 里按更具体的 id 前缀加分支即可，
  前端无需改动（这正是能力驱动 UI 的收益）。
