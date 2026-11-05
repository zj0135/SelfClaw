# 网络出口（代理）设置设计

关系：与 `appearance`、`workspace_tools`、`direct_compression` 同构的一套桌面设置（真值在 `desktop-settings.json` 的 `network_proxy` 节点）；覆盖的请求形态见 [全链路架构图](pipeline-architecture.md) 与 [运行期执行流](runtime-execution-flow.md)。

## 1. 需求

在设置页提供三档网络出口，选定后 SelfClaw 自身的请求都从该出口发出：

| 模式 | 界面文案 | 含义 |
|---|---|---|
| `Direct` | 本地直连 | 直连，忽略系统代理与继承来的代理环境变量 |
| `System`（默认） | 系统网络 | 跟随 Windows 代理配置（与设置项存在之前的行为一致） |
| `Custom` | 自定义 | 使用用户填写的 host:port |

范围：不做代理认证，不做 NO_PROXY 输入框，不做代理连通性测试按钮，不接入 WebView2 内核（理由见 §5）。

## 2. 契约

`NetworkProxySettings(Mode, Host, Port)` 是 `SelfClaw.Core/Models/Network` 的记录；`Host`/`Port` 只在 `Custom` 下有含义。

| 模式 | 进程内 HTTP handler | 子进程环境变量 |
|---|---|---|
| `Direct` | `UseProxy = false` | `HTTP_PROXY` / `HTTPS_PROXY` / `ALL_PROXY` = 空串，`NO_PROXY` = `*` |
| `System` | `UseProxy = true`，`Proxy = null`（.NET 默认链：环境变量 → Windows 设置） | 从注册表 `HKCU\...\Internet Settings`（`ProxyEnable` + `ProxyServer`）解析出的地址；解析不到就什么都不写 |
| `Custom` | `UseProxy = true`，`Proxy = new WebProxy(http://host:port)` | 同一地址写入三个变量 |

两条判定规则：

- **未填完的自定义端点按系统模式处理**，而不是失败。设置页在用户还在输入端口时就会保存，落盘的 `Custom` 可能只有 host；`NetworkProxyEndpoint.TryGetCustomProxy` 是这两处唯一的判定点。
- **`System` 是枚举的零值**。手改过的节点（缺字段、非法枚举值）因此回落到设置项存在之前的行为，而不是静默变成直连。

## 3. 覆盖的请求

- **进程内 .NET**：一切请求都经 `AiProviderHttpClientProvider`（Direct 对话、Responses/ChatCompletions、Anthropic、模型列表、连通性检查），handler 由 `NetworkProxyHttpHandler.Create` 构造。MCP 的 HTTP 传输也用同一个工厂，因为 SDK 默认会自建 `HttpClient` 并绕过设置。
- **SelfClaw 启动的子进程**：CLI 回合（`CliAgentProcessHost`）、编程助手探测（`CliProbeProcess`）、`run_shell_command`（`WorkspaceShellRunner`）、MCP stdio server（含 npx 拉包）。Node/Python 系 CLI 完全不读 Windows 系统代理，只认环境变量，这正是 `System` 档要解析注册表地址的原因。

**刻意不覆盖**（不是遗漏，改之前先读这一节）：

- **Skill 脚本**：`SkillScriptRunner` 的变量白名单是安全边界（第三方脚本），不注入。
- **ConPTY 终端、Git 命令**：用户自己的工具，不是 SelfClaw 的业务请求；它们仍继承用户的环境变量。
- **WebView2**：见 §5。

## 4. 生效时机

| 路径 | 生效点 |
|---|---|
| 进程内 HTTP | 下一次创建 handler（fingerprint 含代理描述，换档即换 handler，无需重启） |
| 子进程 | 下一次启动子进程 |
| 已建立的 MCP 连接 | 沿用建连时的出口；空闲 5 分钟回收后重建（池 key 含 `ConfigRevision`，v1 不做主动 drain） |

handler 不随换档主动 purge：在飞请求不能被打断 dispose，代价是每个档位各留一个空闲连接池，直到 provider 释放。

`App.StartAsync` 在解析 MainWindow 之前 `await` 一次 `GetAsync()`：否则用户保存过的自定义出口要等第一次打开设置页才被加载。

## 5. 为什么 WebView2 不接入

打包形态下界面本体不是网络请求：`MainWindow` 用虚拟主机映射把 `Assets/` 与附件目录映射成 `https://appassets.selfclaw.local/...`，读的是本地文件；外链经 `open-link` 交给系统默认浏览器。真正会用到浏览器内核网络栈的只有三类，且都不是 SelfClaw 的业务请求：插件面板若加载远端页面/图片、开发模式的 vite dev server、Edge 运行时自身的后台请求。

代价也不对等：WebView2 的代理只能用 `--proxy-server` 且仅在创建时设定（改完必须重启），还要把虚拟主机加进 bypass；一旦代理不可用，界面会白屏，用户连设置页都打不开去改回来。

## 6. 实现落点

- 契约：`SelfClaw.Core/Models/Network/*`、`SelfClaw.Core/Interfaces/Network/INetworkProxySource.cs`（`Current` 同步快照 + `GetAsync`；`Current` 永不阻塞、永不抛）。
- Infrastructure：`SelfClaw.Infrastructure/Network/*`（默认源、handler 工厂、端点解析、注册表读取、子进程环境变量），消费方为 `AiProviderHttpClientProvider`、`McpTransportFactory`、`CliAgentProcessHost`、`WorkspaceShellRunner`。
- Desktop：`Services/Network/NetworkProxySettingsService.cs`（节点 `network_proxy`、归一化、`Changed`）+ `NetworkProxySettingsBridge.cs`（`network-proxy/get|save` → `network-proxy/state`），在 `DesktopServiceRegistration` 里覆盖 Infrastructure 注册的默认源。
- 前端：`composables/useNetworkProxy.js`、`components/settings/network/Network.vue`，挂在「系统设置 → 运行时设置」组内（默认 Shell 下方，标题「网络代理」）。

## 7. 已知局限

- PAC 自动配置脚本场景下，「系统网络」无法为子进程注入具体地址（只有 `AutoConfigURL` 时读不到 host:port）。
- 自定义模式不接受认证信息，也无法表达 SOCKS 代理。
- 按 provider 连接覆盖（`AiProviderConnection.ConnectionOptions` 里预留的 proxy 位置）仍是空的；出口是进程级设置。
