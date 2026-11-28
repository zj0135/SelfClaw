# LlmProvider 重设计：一个可被任意项目引用的 LLM 客户端库

> 状态：**待确认，未动手**。本文取代前一版（那版把配置管理塞进了库，与"独立类库"目标直接冲突）。
> 前提：v33 不受限，`provider_kind` 可直接删除；本次是重设计。
> 关联：`docs/modularization-plan.md` v3 的 P1。

## 0. 先承认上一版的错误

上一版我写了「设置页 DTO 随词汇表进 LlmProvider」。这个判断是错的，而且错在最要命的地方——它会让库变成"SelfClaw 的内部件"，而不是"任何项目都能引用的 LLM 库"。具体错在哪：

| 上一版要塞进库的东西 | 为什么不能进库 |
|---|---|
| `AiProviderView`（含 `Sub`/`Color`/`AccentColor`） | 这是**设置页 UI 的渲染 DTO**。外部项目调 Claude 不需要知道"强调色" |
| `SaveProviderCommand` / `UpsertModelCommand` / `AiProviderSettingsState` / `AiProviderProtocolOption` | 设置页的**命令与状态**，是"配置编辑器"的契约 |
| `AiProviderConnection.CredentialRefs` / `CreatedAtUtc` | **持久化形状**：秘密引用 + 行时间戳。外部项目没有这些 |
| `IAiProviderRepository` / `IAiModelConfigurationRepository` | 库不应定义"仓储"——那是在替调用方决定用 SQLite |
| `IAiModelCatalog` / `IAiProviderSettingsService`(705L) | 应用级配置服务：哪个模型是默认、哪个被禁用，属于宿主策略 |
| `ISecretProtector` | DPAPI 是 Windows 专属，且"密钥怎么存"是宿主的事 |
| `{AppData}\providers\*.json` | 库不该知道任何绝对路径语义 |

**修正后的边界：库里只有"怎么调用"，没有"怎么存、怎么配、怎么展示"。**

## 1. 这个库对外的承诺

一句话：**给它一个已经解析好的连接 + 模型 + 凭据，它给你一个能用的 `IChatClient` / `IImageGenerationClient`。**

它保证：

1. **零 SelfClaw 依赖**。不认识 `SelfClaw.Core`，不认识 `MessageRecord`，不认识会话/工作区/工具策略。
2. **零持久化、零文件路径、零加密**。不碰 SQLite、不碰 `%AppData%`、不碰 DPAPI。
3. **零 UI 概念**。没有 View/Command/State DTO，没有颜色，没有 logo。
4. **零框架强制**。不依赖 DI 容器、不依赖 Hosting、不依赖 `Microsoft.Extensions.Configuration`；没有一个 `static` 可变状态。
5. **跨平台**。TFM `net10.0`（不带 `-windows`）。用了 Windows 专属 API 就编译失败。
6. **可测试**。Transport 是公开端口，外部项目能注入 `HttpMessageHandler` 做离线测试。

它不保证（明确不做）：密钥存储、连接/模型持久化、默认模型选择、可用性校验、重试、限流、成本记账（只提供纯函数算价）。

---

## 2. 依赖预算

实际落地是两个包（见 §11.2）：`Microsoft.Extensions.AI.OpenAI`（带来 M.E.AI.Abstractions 与 OpenAI SDK）与 `Anthropic`。

| 依赖 | 必要性 | 说明 |
|---|---|---|
| `Microsoft.Extensions.AI.OpenAI` | 必须 | 带来 OpenAI SDK 与 M.E.AI.Abstractions，并提供 `AsIChatClient` / `AsIImageGenerator` / `AsIEmbeddingGenerator` |
| `Anthropic` | 必须 | Messages 协议官方 SDK |

**刻意不引用**：

| 不引用 | 理由 |
|---|---|
| `Microsoft.Extensions.AI`（元包） | 它带来 `ChatClientBuilder`/`UseLogging`/`FunctionInvokingChatClient`。库不该替调用方决定管线；需要日志的调用方自己 `ChatClientBuilder` 一层即可 |
| `Microsoft.Extensions.DependencyInjection.Abstractions` | 库不注册任何东西。宿主一行 `services.AddSingleton<ILlmClientFactory>(...)` 就够；不想用 DI 的调用方直接 `new` |
| `Microsoft.Extensions.Hosting` / `Configuration` | 库不读配置；厂商描述符由调用方给流/目录 |
| `Microsoft.Data.Sqlite` / `ProtectedData` | 完全没有持久化与加密 |
| `Microsoft.ML.Tokenizers` | 计数是 agent 上下文引擎的事，不是 provider 的事 |

> 目标：`SelfClaw.LlmProvider.dll` + 两个 SDK，无其他 SelfClaw 程序集。

---

## 3. 公开 API 面（全部，约 20 个类型）

这是"独立类库"最重要的交付物：**公开面必须小到能一次看完。**

### 3.1 调用输入（纯数据，无存储字段）

```csharp
/// 一个已解析好的连接：厂商 + 端点 + 已解密凭据 + 连接级选项。
public sealed record LlmConnection(
    string VendorId,                                  // "openai" / "anthropic" / "deepseek" / 自定义
    Uri Endpoint,
    LlmCredential Credential,
    IReadOnlyDictionary<string, JsonElement>? Options = null);

/// 已解密的凭据。库不做任何解密，只决定"放在哪"。
public sealed record LlmCredential(
    LlmCredentialKind Kind,                           // Bearer / Header / Query
    string Value,
    string? Name = null);                             // Header/Query 的参数名

/// 一个模型 + 这一回合要用的参数。宿主负责把所有"配置来源"解析到这些字段上。
public sealed record LlmModel(
    string ModelId,
    string Protocol,                                  // LlmProtocols.* 之一，或自定义引擎的 id
    LlmSampling Sampling = default,
    IReadOnlyDictionary<string, JsonElement>? Options = null,   // 自由线参数袋
    string? ReasoningEffort = null,
    LlmModelLimits Limits = default,
    bool HasTypedConfiguration = false);              // 见 §5.3：忠实复现现有优先级

public readonly record struct LlmSampling(double? Temperature = null, double? TopP = null);

public readonly record struct LlmModelLimits(
    int? ContextWindowTokens = null,
    int? MaxOutputTokens = null,
    bool IsMultimodal = false);
```

注意与现在的差别：`AiSamplingOptions` 的 `TemperatureEnabled/TopPEnabled` 双布尔是**设置页语义**（区分"未启用"与"用了默认值"）。库里换成 `double?`，宿主映射一次。这就是"UI 形状不该进库"的具体体现。

### 3.2 唯一入口

```csharp
public interface ILlmClientFactory
{
    /// 造一回合用的聊天客户端。返回的 lease 拥有管道与 HttpClient 的生命周期。
    LlmClientLease CreateChatClient(LlmConnection connection, LlmModel model, LlmRequestOptions? options = null);

    /// 只造 ChatOptions（采样/输出上限/工具绑定）。给"想先检查再发"的调用方。
    ChatOptions CreateChatOptions(LlmConnection connection, LlmModel model, LlmRequestOptions? options = null);

    /// 生图。协议不支持时抛 NotSupportedException。
    LlmImageClientLease CreateImageClient(LlmConnection connection, LlmModel model);

    /// 模型清单（该端点暴露了哪些模型）。厂商描述符决定用哪个清单协议。
    Task<IReadOnlyList<LlmModelDescriptor>> ListModelsAsync(
        LlmConnection connection, CancellationToken cancellationToken = default);
}

public sealed record LlmRequestOptions(
    IReadOnlyList<AITool>? Tools = null,
    ILoggerFactory? LoggerFactory = null,            // 可选；库不强制日志
    TimeSpan? StreamIdleTimeout = null);
```

```csharp
public sealed class LlmClientLease : IDisposable
{
    public IChatClient Client { get; }
    public ChatOptions Options { get; }
    public HttpClient Transport { get; }
    public void Dispose();                            // 客户端 + HttpClient；不动共享 handler 池
}
```

典型外部用法（10 行就能跑）：

```csharp
var catalog = LlmVendorCatalog.CreateDefault();
var factory = new LlmClientFactory(catalog);
var connection = new LlmConnection("openai", new Uri("https://api.openai.com/v1/"),
                                   new LlmCredential(LlmCredentialKind.Bearer, apiKey));
var model = new LlmModel("gpt-5", LlmProtocols.OpenAiChat);
using var lease = factory.CreateChatClient(connection, model);
await foreach (var u in lease.Client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")], lease.Options))
    Console.Write(u.Text);
```

**没有一个参数是 SelfClaw 的类型，没有一句要读配置。**

带自建出站（SelfClaw 走系统代理）时，只多一行构造参数：

```csharp
var factory = new LlmClientFactory(LlmVendorCatalog.CreateDefault(),
                                  new NetworkProxyTransport(proxySource));   // 调用方的 ILlmTransport
using var lease = factory.CreateChatClient(connection, model);
```

不传 transport 就用 `LlmTransports.Default`。详见 §6。

### 3.3 扩展点（3 个，有明确上限）

```csharp
/// 新协议：完全不同的线格式时才实现。
public interface ILlmProtocolEngine
{
    string Protocol { get; }
    IChatClient CreateChatClient(LlmProtocolContext context, HttpClient http);
    ChatOptions CreateChatOptions(LlmProtocolContext context, IReadOnlyList<AITool> tools);
    IImageGenerationClient? CreateImageClient(LlmProtocolContext context, HttpClient http) => null;
}

/// 请求形状差异（认证放置、路径前缀、查询参数、脏流归一）：实现为 handler 包装。
public interface ILlmRequestQuirk
{
    string Id { get; }
    HttpMessageHandler Wrap(HttpMessageHandler inner, LlmQuirkContext context);
}

/// 出站口：调用方决定代理、连接池、证书、测试替身。详见 §6。
public interface ILlmTransport
{
    /// 返回当前出口的共享 handler。库只包装它（disposeHandler: false），**从不 dispose**。
    /// handler 的连接池与生命周期归实现方所有。
    HttpMessageHandler GetHandler();
}
```

### 3.4 厂商目录（实例，不是 static）

```csharp
public sealed class LlmVendorCatalog
{
    public static LlmVendorCatalog CreateDefault();                       // 只用内置（嵌入式资源）
    public static LlmVendorCatalog Empty { get; }                         // 只用自定义
    public LlmVendorCatalog AddFromJson(Stream stream);                   // 追加或同名覆盖
    public LlmVendorCatalog AddFromDirectory(string directory);           // 路径由调用方给
    public IReadOnlyCollection<LlmVendorDescriptor> Vendors { get; }
    public LlmVendorDescriptor GetRequired(string vendorId);
    public LlmVendorDescriptor? Find(string vendorId);
}

public sealed record LlmVendorDescriptor(
    string VendorId, string DisplayName,
    Uri DefaultEndpoint,
    IReadOnlyDictionary<string, LlmVendorProtocol> Protocols,   // 协议 id → 该协议下的参数表/quirks
    LlmVendorAuth Auth,
    LlmVendorModelListing? ModelListing = null,
    IReadOnlyList<LlmQuirkRef>? Quirks = null,
    string? Subtitle = null, string? AccentColor = null);        // 可选装饰，纯展示用，库不消费
```

`DisplayName`/`AccentColor` 留在描述符里是**数据**，不是 UI 代码——调用方要用就用，不用就忽略。这和 `AiProviderView` 的 `Color` 字段性质不同（后者把 UI 形状写进了 API 契约）。

### 3.5 其余

`LlmProtocols`（协议 id 常量）、`LlmModelDescriptor`（清单结果）、`LlmTokenUsage` + `LlmCostCalculator`（纯函数算价）、`LlmImageToolDelivery`（协议事实：工具结果能否带图、以什么形状）、`LlmProviderException`。

**公开面之外的一切都是 `internal`。** `OpenAiChatReasoningReplayClient`、`OpenAiStreamNormalizingStream`、`ProviderStreamIdleGuard`、`ModelOptionReader`、`LlmOptionResolver`、四个 engine 的实现全部 internal。

---

## 4. 厂商=数据，协议=代码

### 4.1 三个正交轴

| 轴 | 表示 | 粒度 | 形态 |
|---|---|---|---|
| 厂商 | `VendorId`（`openai`/`anthropic`/`custom`/`deepseek`/`azure-openai`） | 每连接 | **数据**（JSON 描述符） |
| 协议 | `Protocol` 字符串（`openai-chat`/`openai-responses`/`anthropic-messages`/`openai-images`） | 每模型 | **代码**（一个 engine） |
| 专有参数 | 参数行表（JSON-path + 来源链 + 映射） | 每厂商 × 每协议 | **数据** |

诊断（已核实）：`AiProviderKind` 全仓库只有 2 处真正参与判断（`OpenAiProviderAdapter.ChatCompletions.cs` L68/L76，都是"要不要默认写 `$.thinking.type`"），却为此注册了两个 adapter 实例 + 一列持久化字段。真正的协议分派全在 `AiProviderApiFormat`。所以"按协议"不是因为设计正确，是因为厂商轴没建起来。

### 4.2 协议用字符串而不是枚举

理由：第 4 层扩展（自定义协议）若协议是枚举就无法扩展，只能 fork。字符串 + `LlmProtocols.*` 常量兼顾可读与可扩。代价是失去编译期 switch 穷尽性——由 `LlmProtocolRegistry` 构造期 fail-fast 补上（协议重复杂交、未知协议查找即抛）。

### 4.3 厂商描述符（JSON，路径由调用方给）

内置于库的 3 个厂商是**嵌入式资源**；额外的由调用方 `AddFromJson`/`AddFromDirectory` 提供。**库自己绝不拼任何路径。** SelfClaw 宿主会传 `{AppData}\providers`，但那是宿主的决定。

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
        { "path": "$.thinking.type", "type": "string",
          "sources": ["reasoning.effort", "option:thinking.type", "request.enableReasoning"],
          "map": { "none": "disabled", "false": "disabled", "*": "enabled" } }
      ]
    }
  }
}
```

---

## 5. 厂商专有参数：参数表

### 5.1 参数行与来源

```csharp
internal sealed record LlmVendorParameter(
    string Path,                              // "$.thinking.type"
    LlmVendorValueType Type,                  // String / Boolean / Integer / Number
    IReadOnlyList<string> Sources,            // 按序求值，第一个有值的胜出
    IReadOnlyDictionary<string, string>? Map, // 值→写出口径，"*"=任意非空
    JsonElement? Value,                       // 常量，忽略 Sources
    bool RequiresLegacyConfiguration = false);// 见 5.3
```

| 来源 | 读什么 |
|---|---|
| `reasoning.effort` | `LlmModel.ReasoningEffort` |
| `option:<key>` | `LlmModel.Options[key]` |
| `request.enableReasoning` | 由 `ReasoningEffort is not null and != "none"` 派生（与今天 `AiChatClientFactory` 的算法一致） |
| `limits.multimodal` | `LlmModel.Limits.IsMultimodal` |
| `connection:<key>` | `LlmConnection.Options[key]` |
| `value` | 参数行自带的常量 |

### 5.2 等价性（现有 if 链 → 数据行）

| 内置厂商 | 参数行 | 等价于 | 
|---|---|---|
| `openai` | `$.thinking.type` sources=[`option:thinking.type`] RequiresLegacyConfiguration | L74 分支 |
| `custom` | `$.thinking.type` sources=[`reasoning.effort`, `option:thinking.type`, `request.enableReasoning`] + map | L68/L74/L78 三个分支的完整优先级 |
| 两者 | `$.reasoning_effort` sources=[`reasoning.effort`, `option:reasoning_effort`] RequiresLegacyConfiguration | L84-91 |
| 两者 | `$.parallel_tool_calls` sources=[`option:parallel_tool_calls`] | L93-96（不 gate） |
| 两者 | `$.store` sources=[`option:store`] | L98-101（不 gate） |

`openai-responses` 侧 6 行同理。**逐字节等价由 §7 的请求 golden 兑现。**

### 5.3 一个必须说清的忠实细节

现有代码里 `thinking.type` / `reasoning_effort` 的来源被 `Configuration is null` 门控，而 `store` / `parallel_tool_calls` 不门控。门控的语义是"UI 上设过类型化配置后，别让旧的手写线参数盖掉它"。库里用 `LlmModel.HasTypedConfiguration` + 参数行的 `RequiresLegacyConfiguration` 复现，**不改语义**。这是整个重构里唯一微妙的地方，靠 golden 兜底。

### 5.4 Knobs 与参数表的分界

参数表只管**原始请求体字段**。`temperature` / `topP` / `tools` / `MaxOutputTokens` 属于跨厂商通用项，仍走共享的 `ChatOptions` 构建器（含"目录上限兜底"这条跨厂商规则）。否则参数表会变成杂物间。

### 5.5 加厂商的真实成本

| 形态 | 写什么 |
|---|---|
| 标准 OpenAI 兼容 | 一个 JSON（参数表可为空） |
| 兼容 + 专有字段 | JSON + 1~2 行参数 |
| 兼容 + 不同认证/路径 | JSON + 若干 quirk 条目（Azure 也变纯数据） |
| 完全不同线格式 | JSON + 一个 `ILlmProtocolEngine` |

---

## 6. Quirks：请求形状差异

参数表能改"已存在请求对象的字段"，改不了请求形状（Azure 的 `api-key` 头、`/openai/deployments/{deployment}` 路径、`?api-version=`）。这类用**具名 quirk**：数据声明 + 代码注册表，实现为 `DelegatingHandler`。

选 handler 层的理由：`ILlmTransport` 是唯一造 `HttpClient` 的地方，OpenAI SDK（`HttpClientPipelineTransport`）、Anthropic SDK（`ClientOptions.HttpClient`）、模型清单三路全经它，所以 quirk 链**对所有协议统一生效，且不泄漏任何 SDK 类型**。

| 内置 quirk | 作用 | 取代 |
|---|---|---|
| `header-auth` | 自定义认证头 | 现在无处可写 |
| `query` | 静态查询参数 | 同上 |
| `path-prefix` | SDK 相对路径前插前缀 | 同上 |
| `stream-normalize` | 脏 SSE 帧归一 | **现在是无条件套在所有 OpenAI 族** |

最后一条正是"厂商特性挂在协议上"的实例：改造后只在描述符声明它的厂商生效（今天的 `custom` 声明，`openai` 不声明——与现状等价，因为严格 OpenAI 从不会吐这种帧）。

Azure 因此是纯数据：

```json
{
  "vendorId": "azure-openai",
  "auth": { "kind": "header", "name": "api-key" },
  "protocols": { "openai-chat": {} },
  "quirks": [
    { "id": "query", "parameters": { "api-version": "2024-10-21" } },
    { "id": "path-prefix", "prefix": "/openai/deployments/{deployment}" }
  ]
}
```

### 6.1 出站注入：为什么给 handler，而不是给 HttpClient

调用方（SelfClaw）要"自己决定走系统代理"，这个诉求完全成立，而且**必须**由调用方解决——库不该知道任何代理语义。但注入点应该是 **handler**，不是 `HttpClient` 实例。直接传 `HttpClient` 有五个具体问题，其中第一个是硬阻断：

| # | 问题 | 证据 / 后果 |
|---|---|---|
| 1 | **Anthropic SDK 会 dispose 你传进去的 `HttpClient`** | SDK 文档原话：`ClientOptions.DisposeHttpResources` — "Disposes `ClientOptions.HttpClient` along with any attached `Handlers`"。若 SelfClaw 传一个长命共享 `HttpClient`，第一回合结束时它的 `AnthropicClient` 被 dispose，**共享 HttpClient 随之失效**，第二回合 `ObjectDisposedException`。现有代码正是为此每回合造一个薄 wrapper（`CreateTurnClient`，`disposeHandler: false`） |
| 2 | **`Timeout` 冲突导致提前取消** | SDK 文档原话："The HttpClient has a built-in timeout, which defaults to 100 seconds. When passing a custom HttpClient, this timeout may conflict with the SDK's own timeout handler and cause premature cancellation." 现有 `CreateTurnClient` 显式设 `Timeout = InfiniteTimeSpan` 就是为避开它；共享实例上无法按回合改 |
| 3 | **`BaseAddress` 无法按连接设定** | 一个 `HttpClient` 只有一个 `BaseAddress`，而 SelfClaw 同时有 OpenAI / Anthropic / 自定义网关多个端点；在共享实例上改 `BaseAddress` 是并发写竞态 |
| 4 | **连接级选项无处施加** | `timeout_seconds` / `extra_headers` 是每连接的；共享实例上只能改 `DefaultRequestHeaders`，同样是并发写竞态 |
| 5 | **库失去池化与出口切换能力** | 现在 handler 池按指纹（endpoint + timeout + headers + 出口）缓存，出口一变下次请求就换池。调用方自持 HttpClient 后，库只能二选一：要么放弃切换，要么去 dispose 调用方的对象 |

**结论与原则：共享 handler，不要共享 client。** `HttpClient` 只是 handler 上的一层薄包装，共享它没有任何收益，却带来所有权歧义；而 `SocketsHttpHandler` 才是真正持有连接池和代理配置的对象。所以 `ILlmTransport.GetHandler()` 是唯一注入点：

| 层级 | 调用方写什么 | 适用 |
|---|---|---|
| **推荐**：`ILlmTransport` | `GetHandler()` 返回当前出口的共享 `SocketsHttpHandler`；池化与出口切换归调用方 | SelfClaw（代理）、自定义证书/解压、离线测试 |
| 便利：`LlmTransports.FromHandler(h)` | 直接给一个 handler 实例（库不 dispose，`disposeHandler: false` 包装） | 已有 handler 的场景 |
| 逃生口：`LlmTransports.FromHttpClient(Func<HttpClient>)` | 每次调用**新建**一个 `HttpClient`，由 lease 负责 dispose | 用 `IHttpClientFactory` 的调用方 |

```csharp
public static class LlmTransports
{
    /// 库只包装、从不 dispose 这个 handler。
    public static ILlmTransport FromHandler(HttpMessageHandler handler);

    /// 工厂每次必须返回新实例（由 lease dispose）。要共享请改传它的 handler。
    public static ILlmTransport FromHttpClient(Func<HttpClient> factory);

    /// 开箱可用：SocketsHttpHandler，跟随系统代理与默认凭据，无自定义头。
    public static ILlmTransport Default { get; }
}
```

`FromHttpClient` 的注释里会写死那句"要共享请改传它的 handler"——因为这是这个 API 唯一的误用方式。

### 6.2 库因此变简单了

把 handler 所有权交出去后，库内**整块消失**：handler 池、出口指纹计算、`IPAddress`/代理判断、`IDisposable` 生命周期。库每回合只做一件极廉价的事：

```csharp
var handler = _transport.GetHandler();
var http = new HttpClient(handler, disposeHandler: false)
{
    BaseAddress = connection.Endpoint,          // 每连接，天然正确
    Timeout = TimeSpan.FromSeconds(connection.TimeoutSeconds)  // 每连接
};
```

`HttpClient` 本就是为"频繁创建、薄包装共享 handler"设计的，不需要缓存。

SelfClaw 侧的实现就是今天那个测试缝的正式化——`AiProviderHttpClientProvider` 已经有 `Func<NetworkProxySettings, HttpMessageHandler>` 构造，`NetworkProxyHttpHandler.Create(settings)` 就是现成的 handler 工厂：

```csharp
internal sealed class NetworkProxyTransport(INetworkProxySource proxySource) : ILlmTransport
{
    public HttpMessageHandler GetHandler() => NetworkProxyHttpHandler.Create(proxySource.Current);
}
```

`docs/network-proxy-design.md` 的语义一字不改：`Direct` 必须显式 `UseProxy = false`（因为 .NET 默认跟随系统代理），`System` 留 `Proxy = null`，`Custom` 装 `WebProxy`。

### 6.3 默认 transport

---

## 7. 验证：请求侧 golden

最大风险是**悄悄改了某个厂商的请求体**。响应侧已有 `ProtocolBaselineProbeTests`；缺请求侧，这次必须补：

- 矩阵：`{openai, custom} × {chat, responses, anthropic} × {无类型化配置, 类型化配置+effort=none, 类型化配置+effort=high, 无类型化配置+显式 option, enableReasoning=true}`
- 断言：**原始请求体 JSON**（handler 层抓取），改造前后逐字节一致
- 落盘：`SelfClaw.Tests/LlmProvider/RequestGolden/*.json`

它同时证明三件事：①参数表与 if 链等价；②流归一收窄到 `custom` 没改坏 `openai`；③`max_output_tokens` 目录兜底优先级未变。

另外加一个**独立性测试**（比断言更硬的证明）：`SelfClaw.Tests/LlmProvider/StandaloneLibraryTests.cs` —— 用反射断言 `typeof(LlmClientFactory).Assembly` 的引用集合里没有任何 `SelfClaw.*`、没有 `Microsoft.Data.Sqlite`、没有 `ProtectedData`、没有 `Hosting`。再配一个 `samples/LlmProviderProbe` 控制台工程（~20 行、零 SelfClaw 引用），作为"外部项目真的能引用"的活证据。

出站注入另需三组测试（§6.1 的五个坑逐条钉住）：

1. **handler 不被库 dispose**：调用方给 handler → 跑一回合 → 断言 `GetHandler()` 返回的实例仍可用（再发一次请求成功）。
2. **`FromHttpClient` 的所有权**：工厂每次返回新实例 → 断言 lease dispose 后该 client 已释放；且断言库从未 dispose 工厂之外的对象。
3. **`BaseAddress` / `Timeout` 是每连接的**：同一 transport、两个不同 endpoint 的连接交替发请求 → 断言各自打到正确端点、超时按连接生效。

---

## 8. 存储重设计（v34，宿主侧，不进库）

库不碰数据库。但放开 v33 后 SelfClaw 宿主能顺手把冗余列清掉：

```sql
-- ai_provider_connections
- provider_kind INTEGER      -- 删除：仅 2 处决策，已移进厂商描述符
- auth_kind     INTEGER      -- 删除：一个值 ApiKey，且校验永不触发；改由描述符声明
  catalog_id → vendor_id     -- 改名，值不变（openai/anthropic/custom）

-- ai_model_profiles
- api_format INTEGER         -- 删除
+ protocol TEXT NOT NULL DEFAULT 'openai-chat'   -- 值域 'openai-chat'/'openai-responses'/
                                                  -- 'anthropic-messages'/'openai-images'
```

**必须新建迁移机制**：现状 `SqliteDatabase` 是单版本硬门（`versions.Count != 1 || versions[0] != 33` 即抛，注释写明"Never migrate"）。放开 v33 意味着我们开始接管迁移正确性——这是本次最大新增风险，要一次建好而不是为 v34 打特例：

1. 读 `schema_versions`：`Max == Current` → 直接开；`Max < Current` → **先复制 `selfclaw.db.bak-{version}`**，再按顺序步骤逐个事务应用，每步写一行版本；`Max > Current` → 拒绝（降级守卫）。
2. `int → string` 的协议转换需要 `ADD COLUMN` → `UPDATE`（CASE 映射 0..3）→ `DROP COLUMN`（SQLite 3.35+ 支持）。
3. 任何异常 → 回滚 + 保留备份 + 可读错误，**不半迁移**。

---

## 9. 宿主侧保留什么（这些都不进库）

`SelfClaw.Infrastructure/AiProviderSettings/`：

| 保留 | 说明 |
|---|---|
| `AiProviderConnection` / `AiModelProfile` / `AiModelConfiguration` / `AiModelProfileSelection` | 持久化形状 |
| `AiProviderView` / `AiModelView` / `EnabledModelView` / `SaveProviderCommand` / `UpsertModelCommand` / `AiProviderSettingsState` / `ConnectivityCheckResult` | 设置页契约 |
| `AiProviderSettingsService`(705L) / `SqliteAiProviderRepository` / `AiProviderSecrets` | 配置 CRUD + 密钥解析 |
| `ISecretProtector` 实现（DPAPI） | 接口留在 `SelfClaw.Core`（app 契约） |
| `AiProviderProtocolOption` | **删除**：加提供商=选厂商，加模型时协议下拉框来自厂商 `protocols` 键 |
| **`LlmRequestFactory`（新）** | 唯一的映射点：`Connection + Profile + Secrets` → `LlmConnection + LlmModel`。这不是泄漏，是宿主的职责 |

`AiChatClientFactory` 拆两半：宿主侧 `AiChatClientResolver`（解析+校验+解密）+ 库侧 `LlmClientFactory`（纯构造）。`NonSensitiveLoggerFactory`（永久屏蔽 M.E.AI Trace）留在宿主，通过 `LlmRequestOptions.LoggerFactory` 传进去——**库不强制任何日志策略**。

---

## 10. 实施步骤（P1 = 5~6 天）

| 步 | 内容 | 验证 |
|---|---|---|
| P1a | 建项目（TFM `net10.0`，仅 3 个包），纯 `git mv` 协议实现，行为零改动 | 全绿 |
| P1b | 迁移 runner（v34）+ DDL；本步不碰代码契约 | v33 fixture → v34 打开成功 + 备份存在 |
| P1c | Tier1 契约（§3.1/3.2）+ 厂商描述符与目录 + `LlmRequestFactory`；**请求侧 golden 落盘（改造前基线）** | 全绿 + golden 存在 |
| P1d | 4 个 `ILlmProtocolEngine` + 参数表驱动 + quirk 链；删 `AiProviderRegistry` | **golden 逐字节一致（核心验收）** |
| P1e | 删 `AiProviderKind`/`AuthKind`/`AiProviderProtocolOption`；设置页 DTO 留宿主；独立性断言 + `samples/LlmProviderProbe`；可选补 4 个内置厂商 JSON | golden 不变 + 独立性断言绿 + 冒烟（openai 真连接、custom 网关、外部 JSON 生效） |

---

## 11. 实现记录（P1a 已落地）

`SelfClaw.LlmProvider` 已建成并编译通过、31 个针对性测试全绿、全量 2152 个测试无回归。
实现中有四处比本文原计划更干净，一处踩到 SDK 的坑，都记在这里，后续阶段以本节为准。

### 11.1 图片与向量直接复用框架适配器（优于原计划）

原计划要自造 `IImageGenerationClient` 并搬 400 行 `OpenAiImageGenerationClient`。实现时核实到
`Microsoft.Extensions.AI.OpenAI` 已经提供：

- `ChatClient.AsIChatClient()`
- `ImageClient.AsIImageGenerator()` → `IImageGenerator`
- `EmbeddingClient.AsIEmbeddingGenerator(int? dimensions)` → `IEmbeddingGenerator<string, Embedding<float>>`

所以图片与向量引擎各只有约 20 行，不搬那 400 行。这也正好对上"库只负责提供 M.E.AI 对象"的定位——
比自造接口更彻底，且调用方拿到的就是框架类型。

新增第五个内置协议 `openai-embeddings`（原计划把 embedding 留作"未来的扩展点"）。

### 11.2 包清单（实际）

| 包 | 说明 |
|---|---|
| `Microsoft.Extensions.AI.OpenAI` | 带来 OpenAI SDK + M.E.AI.Abstractions + 上面三个 `As*` 适配器 |
| `Anthropic` | Messages 协议官方 SDK |

不需要单独引用 `Microsoft.Extensions.AI.Abstractions`（传递获得），也不引用 `Microsoft.Extensions.AI` 元包。

### 11.3 不提供 `FromHttpClient`

`HttpClient` 不暴露它内部的 handler，所以无法把"一个现成 HttpClient"接进 quirk 链。而 quirk 链是
请求形状差异的唯一落点。因此公开面只有 `ILlmTransport.GetHandler()` 与 `LlmTransports.FromHandler(handler)`。
要复用已有 `HttpClient`，交出它的 handler。

### 11.4 库不提供 token 计数与成本计算

`LlmTokenUsage` 与 `LlmCostCalculator` 已删除。

理由：计数已经是 M.E.AI 的 `UsageDetails`（框架返回类型），本库再定义一个 `TokenUsage`
只是同一件事的第二种写法；而"按谁的价、要不要记账"是调用方的账务策略，价格表属于调用方配置。

模型清单条目 `LlmModelDescriptor` 仍然带价格字段——那是 provider 线上 `/models` 响应里的数据
（OpenRouter 那类端点确实返回），是本库在读别人给的数，不是本库在算账。

`LlmProviderLayoutTests` 会断言这两个类型不会重新出现。

### 11.5 踩到的坑：`options.Patch` 必须直接调用

`System.ClientModel.Primitives.JsonPatch` 通过 `OpenAIChatCompletionOptions.Patch` 暴露的是**可写引用**。
把它取出来当参数传给别的方法，改动就落在副本上——请求体里什么都不会出现，而且不报错。

```csharp
// 错：patch 是副本，写入丢失
Apply(raw.Patch, parameters);

// 对：每次都在属性表达式上直接调用
raw.Patch.Set(key, value);
```

这一点用真实请求体捕获才发现（`LlmRequestShapeTests`）。没有它，参数表看起来全部通过单测，
但线上一个字段都发不出去。**Infrastructure 现有的 `BuildChatCompletionRawOptions` 恰好是正确形态**，
所以这个坑只在"把逻辑抽成 helper"时才会踩到。

### 11.6 按能力分目录与命名空间

原实现把所有 `.cs` 平铺在项目根目录，走的是"库很小，一眼看完"的思路。实际上 55 个文件平铺后
能力边界只能靠文件名猜。现在一个能力一个目录、一个命名空间：

```
Client/  Connections/  Models/  Parameters/  Vendors/
Protocols/ (＋5 个协议子目录)  Chat/  Transport/  Quirks/  Errors/
```

依赖方向单向、无环（见根 README 的图）。`LlmProviderLayoutTests` 钉住三条约定：

1. 项目根目录不放 `.cs`
2. 每个含代码的目录都有 `README.md`
3. 每个目录的文件声明的命名空间与目录路径一致

代价是调用方要多写几个 `using`。对"可以被任意项目引用"的库来说，这是对的取舍：
命名空间承担了能力索引的职责，而 55 个平铺文件的 `using SelfClaw.LlmProvider;` 什么都不说明。

### 11.7 能力内再分三层桶

第一版把所有 `.cs` 直接放在能力目录里（`Chat/LlmChatOptions.cs` 与 `Chat/LlmImageToolDelivery.cs` 并列），
接口、数据、实现混在一起。现在每个能力目录内部再分三个桶：

| 桶 | 只放 |
|---|---|
| `Abstractions/` | 接口 |
| `Dtos/` | record / enum / 常量 |
| `Implementations/` | 有行为的类（`internal` 与 `public` 都算） |

三个例外，都由测试表达而不是靠自觉：

- `Protocols/<协议名>/` 是**单一实现单元**（一个协议一套实现），内部不再分桶；
- `Errors/` 是**同质叶子**（全部是异常类型），不分桶，测试断言它只含 `: Exception`；
- 能力目录自身不直接放 `.cs`，只放桶。

同时把 `Models` 能力改名为 `Profiles`——否则会出现 `Models/Dtos` 这种读起来重复的路径，而
`LlmModel` 本来就是"模型档案"。

`LlmProviderLayoutTests` 现在钉住 9 条：根目录无 `.cs`、只有根 README 且覆盖全部能力、
目录=命名空间、能力目录不直接放 `.cs`、`Abstractions` 只放接口、`Dtos` 不放行为、
协议子目录不含接口、**一个文件只声明一个类型**。

最后一条抓出三处真实违规：`LlmImageToolCapability.cs` 里还藏着 `LlmImageToolDelivery`、
`ILlmRequestQuirk.cs` 里藏着 `LlmQuirkContext`、`ILlmProtocolEngine.cs` 里藏着 `LlmProtocolContext`。
都已拆成独立文件（仓库约定本就是"一个文件只放一个 DTO 或一个服务类"）。

代价：调用方要多引几个 `using`。这是有意的取舍——命名空间承担能力索引，
`using SelfClaw.LlmProvider.Client.Implementations;` 比一条什么也不说明的
`using SelfClaw.LlmProvider;` 更有信息量。

### 11.8 文档只有根目录一份

第一版给每个能力目录（含 5 个协议子目录）各写了一份 README，共 16 份、约 690 行。
碎片化的代价是明显的：想知道"一次请求从模型选择到发出经过了哪些决定"，要在 4 个文件之间跳。

现在全部汇总到根 `README.md`，按能力分节（`### Connections —` 这种），保留每一处
"为什么这么做"的原创论证，去掉重复的"布局"表（三桶规则统一说明一次即可）。

`LlmProviderLayoutTests` 相应加了两条断言，比原来的"每目录有 README"更贴意图：

- `文档只有根目录一份README` —— 子目录出现 `README.md` 就失败；
- `根README覆盖每一条能力` —— 每个能力目录都必须有对应的 `### <能力> —` 小节，
  防止以后新增能力却忘了写文档。

### 11.9 库不带任何厂商数据

`Vendors/*.json` 已从库中移出。**目前没有任何地方存放它们**——宿主尚未接入新库
（设置页仍走 `AiProviderCatalog.cs`），所以先在宿主侧建一份 "半接入" 的产品数据没有意义，
只会成为第二处需要同步维护的定义。等 P1c 真正把宿主接上时再随接入一起建。
库随之删掉 `LlmVendorCatalog.CreateDefault()` 与内嵌资源，`LlmClientFactory` 的 `vendors` 参数改为必填。

三条理由，按重要性排：

1. **重复的事实来源。** 本仓库已有 `AiProviderCatalog.cs` 描述"显示哪几家、什么颜色、默认端点"，
   宿主的设置页在读它。库再嵌一份，同一件事就有两处定义。
2. **版本纠缠。** 厂商线格式会变；数据在库里意味着改一个字段要发版。
3. **展示字段不是库的事。** `displayName` / `subtitle` / `accentColor` 是产品决定。
   库透传这些字段（不多不少地携带、不消费），但不替调用方挑选。

代价（必须承认）：开箱不能直接用。调用方至少要给一份描述符，否则 `GetRequired` 抛
一条错误并指出怎么加。库的 README 保留了 openai / deepseek / azure-openai 三份可复制的样板，
把这部分知识转移的成本压到"复制一段 JSON"。

测试也随之改成分层：

| 层 | 测什么 | 夹具 |
|---|---|---|
| 库测试 | 机制：来源链、门控、映射、quirk 链、请求体 | `TestVendors` 里的 `test-strict` / `test-gateway` 合成厂商 |
| 宿主测试（P1c） | 数据：`openai` / `anthropic` / `custom` 的真实参数表 | 宿主 `Vendors/*.json` |

这比原来更准确：库测试不再"测试自己内嵌的数据"，而是测试机制；真实厂商的行为由
拥有那份数据的一方负责验证。

`LlmProviderStandaloneTests` 增加一条断言：程序集不得内嵌任何名字含 `Vendor` 的资源。

### 11.10 内置 quirks 收窄到三个通用形状

`stream-normalize`（约 360 行 SSE 改写）已从库中移除，只保留 `ILlmRequestQuirk` 机制与三个
通用实现（`header-auth` / `query` / `path-prefix`）。

判据不是"代码行数"，而是**这段代码在替谁做判断**：

| quirk | 性质 | 归属 |
|---|---|---|
| `header-auth` / `query` / `path-prefix` | 协议形状的**参数化变体**，20~80 行，不绑定任何厂商 | 库 |
| `stream-normalize` | 对**特定网关 bug** 的补偿，360 行，编码别人家的错误行为 | 调用方 |

对标准提供商的使用者，`stream-normalize` 永远是死代码——符合规范的端点不产生那些畸形帧。
而一个错误实现会让思考内容静默丢失（M.E.AI 只读 `reasoning_content`），这是最糟的失败模式：
不报错、只是少了东西。

已把三个典型症状与做法写进根 README 的 `Quirks` 一节，知识没有丢，只是从代码变成了文档。

连带影响：

- 宿主当前没有任何厂商描述符文件，所以不存在需要删的声明。宿主若确实需要，按 README 实现一个
  quirk 并在自己的描述符里声明 id 即可；现有
  `Infrastructure/AiProviders/OpenAi/OpenAiStreamNormalizingStream.cs` 就是那份实现，P1d 可直接改造。
- 测试改为由 `TestVendorQuirk` 提供调用方实现的 quirk，并新增两条断言：
  **调用方实现的 quirk 真的加到了线上请求头上**，以及**未注册的 quirk 在发请求前就失败**。

### 11.11 两个框架缺陷补偿：已实测，保留到框架修复

`OpenAiChatReasoningReplayClient` 与 `OpenAiResponsesReplayClient` 都来自原
`SelfClaw.Infrastructure/AiProviders/OpenAi/`，迁移时逻辑逐字节未改（`diff` 只显示注释与
格式化差异）。它们宣称的行为此前只有原作者的注释作证，P1a 后补了两次实证：

| 探针 | 结果 |
|---|---|
| 带 `TextReasoningContent("thinking hard")` 的助手消息 → `AsOpenAIChatMessages()` 序列化 | 不打补丁：`{"role":"assistant","content":"the answer"}`；打补丁后：`...,"reasoning_content":"thinking hard"` |
| 喂 `response.output_item.done` SSE → 看 SDK 吐出的 update | update 上是 `StreamingResponseOutputItemDoneUpdate`（带完整 item），内层 `FunctionCallContent.RawRepresentation` 为 **null** |

结论按证据强度分开记录，不混为一谈：

- Chat 版：**后果确定**。多轮对话里模型看不到自己上一轮的思考内容——静默丢失，不报错。
- Responses 版：**只证明信息丢失，未证明后果**。`call_id`/`name`/`arguments` 都在，
  `id`/`status` 是否必需取决于是否用 `previous_response_id` 续接。属于防御性保留。

决策：两者都保留（用户 2026-11-28 确认），但标注为**有保质期的代码**。
`Directory.Packages.props` 把 `Microsoft.Extensions.AI.OpenAI` 钉在 10.10.0，
升级时必须重新核对；官方修好后直接删除并退化为 `client.AsIChatClient()`。

这两次探针是临时测试，没有留在仓库里——它们验证的是第三方框架的行为，
写成常驻测试只会在框架升级后变成需要维护的噪音。证据与判断记录在这里。

### 11.12 已验证的内容

| 断言 | 位置 |
|---|---|
| 程序集零 `SelfClaw.*` 引用、无 Sqlite/ProtectedData/Hosting/DI、TFM 不含 Windows | `LlmProviderStandaloneTests` |
| 目录约定：根目录无 `.cs`、唯一根 README 且覆盖全部能力、命名空间=路径、能力内分三桶、抽象桶只放接口、数据桶无行为、一文件一类型、无 token/成本类型 | `LlmProviderLayoutTests`（9 条） |
| 空目录报可读错误、未知厂商列已知、同 id 覆盖、描述符校验 | `LlmVendorCatalogTests` |
| 程序集不内嵌任何厂商描述符 | `LlmProviderStandaloneTests` |
| 参数表与旧 if 链逐条等价（含 `gateLegacySources` 门控） | `LlmParameterTableTests`（11 条） |
| **厂商差异真的体现在发出的请求体里** | `LlmRequestShapeTests`（5 条，捕获真实 HTTP body） |
| 图片投递形状 | `LlmImageToolCapabilityTests` |

---

## 12. 已确认的决策

P1 开工前逐条确认完毕：

1. 公开面就是 §3 列的这些类型。
2. 出站注入点用 `ILlmTransport`（handler）而不是 `HttpClient`；SelfClaw 实现 `NetworkProxyTransport`，库内没有任何代理/池化/指纹代码。
3. `LlmVendorCatalog` 是实例，路径由调用方给；库绝不拼 `%AppData%`。
4. 协议用字符串（`"openai-chat"`）而不是枚举，自定义协议才能真正扩展。
5. **库不带 DI 依赖**：宿主一行 `services.AddSingleton<ILlmClientFactory>(...)` 即可。
6. `ISecretProtector` 留在 `SelfClaw.Core`，库只收已解密的 `LlmCredential`。
7. **本轮不补内置厂商**，且后续也不进库：厂商数据（含 `openai` / `anthropic` / `custom`）
   归调用方，库只有 schema 与加载机制（见 §11.7）。

下一步是 P1b（v34 迁移 runner）。这一步会改宿主侧 schema，与库无关。
