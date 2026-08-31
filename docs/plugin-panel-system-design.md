# 右侧插件面板系统设计（UI Plugin Panels）

> 状态：v1（实现完成）。基线为仓库当前代码。
>
> 本文只覆盖 **UI 面板** 这一类 contribution。Plugins/Skills/MCP 三类 AI 能力的设计见
> `direct-extensions-system-design.md`，两者共用同一套包格式、安装、权限确认与版本租约。

---

## 0. 核心结论

SelfClaw 已经有一套完整的插件系统，但它只管 AI 能力：`plugin.json` 的 `contributes` 里只有
`directInstructions` / `skills` / `mcpServers`。本设计**不新建插件系统**，而是加第四类
contribution：`contributes.panels`。

因此安装（`ExtensionPackageInstaller` 的 staging + 校验 + 原子移动）、版本哈希不可变目录、
权限确认（`acknowledged_permissions_json`）、全局启停、引用计数版本租约
（`PluginVersionLeaseManager`）与设置页全部原样复用。新代码只解决一个问题：

> 怎么安全地把一个第三方 HTML 页面放进右侧，并给它一条受控的宿主通道。

**无 SQLite schema 变更。** 面板定义存在已有的 `extension_packages.manifest_json`，标签页状态属于
桌面 UI 状态，进 `desktop-settings.json`。

---

## 1. 承载模型

右侧栏从 WPF 搬进 Vue，与 `TerminalPanel.vue` + `TerminalHostController` 的分工一致（前端负责渲染，
C# 负责能力与生命周期）：

```
┌──────────┬──────────────────────┬─────────────────┐
│ Sidebar  │  Transcript          │ ▣ Git  ▣ 预览 + │ ← PluginTabBar.vue
│ (Vue)    │  (Vue)               ├─────────────────┤
│          │                      │  <iframe>       │ ← 独立源 = 独立渲染进程
│          │  [Composer]          │  plugin origin  │
└──────────┴──────────────────────┴─────────────────┘
            全部在同一个 WebView2 内 · 一套 hostBridge
```

选它而不是「WPF 右侧栏里再放一个 WebView2」的理由：tab 栏 / 拖拽分隔条 / 动画全是 CSS，直接吃到
现有设计系统；没有 WPF airspace、焦点、DPI 接缝；只有一套 `hostBridge` 和一个
`WebViewMessageRouter`。进程隔离并没有丢——跨源 iframe 在 Chromium 站点隔离下本来就在独立渲染进程
里，插件卡死不会冻住 tab 栏。

原 `Controls/RightPanel.xaml`（AGENTS.md 里标注的 dead code）、`MainWindow.xaml` 的
`RightPanelColumn`/`RightPanelHost`、右栏动画代码，以及标题栏驱动它的 Files / Browser 两个按钮与
`WebViewHostCommandKind.ToggleFiles`/`ToggleBrowser`，一并删除。

---

## 2. 三层信任边界

```
Layer 3  宿主 (C#)         WebViewMessageRouter.RouteAsync
                           硬性来源门禁：只接受 e.Source 源 ==
                           https://appassets.selfclaw.local 的消息，
                           不满足就在读取 type 之前丢弃
            ▲
            │ hostBridge.request('plugin-host/...', { panelKey, ... })
            │
Layer 2  外壳 (Vue app)    usePluginPanels.js 持有全部 iframe
                           身份 = event.origin + event.source === iframeEl.contentWindow
                           【永不信任 payload 里的 pluginId】
            ▲
            │ window.parent.postMessage({...}, SHELL_ORIGIN)
            │
Layer 1  插件 (iframe)     https://<pluginId>.plugin.selfclaw.local
                           sandbox="allow-scripts allow-same-origin allow-forms allow-modals"
                           CSP 由宿主随响应头下发
                           window.selfclaw 由 AddScriptToExecuteOnDocumentCreatedAsync 注入
```

### 2.1 为什么门禁在宿主侧

WebView2 1.0.4022.49 里 iframe 能否拿到 `window.chrome.webview`，本设计**未做验证**（见 §7）。方案
刻意不依赖这个答案：`RouteAsync` 拿 `CoreWebView2WebMessageReceivedEventArgs.Source` 做硬校验，非
应用源一律丢弃。即使插件帧真能直接调 `chrome.webview.postMessage({type:'window-close'})` 或
`extensions/delete`，也拿不到任何东西。

`WebViewMessageRouterTests` 对每一种既有消息类型都断言了这一点，并覆盖缺失来源与
`appassets.selfclaw.local.evil.example` / `evil.appassets.selfclaw.local` 两种仿冒主机名。

### 2.2 `allow-same-origin` 必须给

不给的话 iframe 的源变成不透明的 `null`，`event.origin` 失去身份意义、per-plugin 的
localStorage/IndexedDB 也一起没了。`allow-scripts` + `allow-same-origin` 同时出现通常危险，是因为
同源子框架能反过来摘掉自己的 sandbox 属性；这里插件主机名与应用主机名不同，够不着父文档，所以
不成立。**这条依赖「插件源 ≠ 应用源」，改动 origin 方案时必须重新评估。**

### 2.3 CSP

由 `PluginPanelHostController.BuildContentSecurityPolicy` 生成：

```
default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline';
img-src 'self' data: blob:; font-src 'self' data:;
connect-src 'self' <已确认的 network.fetch origin>;
frame-ancestors https://appassets.selfclaw.local;
frame-src 'none'; object-src 'none'; base-uri 'none'; form-action 'none'
```

不声明 `network.fetch:` 就是 `connect-src 'self'`——插件默认完全断网。`frame-ancestors` 保证面板只
能被外壳嵌入，因此它 `postMessage` 的 parent 永远是外壳。

### 2.4 文件投递与版本钉死

两个机制配合：

1. `SetVirtualHostNameToFolderMapping("<id>.plugin.selfclaw.local", <租约版本目录>, DenyCors)` 让主机名
   可解析（`EnsureTranscriptHostAsync` 里已用过两次的成熟做法），首次打开时惰性注册。
2. `AddWebResourceRequestedFilter` + `WebResourceRequested` **自己读盘返回响应**，从而挂上 CSP、
   `X-Content-Type-Options: nosniff`、`Cache-Control`。虚拟主机映射产出的响应无法事后改头。

处理器同时是版本钉死点：路径按该面板打开时记录的租约目录解析
（`PluginPanelHostController.TryResolvePackageAsset`），更新落盘也换不掉活标签页脚下的文件。已关闭
的插件即使映射还在，也会因为 `_openPlugins` 里查不到而 404。

---

## 3. Manifest 扩展

`schemaVersion` 保持 1——`contributes.panels` 是纯增量字段。

```json
{
  "schemaVersion": 1,
  "id": "git-inspector",
  "name": "Git Inspector",
  "version": "1.0.0",
  "permissions": ["ui.panel", "host.context.read", "network.fetch:https://api.github.com"],
  "contributes": {
    "panels": [
      { "id": "changes", "title": "变更", "icon": "git-branch",
        "entry": "ui/changes/index.html", "defaultWidth": 380 }
    ]
  }
}
```

`PluginManifestReader.ValidatePanels()` 与既有的 `ValidateSkills()` / `ValidateMcpServers()` 并列，
复用同一批私有 helper：

| 规则 | 原因 |
|---|---|
| `entry` 走 `ResolvePackagePath`，必须存在且为 `.html` | 路径逃逸与包外引用 |
| `icon` 只接受 `PluginPanelIcons` 白名单里的名字 | **tab 栏渲染在应用源里，包内 SVG 就是注入面** |
| `title` ≤ 40 字符、无控制字符 | tab 栏排版与终端注入 |
| `defaultWidth` ∈ [280, 720] | 布局约束 |
| 面板 id 走 `ValidateId`，插件内唯一 | 规范 key 为 `<pluginId>/<panelId>` |
| 贡献 panel 的插件 id 必须是合法 DNS label | 面板源由插件 id 派生，否则安装能过、打开时才炸 |
| 有 panel 贡献则必须声明 `ui.panel` | 让权限确认对话框如实反映能力 |

注意 `ValidateId` 允许首尾 `-` 和 64 字符，而 DNS label 不允许——这条额外校验不能省。

### 3.1 权限文法

权限是**披露清单**，不是封闭枚举：没有执行点的未知 token 什么也不授予，而现存包已经在用
`workspace.read` / `process.execute` 这类词汇。因此 `PluginPermissions.Validate`：

- **裸 token 保持宽松**（向后兼容）；
- **带前缀的 token 严格解析**：只认 `network.fetch:`，值必须归一化为裸 origin
  （无 path/query/userinfo，非环回必须 HTTPS）；未知前缀直接拒绝。

`network.fetch:` 的 origin 写进权限串而不是塞进 contributes，是为了让它自然落进
`acknowledged_permissions_json`：插件更新时新增一个域名，权限集合就变了，现有
`ExtensionStatus.NeedsPermission` 通路会强制用户重新确认。

**归一化必须两边一致**：`PluginManifestReader` 与 `PluginContributionService.ReadPermissions` 都调用
`PluginPermissions.Validate`。若两者对同一个 manifest 得出不同结果，`acknowledged ⊇ declared` 永远
不成立，插件会变成不可启用的僵尸。

| 权限 | 授予 |
|---|---|
| `ui.panel` | 贡献右侧面板 |
| `host.context.read` | `getContext()` + `context-changed` |
| `host.transcript.read` | 订阅 transcript 流 |
| `host.composer.write` | `insertPrompt(text)`，插入不发送 |
| `host.workspace.read` | 工作区根内只读 |
| `network.fetch:<origin>` | 放开该 origin 的 `connect-src` |

v1 不做：`host.workspace.write`、插件自主发起回合、进程内代码加载。前两项要先接进
`DesktopToolApprovalHandler`，第三项永远不做。

---

## 4. 消息协议

### 外壳 ↔ 宿主（现有 hostBridge，前缀 `plugin-host/`）

| type | 返回 |
|---|---|
| `plugin-host/get-panels` | 可用面板列表 + 上次持久化的标签页 |
| `plugin-host/open` | `{ ok, panel, url }`，宿主侧 `Acquire` 版本租约并注册映射 |
| `plugin-host/close` | `{ ok }`，最后一个面板关闭时释放租约 |
| `plugin-host/save-tabs` | `{ ok }`，写 `desktop-settings.json` 的 `pluginPanels` |
| `plugin-host/api` | 具体 op 结果，宿主按 permission 校验 |
| `plugin-host/context`（推送） | `{ context }`，外壳原样转成 `context-changed` |
| `plugin-host/evict`（推送） | `{ pluginId }`，外壳关闭对应标签 |

### 插件 ↔ 外壳（`window.postMessage`）

SDK（`Assets/plugin-sdk.js`）在 `window.parent === window` 时直接返回，所以外壳自己不受影响。

```js
window.selfclaw = {
  panelKey, permissions, ready(),
  getContext(),                  // host.context.read
  on('context-changed'|'transcript'|'handshake', fn),
  insertPrompt(text),            // host.composer.write，外壳本地处理，不往返宿主
  workspace: { list, glob, read, search },  // host.workspace.read
}
```

`workspace.*` 一一对应 `IWorkspaceToolService` 的只读方法，不新写文件访问代码。
**`workspaceRootPath` 由宿主从 `MainWindowViewModel.SelectedWorkspaceRootPath` 取，插件不提供也无法
覆盖。**

`PluginPanelBridge` 的权限**从宿主状态解析**（`PluginPanelHostController.GetPermissions(panelKey)`），
不读 payload 里的 `permissions` 字段，也因此只有已打开的面板才能调用。

v1 不发 `theme-changed`：应用目前只有浅色主题，没有可广播的切换事件。

### 4.1 上下文只有一个生产者

拉（`getContext()`）与推（`context-changed`）携带**同一个 `PluginPanelContext` 记录**：

```
{ conversationId, agentId, agentName, agentMode, isBusy, workspaceRootPath, workspaceRootName }
```

它由 `MainWindowViewModel.CaptureContext()` 一处捕获，取值与 `BuildTranscriptProjectionRequest`
同源。这一条不是整洁癖：外壳曾经自己从 transcript 负载里拼一份推送用的上下文，于是推的字段集与拉的
不同，且拼出来的工作区根未必是 `workspace.*` 实际解析的那个根。

`PluginPanelContextPublisher` 同时是捕获方与推送方，监听三个信号：

| 信号 | 覆盖 |
|---|---|
| `WebViewHostChannel.TranscriptPublished` | 会话、代理、忙碌状态——它们本来就走这条发布路径 |
| 视图模型的 `PropertyChanged` | 工作区选择，它可以在没有 transcript 发布的情况下变化 |
| `PluginPanelHostController.PanelOpened` | 刚打开的面板没有历史可回放 |

前两个按记录值去重（流式期间每 120ms 一次的 transcript 发布因此不会变成 120ms 一次的插件推送），
面板打开则**跳过去重**——重启外壳后当前上下文往往与上次推送的逐字节相同，去重会正好吞掉新面板唯一
需要的那一次。推送失败（外壳尚未 ready）不记账，下一个信号仍会送达。

外壳侧 `usePluginPanels` 另外缓存最近一次 `context-changed` 与 `transcript`，在 `handshake` 时补给
新面板；空闲会话里这是它画出第一屏的唯一来源。

---

## 5. 标签页与生命周期

- key = `<pluginId>/<panelId>`；同插件多面板共享一个源、一份映射、一个租约条目（引用计数）。
- 全局一套标签栏，跨会话保持；切会话通过 `context-changed` 通知插件自行刷新，实例不重建。
- `{tabs, activeKey}` 防抖后落 `desktop-settings.json`，启动时随 `get-panels` 回填并丢弃已不可用的 key。
- 右栏宽度用可拖拽分隔条，存 localStorage；拖动期间关掉 grid 过渡并给 iframe 加
  `pointer-events: none`，否则指针会被 iframe 吃掉。
- 上限 8 个插件同时打开。

### 5.1 打开在左，隐藏在右

**打开面板只有一个入口：左侧导航的「插件」项**（`PluginLauncher`）。tab 栏右端不再有 `+`，
只有一个 `-` 隐藏按钮。这样「开哪个面板」与应用其余的导航动作待在同一处，tab 栏只管已经打开的东西。

隐藏是**外壳的视图状态，不是面板的生命周期**：标签、iframe、宿主租约全部原样留着，只是右栏这一列不
占位置。因此右栏可见 = 有标签 且 未被隐藏（`App.vue` 的 `panelVisible`），隐藏态存 localStorage
（`selfclaw:panel-hidden`），与宽度是同一类偏好。

两条约束容易在改动时踩空：

| 约束 | 原因 |
|---|---|
| `PluginPanelHost` 用 `v-show` 而非 `v-if` | `PluginFrame` 在 `onBeforeUnmount` 里注销 frame。`v-if` 会卸载 iframe，于是每次收起都让插件重新加载并重走 handshake，收起再展开不再是廉价动作 |
| 启动器里已打开的条目**必须仍然可点** | 它是隐藏态下把右栏叫回来的那条路。若沿用 `disabled`，面板全部打开又全部隐藏时右栏再也回不来 |

从启动器选一个面板即 `setPanelHidden(false)`——从左侧导航点它就是要看见它，无论此前是隐藏还是未打开。
关掉最后一个标签时 `panels.isOpen` 转假，右栏自然消失，隐藏标志留在原处不影响下次打开。

### 与启停/更新/删除的关系

`ExtensionSettingsService.DeleteAsync` 会 `AcquireDrainsAsync` 排空版本目录，而打开的面板持有租约。
若不先关面板，排空会等一个只有 UI 能释放的租约——**设置操作永久挂起**。

因此新增 Core 接口 `IPluginPanelSessionRegistry`（Desktop 由 `PluginPanelHostController` 实现），
`DeleteAsync` / `SetEnabledAsync(false)` 在排空**之前**调 `CloseAsync(pluginId)`。按既有
`IPluginVersionLeaseManager?` 的写法注册为可空可选依赖，老测试无需改动即可编译。

`CloseAsync` **不等待外壳回执**：它直接清映射、释放租约，然后推 `plugin-host/evict`。映射和租约一没，
面板就再也取不到任何字节，所以一个卡死的 iframe 拖不住设置操作。文件按请求整块读入内存返回，不留
长期句柄，目录随后可以安全删除。

更新：重新导入产生新版本目录，旧版本租约让活标签页继续跑在旧文件上直到关闭。版本哈希目录布局本来
就是为这件事设计的。

---

## 6. 测试

- `PluginManifestReaderTests` — 面板校验（路径逃逸、非 `.html`、非法图标、重复 id、非法 DNS label、
  缺 `ui.panel`）、`network.fetch:` 归一化。
- `PluginPermissionsTests` — 裸 token 向后兼容、origin 归一化、归一化后碰撞判重、非法前缀。
- `PluginPanelHostControllerTests` — 可用性过滤、租约配平（打开两个面板时排空必须阻塞）、
  `GetPermissions` 只对已打开面板返回、`CloseAsync` 释放并推送 evict、
  `TryResolvePackageAsset` 的目录containment。
- `PluginPanelContextPublisherTests` — 推与拉是同一个记录、去重（流式 transcript 不产生推送）、
  忙碌变化经 transcript 信号送达、面板打开跳过去重、外壳未 ready 时的推送不记账。
- `WebViewMessageRouterTests` — 来源门禁对每种既有消息类型、缺失来源、仿冒主机名。
- `ExtensionSettingsServiceTests` — 禁用/删除在排空前关闭面板；面板投影与 broken manifest 降级。
- `PanelPluginFixtureTests` — 用真实 installer 安装 `Fixtures/panel-demo.zip`，保证文档里的端到端
  夹具不会悄悄失效。

---

## 7. 未验证事项

规划与实现期间外网文档不可达，以下两点**未经核实**，但主设计不依赖它们：

1. **WebView2 1.0.4022.49 中 iframe 是否可见 `window.chrome.webview`**，以及
   `CoreWebView2.FrameCreated` 的逐帧 `WebMessageReceived` 是否会截走顶层投递。
   结论只影响是否值得追加纵深防御（订阅 `FrameCreated`）；§2.1 的来源门禁已独立成立。
2. **`AddWebResourceRequestedFilter` 单独能否解析未映射的主机名。** 当前同时注册了虚拟主机映射
   （负责解析）与资源处理器（负责安全头）。若核实为「能」，可以去掉逐插件映射注册，少一处
   per-CoreWebView2 的资源占用。

---

## 8. 明确不采用的方案

### 8.1 WPF 右侧栏里再放一个 WebView2

不采用。要么 tab 栏是那个 WebView2 里的 HTML（多一套 bridge/router/虚拟主机映射，还多一层
WPF↔Web 接缝），要么每插件一个控件、tab 栏用 WPF 重画（拿不到前端设计系统，N 个控件的内存/焦点/DPI
成本最高）。跨源 iframe 已经提供进程隔离，第二个 WebView2 换不来额外的隔离。

### 8.2 让插件直接调 `chrome.webview`

不采用。它把宿主的全部消息面暴露给第三方页面，而且是否可行取决于未核实的 WebView2 行为。改走
`window.parent.postMessage`，外壳是唯一持有 bridge 的一方。

### 8.3 权限里未知 token 一律拒绝

不采用。权限是披露清单，没有执行点的 token 什么也不授予；一律拒绝会打断现存包正在使用的
`workspace.read` / `process.execute` 词汇。严格性用在真正会扩权的地方：`network.fetch:` 的值。

### 8.4 面板定义单独落库

不采用。manifest 已经是持久记录，第二份副本可能与租约钉住的文件不一致。面板从 manifest 投影，
manifest 读不出来的插件贡献 0 个面板而不是让整个设置页失败。

### 8.5 `CloseAsync` 等待外壳回执后再释放

不采用。那会让一个卡死或无响应的 iframe 拖住设置操作。宿主是权威方：先拆状态，再通知。
