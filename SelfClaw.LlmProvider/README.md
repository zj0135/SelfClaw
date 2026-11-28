# SelfClaw.LlmProvider

一个独立的 LLM 客户端库：给一个已经解析好的连接 + 模型 + 凭据，返回
Microsoft.Extensions.AI 对象（`IChatClient` / `IImageGenerator` / `IEmbeddingGenerator`）。

它不认识 SelfClaw。不读配置、不碰数据库、不加密、不拼路径、不注册 DI、不算账。

## 目录

- [依赖预算](#依赖预算)
- [布局：目录即能力，能力内分三层桶](#布局目录即能力能力内分三层桶)
- [快速开始](#快速开始)
- [能力详解](#能力详解)
  - [Connections — 连接与凭据](#connections--连接与凭据)
  - [Profiles — 模型档案](#profiles--模型档案)
  - [Parameters — 厂商专有参数表](#parameters--厂商专有参数表)
  - [Vendors — 厂商目录](#vendors--厂商目录)
  - [Protocols — 协议引擎](#protocols--协议引擎)
  - [Chat — 聊天共享层](#chat--聊天共享层)
  - [Transport — 出站 HTTP](#transport--出站-http)
  - [Quirks — 请求形状调整](#quirks--请求形状调整)
  - [Client — 入口与租约](#client--入口与租约)
  - [Errors — 异常](#errors--异常)
- [加一个厂商：只写 JSON](#加一个厂商只写-json)
- [加一个协议：写一个引擎](#加一个协议写一个引擎)
- [不负责什么](#不负责什么)
- [公开面](#公开面)

## 依赖预算

| 包 | 为什么 |
|---|---|
| `Microsoft.Extensions.AI.OpenAI` | 带来 OpenAI SDK 与 M.E.AI 抽象，并提供 `AsIChatClient` / `AsIImageGenerator` / `AsIEmbeddingGenerator` |
| `Anthropic` | Messages 协议官方 SDK |

TFM 是 `net10.0`。刻意不引用 `Microsoft.Extensions.AI`（元包）、`DependencyInjection`、
`Hosting`、`Configuration`、`Microsoft.Data.Sqlite`、`ProtectedData`、`Microsoft.ML.Tokenizers`。

这不是承诺而是断言：`LlmProviderStandaloneTests` 用反射检查程序集的引用集合。

## 布局：目录即能力，能力内分三层桶

一个能力一个目录，目录内部再按 **接口 / 数据 / 实现** 分桶，绝不放一块：

| 桶 | 只放 |
|---|---|
| `Abstractions/` | 接口 |
| `Dtos/` | record / enum / 常量（纯数据） |
| `Implementations/` | 有行为的类（`internal` 与 `public` 都算） |

```
SelfClaw.LlmProvider/
├── Connections/       Dtos
├── Profiles/          Dtos  Implementations
├── Parameters/        Dtos  Implementations
├── Vendors/           Dtos  Implementations
├── Protocols/         Abstractions  Dtos  Implementations
│   ├── OpenAiChat/            单一实现单元
│   ├── OpenAiResponses/       单一实现单元
│   ├── AnthropicMessages/     单一实现单元
│   ├── OpenAiImages/          单一实现单元
│   └── OpenAiEmbeddings/      单一实现单元
├── Chat/              Dtos  Implementations
├── Transport/         Abstractions  Implementations
├── Quirks/            Abstractions  Dtos  Implementations
├── Client/            Abstractions  Dtos  Implementations
└── Errors/            同质叶子：只有异常类型，不再分桶
```

三个例外，由测试表达而不是靠自觉：

- `Protocols/<协议名>/` 是**单一实现单元**（一个协议一套实现），内部不再分桶；
- `Errors/` 是**同质叶子**，只含 `: Exception`，不分桶；
- 能力目录自身不直接放 `.cs`，只放桶。

依赖方向单向、无环：

```
Connections  Profiles  Errors             (叶子)
      ↓          ↓
Parameters ← Vendors ← Quirks ← Transport
      ↓          ↓
    Chat ← Protocols
      ↓          ↓
         Client
```

命名空间跟着目录走（`SelfClaw.LlmProvider.Client.Implementations` 等）。这条路更长，但
`using SelfClaw.LlmProvider.Client.Implementations;` 比一条什么也不说明的
`using SelfClaw.LlmProvider;` 信息量大得多。

`LlmProviderLayoutTests` 钉住 8 条约定：根目录无 `.cs`、能力目录不直接放 `.cs`、
`Abstractions` 只放接口、`Dtos` 不放行为、协议子目录不含接口、目录=命名空间、
一个文件只声明一个类型、不存在 token/成本类型。

## 快速开始

库本身不带任何厂商数据（原因见 [Vendors](#vendors--厂商目录)），所以先给它一份目录：

```csharp
var vendors = new LlmVendorCatalog()
    .AddFromDirectory(Path.Combine(AppContext.BaseDirectory, "Vendors"))
    .AddFromDirectory(Path.Combine(appData, "providers"));   // 用户自定义，覆盖前者

var factory = new LlmClientFactory(vendors);
```

然后就是一个连接、一个模型：

```csharp
using SelfClaw.LlmProvider.Client.Implementations;   // LlmClientFactory
using SelfClaw.LlmProvider.Connections.Dtos;         // LlmConnection, LlmCredential
using SelfClaw.LlmProvider.Profiles.Dtos;            // LlmModel
using SelfClaw.LlmProvider.Protocols.Dtos;           // LlmProtocols
using SelfClaw.LlmProvider.Vendors.Implementations;  // LlmVendorCatalog

var connection = new LlmConnection(
    VendorId: "openai",
    Endpoint: new Uri("https://api.openai.com/v1/"),
    Credential: new LlmCredential(LlmCredentialKind.Bearer, apiKey));

var model = new LlmModel("gpt-5", LlmProtocols.OpenAiChat);

using var lease = factory.CreateChatClient(connection, model);
await foreach (var update in lease.Client.GetStreamingResponseAsync(
                   [new ChatMessage(ChatRole.User, "你好")], lease.Options))
{
    Console.Write(update.Text);
}
```

`lease.Options` 已经带好采样参数、输出上限与工具绑定。释放租约释放这回合的管道，
但不会碰你交给 transport 的共享 handler。

### 带工具

```csharp
var tools = new[] { AIFunctionFactory.Create((string path) => File.ReadAllText(path), "read_file") };
var options = new LlmRequestOptions(Tools: tools, StreamIdleTimeout: TimeSpan.FromMinutes(10));

using var lease = factory.CreateChatClient(connection, model, options);
```

`StreamIdleTimeout` 是"两次更新之间最长静默"。超时当失败抛 `TimeoutException`，
不是取消——只有你自己的 token 能取消。

### 图像与向量

```csharp
var imageModel = new LlmModel("gpt-image-1", LlmProtocols.OpenAiImages);
using var imageLease = factory.CreateImageGenerator(connection, imageModel);
var result = await imageLease.Generator.GenerateAsync(
    new ImageGenerationRequest("一只橘猫"),
    new ImageGenerationOptions { MediaType = "image/png" });

var embeddingModel = new LlmModel("text-embedding-3-small", LlmProtocols.OpenAiEmbeddings);
using var embeddingLease = factory.CreateEmbeddingGenerator(connection, embeddingModel, dimensions: 512);
var vectors = await embeddingLease.Generator.GenerateAsync(["hello"]);
```

### 列出模型

```csharp
var models = await factory.ListModelsAsync(connection);
```

厂商描述符决定用哪套清单协议。没声明 `modelListing` 时抛 `NotSupportedException`。

### 只看选项、不建客户端

```csharp
var chatOptions = factory.CreateChatOptions(connection, model, options);
```

## 能力详解

### Connections — 连接与凭据

"打给谁、用什么身份"。

| 类型 | 桶 | 职责 |
|---|---|---|
| `LlmConnection` | Dtos | 厂商 id + 端点 + 已解密凭据 + 连接级选项 + 超时 |
| `LlmCredential` | Dtos | 一条明文凭据 + 放置方式 |
| `LlmCredentialKind` | Dtos | `Bearer` / `Header` / `Query` |

**为什么凭据是明文。** 本库不解密、不存储、不记录凭据，它收到的就是可以直接放到请求上的值。
密钥从哪来（DPAPI、keychain、环境变量、明文配置）是调用方的决定；把"怎么存"抽进库会立刻
把库变成 Windows 专用，或者逼库替调用方选一个存储方案。

`LlmCredentialKind` 决定它放在哪：`Authorization: Bearer`、某个自定义头（如 `api-key`）、
或查询参数。厂商描述符的 `auth` 声明默认方式，连接自己携带的那份是实际值。

**连接级选项。** `Options` 是自由 JSON 袋，供厂商描述符以 `connection:<key>` 引用。
典型用途是 Azure 的 `deployment` 名（`path-prefix` quirk 的占位符来源）与组织/项目标识。

**不做什么。** 不保存连接（没有仓储、没有表、没有文件）；不校验可达性——端点是否是合法
URI 会检查（当场可判），端点是否可达不检查（那是网络请求）。

### Profiles — 模型档案

"用哪个模型、它有什么限制"。

| 类型 | 桶 | 职责 |
|---|---|---|
| `LlmModel` | Dtos | 模型 id + 协议 + 采样 + 自由选项袋 + 思考等级 + 能力上限 |
| `LlmSampling` | Dtos | `Temperature` / `TopP`，都是 `double?`（null = 不发送） |
| `LlmModelLimits` | Dtos | 上下文窗口、输出上限、是否多模态 |
| `LlmModelDescriptor` | Dtos | 模型清单里的一条（id、展示名、窗口、各样价格） |
| `LlmModelListReader` | Impl | 内部：读 OpenAI 与 Anthropic 两种清单线格式 |

**为什么采样是可空 double。** 调用方的设置界面往往要区分"用户没启用这个参数"和"用了某个
默认值"，那是界面的表达问题。库里只关心一件事：这个字段要不要出现在请求里。

**为什么只有清单条目带价格。** `LlmModelDescriptor` 的价格字段是 **provider 线上数据**：
OpenRouter 那类端点确实在 `/models` 响应里返回每百万 token 价，本库只是把它读出来交给调用方。
而"按用量算钱"不是 provider 能力——它是调用方的账务策略，且已有 M.E.AI 的 `UsageDetails`
提供计数。所以库里没有 `TokenUsage`、没有成本计算器。

**`HasTypedConfiguration` 是什么。** 调用方是否已经为这个模型存过类型化配置（思考等级、
采样、上限）。参数表里标了 `gateLegacySources` 的字段会据此跳过自由选项袋——这样界面上设过的
思考等级不会被一条手写的旧线参数盖掉。传这个标志而不是让库去猜配置从哪来，是"库不读配置"
的直接代价，也是它的价值。

### Parameters — 厂商专有参数表

整个库最重要的扩展点：**加一个厂商不需要写代码，只需要写数据。**

| 类型 | 桶 | 职责 |
|---|---|---|
| `LlmVendorParameter` | Dtos | 一条参数：路径、类型、来源链、值映射、常量、旧来源门控 |
| `LlmVendorValueType` | Dtos | `String` / `Boolean` / `Integer` / `Number` |
| `LlmOptionResolver` | Impl | 内部：按来源链求值，产出"路径 → JSON 值" |
| `ModelOptionReader` | Impl | 内部：以"告警并跳过"的策略读自由选项袋 |
| `LlmParameterReader` | Impl | 内部：引擎侧按路径回读已求值的结果 |

一条参数行 = "把按来源链解析出的值，写到请求体的某个 JSON 路径"。参数表是**厂商数据**，
由调用方在描述符里提供（见 [Vendors](#vendors--厂商目录)）；库只实现求值机制。

**来源链**按序求值，第一个有值的胜出：

| 来源 | 读什么 |
|---|---|
| `reasoning.effort` | `LlmModel.ReasoningEffort` |
| `request.enableReasoning` | 由 `ReasoningEffort` 派生的布尔，总是有值 |
| `limits.multimodal` | `LlmModel.Limits.IsMultimodal` |
| `option:<key>` | `LlmModel.Options[key]` |
| `connection:<key>` | `LlmConnection.Options[key]` |
| `value` | 参数行自带的常量（此时忽略来源链） |

**为什么这一层能取代 if 链。** 原来的做法是在适配器里堆条件分支，每加一个厂商就多一段
`if (providerKind == ...)`。一个字段的优先级一旦被写进三段不同的 `if`，它们就会慢慢漂移
（现实中已经漂了：`thinking.type` 被配置门控、`store` 没有）。参数表把这些规则变成**同一份
数据的不同行**，优先级只有一处定义。

代价是表达力有上限：它只能改"已存在请求对象的某些字段"。改请求形状走
[Quirks](#quirks--请求形状调整)。

**`gateLegacySources`。** 为 true 时，`option:` 与 `request.enableReasoning` 只在模型**没有**
类型化配置时参与。这就是原代码里 `Configuration is null &&` 那半句语义，保留它是因为行为变更
不该混在重构里。

**边界。** 参数表只在 **OpenAI 族协议**上生效——它们的请求体支持按 JSON 路径打补丁。
`anthropic-messages` 走 SDK 的强类型参数，暂不接受厂商专有字段。

### Vendors — 厂商目录

"哪一家、支持哪些协议、怎么认证、模型从哪列、以及它自己的参数表"。

**厂商是数据，不是代码。但数据不归本库所有。**

| 类型 | 桶 | 职责 |
|---|---|---|
| `LlmVendorDescriptor` | Dtos | 一家厂商：协议集合、认证方式、清单方式、厂商级 quirks |
| `LlmVendorProtocol` | Dtos | 某个协议在这家厂商上的配置（参数表 + 协议级 quirks） |
| `LlmVendorModelListing` | Dtos | 清单线格式（`openai` / `anthropic`）+ 相对路径 |
| `LlmVendorCatalog` | Impl | 不可变目录：从 JSON 流/文本/目录或已构造的描述符加入 |
| `LlmVendorJson` | Impl | 内部：描述符 JSON → 对象 |

**库里没有任何厂商。** `new LlmVendorCatalog()` 是空的。库不内嵌描述符、不提供
`CreateDefault()`、不带 `openai` / `anthropic` / `custom`。理由按重要性排：

1. **重复的事实来源。** 一个同时拥有设置 UI 的应用必然已经有一份厂商清单（它是产品配置：
   显示哪几家、叫什么、什么颜色）。库再嵌一份，同一件事就有两处定义，迟早不一致。
2. **版本纠缠。** 厂商的线格式会变（新字段、改字段名）。数据在库里的话，改一个字段要发版；
   数据在调用方，改一行 JSON。
3. **展示字段不是库的事。** `displayName` / `subtitle` / `accentColor` 是产品决定。
   库把它们**透传**（不多不少地携带、不消费），但绝不替调用方挑选。

**怎么给数据。** `AddFromDirectory` 读该目录下所有 `*.json`，按文件路径排序加载，
同 `vendorId` 以后出现的为准；目录不存在时保持空目录而不是报错——首次运行没有用户厂商是
正常状态。也可以用 `AddFromJson(stream|string)` 或 `Add(LlmVendorDescriptor)`。

**描述符要求。** 文件名与 `vendorId` 一致（便于排查）；至少声明一个协议；`defaultEndpoint`
必须是绝对 URI。任一不满足，`AddFromJson` 立刻抛 `LlmVendorCatalogException`，并指出是
哪家厂商的哪个字段。

**协议集合就是"这家支持哪些接口"。** `Protocols` 字典的键集合即答案。同一家厂商声明
`openai-chat` + `anthropic-messages` 是合法的，每个协议各自带参数表——因为同一家厂商在不同
线格式下的专有字段本来就不同。

**不做什么。** 不拼任何路径（`%AppData%`、`~/.config`、当前目录都是调用方的决定）；
不读环境变量或应用配置（目录是构造参数）；不写文件；不挑默认厂商（没有厂商时 `GetRequired`
抛一条错误，告诉调用方怎么加）。

### Protocols — 协议引擎

"这条线格式怎么发"。一个协议一个引擎，与厂商无关。

| 类型 | 桶 | 职责 |
|---|---|---|
| `ILlmProtocolEngine` | Abstractions | 一个协议的实现：造聊天客户端、造 ChatOptions、造图像/向量生成器 |
| `LlmProtocolContext` | Dtos | 造对象需要的一切：连接、模型、厂商、运行时选项、**已求值的参数表** |
| `LlmProtocols` | Dtos | 五个内置协议 id 的常量 |
| `LlmProtocolRegistry` | Impl | 按协议 id 索引；重复注册在构造期失败 |

五个内置：

| id | 产出 | 实现目录 |
|---|---|---|
| `openai-chat` | `IChatClient`（Chat Completions） | `OpenAiChat/` |
| `openai-responses` | `IChatClient`（Responses） | `OpenAiResponses/` |
| `anthropic-messages` | `IChatClient` | `AnthropicMessages/` |
| `openai-images` | `IImageGenerator` | `OpenAiImages/` |
| `openai-embeddings` | `IEmbeddingGenerator<string, Embedding<float>>` | `OpenAiEmbeddings/` |

图像与向量不自己写适配层：`Microsoft.Extensions.AI.OpenAI` 已提供
`ImageClient.AsIImageGenerator()` 与 `EmbeddingClient.AsIEmbeddingGenerator(dimensions)`，
所以这两个引擎各只有约 20 行——调用方拿到的就是框架类型。

**协议是字符串不是枚举。** 枚举无法被外部项目扩展，想加协议只能 fork。用字符串 +
`LlmProtocols` 常量：既能编译期查错（常量拼错会报错），又能让调用方注册自己的协议 id。
失去的 switch 穷尽性由注册表补上：协议重复杂交即抛，未知协议查找即抛。

**各协议的两个差异点，值得知道：**

- `openai-chat` 的输出上限必须设在 `ChatOptions` 上，因为 M.E.AI 会把它映射成
  `max_completion_tokens`，而这个格式没有可用的 raw 补丁字段。
- `openai-responses` 相反：raw 值优先于 `ChatOptions.MaxOutputTokens`，所以三级兜底
  必须在 raw options 里落地；而且**思考只由显式选项驱动**，绝不从"是否启用了推理"派生，
  否则会给不支持该参数的模型塞进非法字段。
- `anthropic-messages` 总是显式给输出上限：SDK 自带 4096 的默认值会把普通回答截断。

**两个"补偿客户端"。** 它们都是**框架当前版本的缺陷补偿**，不是协议逻辑：

| 类型 | 补偿什么 | 证据 |
|---|---|---|
| `OpenAiChat/OpenAiChatReasoningReplayClient` | `AsOpenAIChatMessages()` 丢弃 `TextReasoningContent` | 实测：带思考的助手消息序列化后只剩 `{"role":"assistant","content":"..."}`，连 `reasoning_content` 字段都没有；打补丁后才出现 |
| `OpenAiResponses/OpenAiResponsesReplayClient` | `FunctionCallContent.RawRepresentation` 为 null | 实测：`StreamingResponseOutputItemDoneUpdate` 上带完整原生 item，内层 call 的 `RawRepresentation` 却是 null |

第一条的后果是确定的：多轮对话里模型看不到自己上一轮的思考内容。
第二条只证明了"信息在这里、SDK 没接上"，**尚未证明不接会真的出错**，属于防御性保留。

**它们是有保质期的代码。** `Directory.Packages.props` 把 `Microsoft.Extensions.AI.OpenAI`
钉在 10.10.0。升级该包时必须重新核对这两处：

1. 若官方修好了 → **直接删除**这两个类以及 `OpenAiChatEngine` / `OpenAiResponsesEngine`
   里对它们的包装（`new OpenAiChatReasoningReplayClient(client.AsIChatClient())` 退化成
   `client.AsIChatClient()`）。
2. 若官方换了 API 形态 → 不要"修修补补"，按新版本重写或删掉。

不要因为"它们现在能编译"就一直留着——这类补偿代码长期存活会变成没人敢动的黑箱。

### Chat — 聊天共享层

与协议无关、但只在聊天路径上用到的部分。

| 类型 | 桶 | 职责 |
|---|---|---|
| `LlmImageToolDelivery` | Dtos | 三种形状：`Unsupported` / `Native` / `FollowUpUserMessage` |
| `LlmImageToolCapability` | Dtos | 工具结果能不能带图、以什么形状带 |
| `LlmChatOptions` | Impl | 构建所有协议共享的 `ChatOptions`：采样、输出上限、工具绑定 |
| `IdleGuardingChatClient` | Impl | 流式响应的闲置守护 |

**输出上限的三级兜底。** `LlmChatOptions.ResolveMaxOutputTokens` 的优先级是：
`LlmModel.Limits.MaxOutputTokens`（类型化配置）→ `$max_output_tokens` 选项 →
模型清单写入的 `display.maxOutputTokens`。留空等于把决定权交给 provider，而有些 provider
的默认值低到会把普通回答中途截断（Anthropic 集成默认 4096）。这个兜底不是优化，是正确性。

**闲置守护为什么要单独一层。** 流式请求不能设总超时——健康的长回答可以流很多分钟。真正标记
"流已经死了"的是**两次更新之间的间隔**。所以限制的是间隔，而且超时当失败抛
`TimeoutException`，不是取消：只有调用方自己的 token 才能以取消结束一个流。这一层是
`DelegatingChatClient` 而不是 handler，因为它要看的是"M.E.AI 多久没吐出下一个 update"，
而不是"netty 多久没收到字节"。

**图片投递为什么是协议的事实。** 模型自己的多模态声明是唯一的显式信号，然后**协议**决定形状：

| 协议 | 形状 |
|---|---|
| `anthropic-messages` | 工具结果消息直接带图片 |
| `openai-responses` | 同上 |
| `openai-chat` | 图片必须作为紧随工具消息之后的独立用户消息 |

未验证的协议保持 `Unsupported`——猜错会把 base64 当成一个巨大 JSON 字符串发给对方。
`UnavailableReason` 让调用方能直接展示原因，不必匹配错误文案。

### Transport — 出站 HTTP

"请求从哪条线路出去"。代理、连接池、证书、测试替身都是调用方的决定。

| 类型 | 桶 | 职责 |
|---|---|---|
| `ILlmTransport` | Abstractions | 唯一方法：返回当前出口的共享 handler |
| `LlmTransports` | Impl | 入口：`Default`（跟随系统代理）、`FromHandler(handler)` |
| `DefaultLlmTransport` | Impl | `SocketsHttpHandler`，连接池 5 分钟 |
| `FixedHandlerTransport` | Impl | 把一个现成 handler 交出去 |
| `LlmHttpPipeline` | Impl | 内部：handler + quirk 链 + 薄 `HttpClient` |
| `ProviderHttpResponses` | Impl | 内部：非成功状态带响应体摘要的可读错误 |

**为什么给 handler，不给 HttpClient。** 官方 SDK 会 dispose 传进去的 `HttpClient`
（Anthropic 的 `ClientOptions.DisposeHttpResources` 明确如此）。更糟的是共享实例上这几样
都改不了：

| 每连接需要 | 共享 client 上的后果 |
|---|---|
| `BaseAddress` | 一个 client 只有一个；改它是并发写竞态 |
| `Timeout` | SDK 文档明确警告会与自己超时冲突、导致提前取消 |
| 连接级头（`extra_headers`） | 只能改 `DefaultRequestHeaders`，同样竞态 |

`HttpClient` 只是 handler 上的一层薄包装——共享它没有收益，只有所有权歧义。
`SocketsHttpHandler` 才是真正持有连接池与代理配置的对象。所以：**共享 handler，不要共享
client。** 库每回合做 `new HttpClient(handler, disposeHandler: false)`，从不 dispose 这个
handler。

**为什么没有 `FromHttpClient`。** `HttpClient` 不暴露内部 handler，因此无法把它接进 quirk
链（认证放置、路径前缀、查询参数都挂在 handler 上）。要复用已有 `HttpClient`，请交出它的
handler：`LlmTransports.FromHandler(myHandler)`。

**谁用它。** 不只是聊天：模型清单请求也走同一条 pipeline，所以代理设置对所有出站统一生效。

**默认行为。** `LlmTransports.Default` 跟随系统代理并使用默认凭据
（`UseProxy = true, Proxy = null`）。这是 .NET 的原生语义——要"直连"必须显式
`UseProxy = false`，否则那个模式会变成空操作。

**自定义出口：**

```csharp
public sealed class NetworkProxyTransport(INetworkProxySource source) : ILlmTransport
{
    public HttpMessageHandler GetHandler() => BuildProxyHandler(source.Current);
}

var factory = new LlmClientFactory(vendors, new NetworkProxyTransport(proxySource));
```

### Quirks — 请求形状调整

参数表能改"已存在请求对象的字段"，改不了**请求形状**。这一层补那部分。
库只提供**机制**与三个通用实现；针对某家网关的具体补偿由调用方实现。

| 类型 | 桶 | 职责 |
|---|---|---|
| `ILlmRequestQuirk` | Abstractions | 一个具名调整：包装 handler |
| `LlmQuirkRef` | Dtos | 描述符里对某个 quirk 的一次引用（可带自己的参数） |
| `LlmQuirkContext` | Dtos | 调整需要的一切：连接 + 该条目声明的参数 |
| `LlmQuirkRegistry` | Impl | 按 id 索引；重复杂交即抛 |

**为什么在 handler 层。** `ILlmTransport` 是唯一造 `HttpClient` 的地方，OpenAI SDK
（`HttpClientPipelineTransport`）、Anthropic SDK（`ClientOptions.HttpClient`）、模型清单
三路全都经它。挂在 handler 上意味着：对所有协议统一生效、不需要每个引擎各写一遍、
不泄漏任何 SDK 类型到 quirk 实现里、厂商级与协议级可以叠加。

顺序：`transport.GetHandler()` → 厂商级 quirks（按声明序）→ 协议级 quirks（按声明序）。
越靠后的越先看到真实网络流。

**内置三个**（都是通用的形状调整）：

| id | 作用 | 典型用途 |
|---|---|---|
| `header-auth` | 自定义认证头 | Azure 的 `api-key: ...` |
| `query` | 静态查询参数 | `api-version=2024-10-21` |
| `path-prefix` | 在路径前插前缀 | `/openai/deployments/{deployment}` |

`{deployment}` 这类占位符从 `LlmConnection.Options` 就地取值。这三个之所以在库里：
它们是**协议形状的变体**，参数化、各自 20~80 行、不绑定任何特定厂商——任何实现 Azure
风格端点的人都需要同样的三件事。

**不在库里的：脏流归一。** 有一类 quirk 不在这里：把某些兼容网关的畸形 SSE 帧改成 SDK 与
M.E.AI 能接受的样子。它曾作为内置 `stream-normalize` 存在（约 360 行），已移除。理由：

1. **它是使用方对自家网关的了解，不是协议的一部分。** 符合规范的端点永不产生这些帧，
   所以对标准提供商的使用者是纯粹冗余。
2. **它编码的是特定网关的 bug。** 哪家网关吐什么畸形帧，只有接那家网关的人知道；
   库替他们猜，猜对了是运气，猜错了是静默丢数据。
3. **一个 quirk 有 360 行，说明它不是"配置"而是"实现"。** 这类实现应当跟着了解它的人走。

如果你的网关确实需要，实现 `ILlmRequestQuirk` 并注册即可。三个常见症状：

| 症状 | 原因 |
|---|---|
| 解析流时抛 `Unknown ChatToolCallKind` | `tool_calls[].type` 缺失或为空字符串 |
| 思考内容完全不见 | 思考文本以 `reasoning` / `reasoning_text` / `reasoning_details` 到达，而 M.E.AI 只读 `reasoning_content` |
| 每一帧都被读成拒绝 | 网关在每帧都盖上 `"refusal": ""`，M.E.AI 当成真的 refusal |

做法是包一层 `DelegatingHandler`，在响应 `Content-Type` 是 `text/event-stream` 时把内容流
换成一个逐行改写 `data:` 帧的 `Stream`；其他字节原样透传。

**加一个 quirk：**

```csharp
public sealed class MyQuirk : ILlmRequestQuirk
{
    public string Id => "my-quirk";

    public HttpMessageHandler Wrap(HttpMessageHandler inner, LlmQuirkContext context) =>
        new MyHandler(inner);   // 记得把 context.Parameters 用上
}

var factory = new LlmClientFactory(
    vendors,
    quirks: LlmQuirkRegistry.CreateDefault().With(new MyQuirk()));
```

然后在描述符里引用：

```json
{ "vendorId": "some-gateway", "quirks": [{ "id": "my-quirk", "parameters": { "mode": "loose" } }] }
```

未注册的 id 在**发请求前**就抛 `LlmProviderException`，不会等到流读到一半才发现。

**与参数表的分工：**

| 需求 | 用哪个 |
|---|---|
| 请求体里多一个/改一个字段 | 参数表 |
| 换认证头、改路径、加查询参数、改响应流 | quirk |
| 整条线格式都不同 | 协议引擎 |

### Client — 入口与租约

调用方唯一需要 `new` 的东西：把"已解析的连接 + 模型 + 凭据"变成一组可用的 M.E.AI 对象，
并管好它们的释放。

| 类型 | 桶 | 职责 |
|---|---|---|
| `ILlmClientFactory` | Abstractions | 五个入口：聊天客户端、聊天选项、图像生成器、向量生成器、模型清单 |
| `LlmRequestOptions` | Dtos | 一次调用可选的运行时输入：工具、日志工厂、流的闲置超时 |
| `LlmClientFactory` | Impl | 默认实现。只做装配：查厂商、查协议引擎、求值参数表、建一层薄 HttpClient |
| `LlmClientLease` | Impl | 一回合的聊天管道。释放它 → 释放客户端 + 这层 HttpClient |
| `LlmImageGeneratorLease` / `LlmEmbeddingGeneratorLease` | Impl | 同上，对应图像与向量 |

**为什么租约要显式释放。** `IChatClient` 和它背后的 SDK 客户端持有资源，而"什么时候算一回合
结束"只有调用方知道。所以每次调用返回一个租约，而不是让工厂持有缓存——工厂本身是无状态的，
可以单例。

租约释放**只**释放自己那层薄 `HttpClient`；调用方交给 `ILlmTransport` 的共享 handler
不归本库管。

**不做什么。** 不缓存（没有连接池、没有客户端池——`HttpClient` 本就是为"频繁创建、薄包装
共享 handler"设计的）；不注册 DI（宿主一行 `services.AddSingleton<ILlmClientFactory>(...)`
即可）；不选模型、不校验可用性、不重试。

### Errors — 异常

库抛出的、可以直接展示给用户的错误。

| 类型 | 职责 |
|---|---|
| `LlmProviderException` | 配置或装配问题的可读错误（未注册的厂商、协议、quirk，缺少参数等） |
| `LlmVendorCatalogException` | 描述符无法解析，继承前者 |

**什么不打成自定义异常：**

| 情况 | 实际行为 |
|---|---|
| provider 返回非成功状态 | `HttpRequestException`，带被截断的响应体摘要 |
| 流太久没有更新 | `TimeoutException` |
| 协议不支持某个操作 | `NotSupportedException` |
| 调用方取消 | `OperationCanceledException` 原样冒泡 |
| 入参为 null / 非法 | `ArgumentNullException` / `ArgumentException`（编程错误） |

这些都是 .NET 或 SDK 已有的、调用方本来就要处理的类型。库不再包一层——包装只会让调用方
多写一次拆包，或者丢失 `HttpStatusCode` 这类有用信息。

## 加一个厂商：只写 JSON

**库不含任何厂商数据。** 内置厂商（随应用发布的）与用户厂商（运行时目录）都由调用方提供，
同一套 schema：

```csharp
var vendors = new LlmVendorCatalog()
    .AddFromJson(myJson)                            // 单个
    .AddFromDirectory(pathToVendorDirectory);       // 整个目录，路径由你决定
```

```json
{
  "vendorId": "deepseek",
  "displayName": "DeepSeek",
  "defaultEndpoint": "https://api.deepseek.com/v1/",
  "auth": { "kind": "bearer" },
  "modelListing": { "protocol": "openai", "path": "models" },
  "protocols": {
    "openai-chat": {
      "parameters": [
        {
          "path": "$.thinking.type",
          "type": "string",
          "sources": ["reasoning.effort", "option:thinking.type", "request.enableReasoning"],
          "map": { "none": "disabled", "false": "disabled", "*": "enabled" },
          "gateLegacySources": true
        }
      ]
    }
  }
}
```

`protocols` 的键就是这家厂商支持的接口。字段含义见
[Parameters](#parameters--厂商专有参数表) 与 [Vendors](#vendors--厂商目录)。

需要改请求形状（认证头、路径、查询参数、响应流）时加 `quirks`：

```json
{
  "vendorId": "azure-openai",
  "auth": { "kind": "header", "name": "api-key" },
  "protocols": { "openai-chat": {} },
  "quirks": [
    { "id": "query", "parameters": { "api-version": "2024-10-21" } },
    { "id": "path-prefix", "parameters": { "prefix": "/openai/deployments/{deployment}" } }
  ]
}
```

四层扩展，只有第 4 层要写代码：

| 厂商形态 | 写什么 |
|---|---|
| 标准 OpenAI 兼容 | 一个 JSON（参数表可为空） |
| 兼容 + 专有字段 | JSON + 1~2 行参数 |
| 兼容 + 不同认证/路径 | JSON + 若干 quirk 条目（Azure 也变成纯数据） |
| 完全不同线格式 | JSON + 一个 `ILlmProtocolEngine` |

## 加一个协议：写一个引擎

线格式和这五个都不同时才需要。

```csharp
public sealed class MyEngine : ILlmProtocolEngine
{
    public string Protocol => "my-protocol";
    public IChatClient CreateChatClient(LlmProtocolContext context, HttpClient httpClient) => ...;
    public ChatOptions CreateChatOptions(LlmProtocolContext context, IReadOnlyList<AITool> tools) => ...;
}

var factory = new LlmClientFactory(
    vendors,
    protocols: LlmProtocolRegistry.CreateDefault().With(new MyEngine()));
```

`context.Parameters` 里已经是按厂商参数表求值好的结果，引擎直接取用，不必再解析优先级。

## 不负责什么

| 不做 | 谁做 |
|---|---|
| **厂商数据（内置或预置）** | 调用方。库只有 schema 与加载机制 |
| **针对某家网关的畸形响应补偿** | 调用方。实现 `ILlmRequestQuirk` 后注册 |
| 密钥存储与解密 | 调用方。本库只收 `LlmCredential` 明文 |
| 连接与模型的持久化 | 调用方 |
| 默认模型选择、可用性校验 | 调用方 |
| 重试、限流、熔断 | 调用方，或在外面包一层 `DelegatingChatClient` |
| 日志策略 | 调用方通过 `LlmRequestOptions.LoggerFactory` 决定；不传就没有日志 |
| **token 计数与成本** | 调用方。计数由 M.E.AI 的 `UsageDetails` 提供；"按谁的价、要不要记账"是账务策略 |

## 公开面

按能力分组（括号里是桶：A = `Abstractions/`，D = `Dtos/`，I = `Implementations/`）：

| 能力 | 公开类型 |
|---|---|
| Connections | `LlmConnection`、`LlmCredential`、`LlmCredentialKind`（D） |
| Profiles | `LlmModel`、`LlmSampling`、`LlmModelLimits`、`LlmModelDescriptor`（D） |
| Parameters | `LlmVendorParameter`、`LlmVendorValueType`（D） |
| Vendors | `LlmVendorDescriptor`、`LlmVendorProtocol`、`LlmVendorModelListing`（D）、`LlmVendorCatalog`（I） |
| Protocols | `ILlmProtocolEngine`（A）、`LlmProtocolContext`、`LlmProtocols`（D）、`LlmProtocolRegistry`（I） |
| Chat | `LlmImageToolDelivery`、`LlmImageToolCapability`（D） |
| Transport | `ILlmTransport`（A）、`LlmTransports`（I） |
| Quirks | `ILlmRequestQuirk`（A）、`LlmQuirkRef`、`LlmQuirkContext`（D）、`LlmQuirkRegistry`（I） |
| Client | `ILlmClientFactory`（A）、`LlmRequestOptions`（D）、`LlmClientFactory` 与三个租约（I） |
| Errors | `LlmProviderException`、`LlmVendorCatalogException` |

除此之外一律 `internal`。
