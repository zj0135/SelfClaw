# 插件视图系统设计（Plugin Views：右侧面板 + 窗口悬浮视图）

> 状态：**已实施**（2026-09-30）。实施后的状态、测试证据与未实机验收的限制见 `docs/desktop-architecture-review.md` §6。
> 本文**取代** `docs/plugin-panel-system-design.md`（已删除；其中仍然有效的内容——三层信任边界、CSP、版本租约与文件钉死、测试要求——已并入本文）。
> 前提：当前是开发阶段，**不做任何数据兼容**。`desktop-settings.json` 的相关节点、localStorage 偏好键、旧的 `panel-demo.zip` 夹具都可以直接清空/重建。

---

## 0. 核心结论

1. 新增第二类视图位置 slot：`floating`。它给插件一层**覆盖主对话区（主内容列）的透明 iframe**，插件在自己的坐标系里任意摆放任意 UI，并通过宿主推送的**地标矩形**对齐宿主布局。悬浮层不覆盖侧栏与右侧面板——这既是产品要求，也是命中测试成立的前提（见 §2.1、§2.3）。
2. **不是新系统。** 把现有 `contributes.panels` 泛化为 `contributes.views` + `slot`，右侧面板原样保留（`slot: "right"`）。安装校验、版本哈希目录、权限确认、版本租约、虚拟主机映射、CSP 与安全头、资源读取、`plugin-host/*` 宿主通道、shell↔插件的 `postMessage` 协议、禁用/删除时的 evict 与租约排空，**全部只有一份实现**。
3. 悬浮视图与右侧面板**共用同一条 frame 管道**：一个 view = 一个 `<iframe>` = 一个 key（`<pluginId>/<viewId>`）= 一条 host session 引用。身份（`event.origin` + `event.source === frame.contentWindow`）、握手、`context` / `transcript` / `appearance` 推送、租约引用计数一行都不用改。
4. 与面板唯一的实质差异是**指针模型**：悬浮层默认 `pointer-events: none`，只有插件**显式声明**的交互矩形在被指针命中时，宿主才把那一帧临时设为可交互。没有声明的像素对所有输入（点击、悬停、滚轮、拖动）完全透明。
5. 宿主额外把 5 个地标矩形作为「几何事实」推给插件——与 `appearance`「送事实、不送样式」是同一个设计语言，同一批推送管线，不新增通道、不设权限门。

**明确边界**：悬浮视图是**窗口坐标系的叠加层，不参与布局流**。它不能把内容插进转录消息、不能推动宿主布局。那类诉求需要宿主渲染的声明式贡献（消毒 DOM 或 shadow DOM 沙箱），属于另一份设计，本设计不覆盖（见 §12.3）。

---

## 1. 概念与命名

| 概念 | 含义 | 承载 |
| --- | --- | --- |
| **view** | 插件贡献的一块 UI，key = `<pluginId>/<viewId>` | `plugin.json` |
| **slot** | 视图被放去哪里：`right` / `floating` | `plugin.json` |
| **dock** | 右侧标签栏 + 面板列，承载所有 `right` 视图 | `PluginDockHost.vue` |
| **floating layer** | 覆盖窗口的悬浮层，承载所有 `floating` 视图 | `PluginFloatingLayer.vue` |
| **frame** | 一个视图对应的沙箱 iframe | `PluginFrame.vue` |

开发阶段无兼容负担，一次性把「panel」这个只对右栏成立的词从通用概念里换掉。`plugin-host/` 消息前缀不变（它本来就是「插件 ↔ 宿主」），但字段与类型名统一：

| 旧 | 新 |
| --- | --- |
| `contributes.panels` | `contributes.views`（元素新增 `slot`） |
| `PluginPanelView` | `PluginView`（新增 `Slot`、`DefaultWidth` 变可空） |
| `IPluginPanelCatalog` | `IPluginViewCatalog` |
| `IPluginPanelSessionRegistry` | `IPluginViewSessionRegistry` |
| `PluginPanelHostController` | `PluginViewHostController` |
| `PluginPanelResourceReader` | `PluginViewResourceReader` |
| `OpenPluginPanelSession` | `OpenPluginViewSession` |
| `PluginPanelBridge` / `PluginPanelContext` / `PluginPanelContextPublisher` | `PluginViewBridge` / `PluginViewContext` / `PluginViewContextPublisher` |
| `PanelOpened` 事件 | `ViewOpened` |
| `PersistedPluginTabs` / settings 节点 `pluginPanels` | `PersistedPluginViews` / `pluginViews` |
| `plugin-host/get-panels` `open` `close` `save-tabs` | `plugin-host/get-views` `open` `close` `save-views` |
| 字段 `panelKey`、查询参数 `__selfclaw_panel` | `viewKey`、`__selfclaw_view` |
| `usePluginPanels.js` / `PluginPanelHost.vue` / `PluginLauncher` 里的 panel 命名 | `usePluginViews.js` / `PluginDockHost.vue` / view 命名 |
| `ExtensionSettingsState.Panels` | `ExtensionSettingsState.Views` |
| 本地偏好键 `selfclaw:panel-width` / `selfclaw:panel-hidden` | `selfclaw:dock-width` / `selfclaw:dock-hidden` |

保留「面板」这个词的地方只有 UI 文案与 dock 相关组件（`PluginDockHost` / `PluginTabBar` / `PluginTab`）。

---

## 2. 承载模型、坐标空间与指针模型

```
┌───────────────────────────────────────────────────────────┐  z-index
│ WindowControls（窗口按钮 + 面板开关 + 悬浮开关）  46px 条带 │  460  ← 恒定在悬浮层之上
├──────────┬──────────────────────────────┬─────────────────┤
│ Sidebar  │ 主对话列 .main-content       │ ▣ Dock 面板     │  内容层
│          │ ┌──────────────────────────┐ │                 │
│          │ │ PluginFloatingLayer      │ │  ← 只覆盖主对话列：到不了
│          │ │ position:absolute;inset:0│ │     侧栏、标题栏、右栏
│          │ │ pointer-events:none      │ │  450
│          │ │  → 仅「被命中交互矩形」的 │ │
│          │ │    那一帧临时 auto       │ │
│          │ │  <iframe> 悬浮视图 A     │ │
│          │ │  <iframe> 悬浮视图 B     │ │
│          │ └──────────────────────────┘ │                 │
├──────────┴──────────────────────────────┴─────────────────┤
│ PluginLauncher 500 · 图片预览 1000 · 确认框 1250 · Toast 1300 · 缩放热区 9999
└───────────────────────────────────────────────────────────┘
```

### 2.1 坐标不变式（评审必守）

悬浮层的宿主元素为 `position: absolute; inset: 0`，挂在 `.main-content`（主对话列：转录 + 输入区 + 终端）里，包含块就是这一列。`.main-content` 的 `overflow: hidden` 顺带保证插件画不出这一列。

1. 插件文档的视口 = 主对话列的盒子。列随侧栏折叠、右栏开合而变宽变窄，插件因此总是按"当前可用宽度"重排，不会有一半视口藏在面板下面。
2. 插件坐标 = 主对话列的局部坐标。宿主地标在窗口坐标里测量，推送前**换算到每个帧自己的坐标系**（`usePluginFrames.localizeAnchors`），所以插件作者不需要关心原点差异；反过来，插件上报的交互矩形按自己的坐标存储，命中测试再把指针换算进同一个坐标系（`useFloatingPointer.hitTest`）。
3. WebView2 的 zoom factor 对整个页面统一生效，插件帧与应用共享同一缩放，因此两者内部 CSS 像素一致。

`.main-content` 是 `position: relative` 且**不得**引入 `transform` / `filter` / `contain` / `will-change: transform`——那会把 `absolute` 的包含块换掉，同时也让坐标换算失效。这条要求写进 `PluginFloatingLayer.vue` 的注释。

**为什么必须是"主对话列"而不是整个窗口**：实测指针位于子 iframe 上时父文档收不到任何 `pointermove`（§13.1），而外壳的命中测试跑在窗口 `pointermove` 上。悬浮层若与右栏的插件帧重叠，落在重叠处的指针会让命中测试从不运行 → 那一帧永远不会被唤醒 → 点了没反应，只有关掉右栏才行。限定在主对话列（这一列里没有其它 iframe）是让整套机制成立的前提，不是审美选择。

### 2.2 z 序（单一权威表）

| 层 | z-index | 理由 |
| --- | --- | --- |
| 主对话列内的应用内容（转录、输入区、终端、活动面板、各种菜单） | ≤ 449 | — |
| **PluginFloatingLayer** | 450 | 插件在主对话内容之上（它在 `.main-content` 内，几何上到不了侧栏、标题栏与右栏） |
| PluginLauncher | 500 | 第二道逃生口：能打开它就一定能关掉任意悬浮视图 |
| 图片预览 / 确认框 / Toast | 1000 / 1250 / 1300 | 安全与提示 UI 永远压住插件 |
| 窗口缩放热区 | 9999 | 既有实现，悬浮层不能吃掉缩放 |

标题栏（46px）与右栏都在 `.main-content` 之外，插件既画不到也点不到，所以「顶部条带归宿主」这条旧限制随悬浮层一起消失了：窗口拖动、窗口按钮、面板开关、悬浮开关**永远可点**，是「插件卡住界面」的第一道逃生口。

副作用（写进作者文档）：主对话列内**宿主自己的**弹出层（模型选择、工作区菜单等）在 z 序上低于悬浮层，插件浮层显示期间可能压住它们；用户总开关是标题栏那颗图层按钮。

### 2.3 指针模型（本设计的核心）

**为什么不能让整层可交互 + 插件自己穿透**：父文档里 `<iframe>` 元素上的 `pointer-events: none` 会跳过整棵子文档，子文档里的 `auto` **无法**把它救回来（跨文档命中测试不回落）；反过来整层 `auto` 时，空白像素会吃掉宿主的点击与悬停。因此唯一可行的模型是「默认透明 + 显式矩形 + 按需武装」。

**命中测试算法**（外壳侧，`useFloatingPointer.js`）：

1. 层与每个帧默认 `pointer-events: none`。未武装时窗口 `pointermove` 派发给宿主元素，事件目标是应用自己的 DOM。
2. 外壳在 `window` 上以 **capture** 阶段监听 `pointermove`（并 `{ passive: true }`，避免被 `stopPropagation` 影响），对每个悬浮帧的交互矩形做几何命中测试，取 DOM 顺序**最上面**的一个（层内子元素顺序 = `floatingViews` 的顺序，后渲染者在上；命中测试从后往前）。命中变化时：上一帧置 `none`，新帧置 `auto`（"武装"，armed）。仅存在悬浮视图时才安装该监听。
3. 已武装时事件进入插件文档。**实测父文档在指针位于子 iframe 上时收不到任何 `pointermove`**，所以「目标不是被武装的帧就解除武装」只能拦住指针跑到宿主自己的上层元素（对话框、启动器，它们盖在主对话列之上）上的情形；帧内的离开交给第 4 步。
4. 插件侧（SDK）在武装状态下每次 `pointermove` 判断指针是否仍在自己的矩形内，一旦离开就发 `hit-regions-release`，通知宿主解除武装（同时清自己的 hover 态）。这是**主要路径**：悬浮帧盖住整个主对话列，指针仍在帧内但已离开矩形时，这条消息是外壳唯一的依据。
5. 宿主强制解除武装：窗口 `blur`、停靠栏拖动开始、视图关闭、插件被 evict、整个悬浮层被隐藏。

**并发与焦点语义**（与右侧面板保持一致，不新增机制）：

- 点击悬浮控件会把键盘焦点交给插件文档，此时宿主快捷键（Esc 等）暂时不可用——与今天点击右侧面板完全相同。
- 滚轮在矩形内归插件，矩形外正常滚宿主。
- 矩形外的悬停/拖动/右键一律穿透到宿主。
- 只有一个帧会被武装，不存在两个悬浮视图同时吃指针的情形。

**与 `.app.resizing` 的关系**：App.vue 里已有 `.app.resizing iframe { pointer-events: none }`（拖动分隔条时防止 iframe 吃掉指针）。悬浮帧的开关是内联样式，会覆盖该规则，所以**不要**依赖 CSS：`useFloatingPointer` 接收 `isSuspended: () => boolean`（App 传 `() => resizing.value`），暂停期间一律计算为 `none`。

---

## 3. Manifest

```json
{
  "schemaVersion": 1,
  "id": "hud-demo",
  "name": "HUD Demo",
  "version": "1.0.0",
  "permissions": ["ui.panel", "ui.floating", "host.context.read"],
  "contributes": {
    "views": [
      { "id": "inspector", "slot": "right", "title": "变更", "icon": "git-branch",
        "entry": "ui/inspector.html", "defaultWidth": 380 },
      { "id": "hud", "slot": "floating", "title": "悬浮 HUD", "icon": "layers",
        "entry": "ui/hud.html" }
    ]
  }
}
```

`PluginManifestReader.ValidateViews`（替换 `ValidatePanels`）与既有 `ValidateSkills` / `ValidateMcpServers` 并列，复用同一批私有 helper：

| 规则 | 说明 |
| --- | --- |
| `slot` 必填，取值 `right` \| `floating` | 未知值安装即拒，错误消息列出合法取值 |
| `views[].id` | 走 `ValidateId`，**数组内全局唯一**（key 是 `<pluginId>/<viewId>`，跨 slot 重名同样是冲突） |
| `title` ≤ 40、无控制字符 | 同今天（launcher、tab、设置页都要渲染） |
| `icon` | `PluginPanelIcons` 白名单（改名 `PluginViewIcons`），不接受包内 SVG |
| `entry` | `ResolvePackagePath` + 必须存在 + `.html` |
| `defaultWidth` | 仅 `right` 允许：280–720，缺省 360。**`floating` 上出现 `defaultWidth` 直接报错**，不静默忽略 |
| slot ↔ 权限 | `right` 需要 `ui.panel`，`floating` 需要 `ui.floating`，任一缺失即拒 |
| DNS label | 贡献任意 slot 的插件 id 必须是合法 DNS label（源由 id 派生） |

`schemaVersion` 仍为 1：`views` 是纯增量字段，且开发阶段允许清空旧数据。

`slot` 在 C# 里是枚举 `PluginViewSlot { Right, Floating }`，用与 `ExtensionStatus` 相同的写法（`[JsonConverter]` + `JsonStringEnumConverter` + kebab-case-lower）保证 WebView 线上格式是 `"right"` / `"floating"`，不依赖宿主全局序列化选项。

---

## 4. 权限

权限是**披露清单**：启用时逐项展示，用户确认才算授权。新增一个 token，其余不变。

| 权限 | 授予 | slot |
| --- | --- | --- |
| `ui.panel` | 贡献右侧停靠面板 | `right` |
| **`ui.floating`** | **贡献覆盖主窗口的悬浮视图（可覆盖整个应用并截获其上的点击，故单独披露）** | `floating` |
| `host.context.read` | `getContext()` / `context-changed` | 两者 |
| `host.transcript.read` | `transcript` 事件 | 两者 |
| `host.composer.write` | `insertPrompt(text)`（插入不发送） | 两者 |
| `host.workspace.read` | 工作区根内只读 | 两者 |
| `network.fetch:<origin>` | 放开该面板 CSP 的 `connect-src` | 两者 |

`ui.floating` 单独成 token 而不是复用 `ui.panel`：悬浮视图能在用户没看它的时候覆盖整个界面并截获点击，确认对话框必须如实披露，否则「启用即授权」就不再是知情同意。**不做**：`ui.floating` 不隐含任何 workspace/host 能力，反之亦然。

**不设权限门的两类事实**（沿用 `appearance` 的先例）：

- **anchors**：只是窗口里的几个矩形，与会话内容无关，任何视图都该能对齐宿主。
- **交互矩形**：是插件自己的几何，不授予任何宿主数据（它只决定指针在何处进入插件自己的文档）。

---

## 5. 宿主（C#）改动

### 5.1 Core / Infrastructure

- Core：`PluginView`（`Key, PluginId, ViewId, Title, Icon, Slot, Origin, Url, DefaultWidth?, Enabled, Status, Permissions, NetworkOrigins`）、`PluginViewSlot` + converter、`IPluginViewCatalog.ListPluginViewsAsync`、`IPluginViewSessionRegistry`、`ExtensionSettingsState.Views`，`PluginPermissions.Floating = "ui.floating"`。
- Infrastructure：`RawPluginViewContribution` / `PluginViewContribution`（含 `Slot`、可空 `DefaultWidth`）、`PluginManifestReader.ValidateViews`、`ExtensionCatalog.ListPluginViewsAsync`（投影 slot，排序规则不变）。

### 5.2 Desktop 宿主

`PluginViewHostController` 对 slot 的认知**只有三处**：

1. 可用性过滤：`Enabled && Status == Ready`（不变）。
2. 上限（**单一事实来源，全部在宿主**）：`MaximumOpenDockedViews = 8`、`MaximumOpenFloatingViews = 4`。超出即返回错误，外壳直接显示宿主给的消息——顺手删掉 `usePluginPanels.js` 里重复的那份 `MAX_TABS`，不再同一规则两处实现。
3. `viewKey` 前缀取 pluginId（不变）。

以下全部**不因 slot 分支**（明确列出，防止实现时走样）：origin 与虚拟主机映射、版本租约引用计数、`plugin-host/evict` 与 `CloseAsync`（禁用/删除前先拆状态、不等外壳回执）、CSP 与安全头、`PluginViewResourceReader` 的并发与体积限制、`GetPermissions(viewKey)`、`PluginViewContextPublisher`（`ViewOpened` 触发跳过去重的强制推送）、设置页详情抽屉的投影。

| 消息 | 变化 |
| --- | --- |
| `plugin-host/get-views` | 返回 `{ views, openViews, activeView }`（`views` 含 `slot`） |
| `plugin-host/open { viewKey }` | 返回 `{ view, url }`，url 查询参数 `__selfclaw_view` |
| `plugin-host/close { viewKey }` | 不变 |
| `plugin-host/save-views { views, activeView }` | 取代 `save-tabs` |
| `plugin-host/api { viewKey, op, args }` | 字段改名，op 集合不变 |
| `plugin-host/context`（推送） | 不变 |
| `plugin-host/evict`（推送） | 不变 |

`WebViewMessageRouter` 的 `plugin-host/` 前缀分支与来源门禁（`e.Source` 必须是应用源，在读 `type` 之前判定）**不动**。

---

## 6. 外壳（Vue）结构

今天 `usePluginPanels.js` 一个文件同时管「与宿主谈判」和「与帧通信」，加上 slot 分发、anchors、命中测试后必然膨胀。按关注点拆开，由一个 facade 收口，`App.vue` 的用法与今天同形（`const pluginViews = usePluginViews()`）：

| 文件 | 关注点 |
| --- | --- |
| `composables/usePluginViews.js` | **facade**：组合下面三个，对外只暴露一个对象 |
| `composables/usePluginViewHost.js` | 与宿主谈判：可用视图、open/close、持久化、evict、错误显示 |
| `composables/usePluginFrames.js` | 帧注册表与身份（`event.origin` + `event.source`）、握手、`context`/`transcript`/`appearance`/`anchors` 推送、插件 `notice` 分发 |
| `composables/useLayoutAnchors.js` | 测量 5 个地标（`[data-anchor]`），`ResizeObserver` + `resize` + rAF 合并 |
| `composables/useFloatingPointer.js` | 交互矩形与武装状态、命中测试、`isSuspended` |

| 组件 | 变化 |
| --- | --- |
| `components/Plugins/PluginDockHost.vue` | 原 `PluginPanelHost.vue`；标签栏 + dock 帧容器（宽度变量、`background: var(--panel)` 留在这一层） |
| `components/Plugins/PluginFloatingLayer.vue` | **新增**：`fixed inset:0` 透明层，逐帧绑定 `pointerEvents` |
| `components/Plugins/PluginFrame.vue` | 只负责 iframe、sandbox 属性与帧注册（`onMounted` / `onBeforeUnmount` 注册与注销）；定位、背景、`display` 交给各自的容器 |
| `components/Plugins/PluginTabBar.vue` / `PluginTab.vue` | 字段 `tab.panel.*` → `tab.view.*`；其余不变 |
| `components/Plugins/PluginLauncher.vue` | 两段列表（面板 / 悬浮视图）；行点击 = 打开或显示；已打开的行带 `×` 关闭按钮（悬浮视图没有标签栏，这是宿主侧保证的关闭入口） |
| `components/Chat/WindowControls.vue` | 新增第三个工具按钮：悬浮层显隐开关（`Layers` 图标），状态存 localStorage。亮起仅当「确有悬浮视图且正在显示」；两颗插件按钮都只是显隐开关，没有可显隐的内容时用禁用态表达——“打开视图”只有左侧「插件」那一个入口 |
| `App.vue` | 装配：dock 列、悬浮层、启动器；给地标加 `data-anchor`；把 `resizing` 作为 `isSuspended` 传给命中测试 |

**卸载语义**：dock 与悬浮层都**不卸载** iframe（否则每次收起都让插件重新加载并重走握手）；只有 `close`、evict、插件禁用/删除才真正卸载。两边的收起手法不同，而且必须不同：停靠栏是 `display: none`（“这一列不存在”是真的布局状态，`dock` 地标随之变 `null`），悬浮层是 `visibility: hidden`（这一列还在，只是不画）。悬浮层用 `visibility` 不是性能选择而是正确性：`display: none` 会让帧的视口变成 0×0、`frame.getBoundingClientRect()` 变成 (0,0)，于是插件测到的几何与外壳换算给它的 anchors 全部失真，而重新显示时没有任何事件能让它重算。

**逻辑上仍是一份实现**：帧注册表、身份判定、握手回填、四类推送、evict 处理全部在 `usePluginFrames` 里，dock 与悬浮层只是同一批帧的两种摆放方式。`PluginFrame.vue` 里不得出现 slot 判断。

---

## 7. 几何协议

### 7.1 anchors（外壳 → 插件，事实推送）

```js
{
  titlebar: { x, y, width, height },   // .main-header 的 46px 条带
  sidebar:  { x, y, width, height },   // 侧栏
  stage:    { x, y, width, height },   // 转录舞台（ActivityStage）
  composer: { x, y, width, height },   // 输入区（ComposerPanel）
  dock:     { x, y, width, height } | null   // 右侧面板列，未打开时为 null
}
```

- **单位是收到它的那个帧自己的视口像素**（不是窗口像素）：外壳在窗口坐标里测量，推送前按帧的 `getBoundingClientRect()` 减去原点（`localizeAnchors`）。插件因此永远不需要关心自己被摆在哪里。
- 每个键**都可能为 `null`**（切换到设置页时 `stage`/`composer` 不存在），插件必须处理 `null`；位于视口之外的地标会有负坐标（例如针对悬浮视图时 `titlebar`），这是正常值。
- 与 `appearance` 一样出现在 `handshake` 里（首帧即可用），随后经 `anchors-changed` 推送。
- 推送时机：窗口 `resize`、插件面板打开/关闭、侧栏折叠、面板宽度拖动、主视图切换；`ResizeObserver` 观察地标元素，rAF 合并，按**换算后的记录值**逐帧去重（沿用 `PluginPanelContextPublisher` 的去重思路，但**这一层完全在外壳里**，不新增 C# 通道——只有外壳能量 DOM 矩形）。
- dock 与悬浮视图**都**收到 anchors（同一条推送路径，不做 slot 分支；差异只在换算后的原点）。
- 地标集合是**封闭的 5 个名字**，实现方式是各组件在自己根元素上加 `data-anchor`；`useLayoutAnchors` 只查询这 5 个选择器。加第六个是应用侧的增量改动，不破坏插件。

### 7.2 交互矩形（插件 → 外壳）

- 矩形用插件自己的视口坐标上报。外壳按原样存储——**不换算成窗口坐标**：帧的原点会随侧栏/右栏变化而移动，而插件不会为此重新上报；命中测试反过来把指针位置换算进每个候选帧的坐标系（`hitTest`），这样帧移动后矩形依然正确。
- **自动模式**：SDK 取 `[data-selfclaw-interactive]` 元素的可见矩形并集；`ResizeObserver`（观察被标记元素）+ `MutationObserver`（body 子树/属性）+ `scroll`(capture) + `transitionend` + `resize` 触发重算，rAF 合并，序列化结果变化才发送。
- **手动模式**：`selfclaw.layout.setInteractive(rects)` 直接提交矩形，`setInteractive(null)` 回到自动模式。给 canvas / WebGL / 自绘命中测试的插件留的出口。
- **默认无矩形** → 纯视觉，不可能拦截任何输入。这是刻意的安全默认：一个有 bug 的插件不会把用户锁在应用之外。
- 线上格式（插件 → 外壳，fire-and-forget）：

```js
{ kind: 'notice', type: 'hit-regions',         payload: { rects: [{ x, y, width, height }] } }
{ kind: 'notice', type: 'hit-regions-release' }
```

  `notice` 是新增的第三种帧→外壳消息种类（现有为 `hello` / `ready` / `request`），它是通用的 fire-and-forget 通道，以后新增同类通知不必再加 kind。

- 外壳校验（payload 来自第三方页面，不得信任）：最多 64 个矩形、坐标必须是有限数、宽高非负、clamp 到窗口范围、向外取整（避免 1px 抖动）。`right` 视图发来的同类通知一律忽略。
- 外壳 → 插件：新增事件 `hit-released`（武装被解除时告知插件，用于清理 hover 态）。规则是**每次解除恰好一条通知**：无论解除是插件自己要求的（`hit-regions-release`）还是外壳判定的（指针移到上层元素、窗口 `blur`、`visibilitychange`、开始拖分隔条、层被隐藏、视图关闭/evict），插件收到的都是同一条消息，因此处理器写成**幂等**即可，不必区分发起方。
  这一条是刻意的去依赖：翻转父文档的 `pointer-events` 后，子文档是否会收到配对的 `pointerout` 属未验证行为（§13.1），而插件自己维护的 hover 态（hover 类名、hover 展开的 popover）并不会因浏览器重算 `:hover` 而复位。武装本身不需要通知——插件看到自己的元素收到 `pointerenter` 就知道了。
- 宿主侧的命中测试、武装/解除时机与并发语义见 §2.3。

---

## 8. 作者模型（`plugin-sdk.js`）

```
window.selfclaw = {
  viewKey,        // "<pluginId>/<viewId>"（原 panelKey）
  slot,           // 'right' | 'floating'（握手后可得）
  permissions, appearance, ready(),
  getContext(), insertPrompt(text), workspace.{list,glob,read,search},
  close(),        // 关闭自己这个视图：外壳本地处理，不往返宿主
  layout: {
    get anchors(),                  // 只读快照，可能含 null
    setInteractive(rects | null),   // 手动交互矩形；null 回自动模式
  },
  on(type, fn),   // handshake | context-changed | transcript | appearance-changed
                  // | anchors-changed | hit-released
}
```

- `handshake.payload` = `{ viewKey, slot, permissions, appearance, anchors }`。
- 外观与几何的统一约定：宿主只送**事实**（`theme` 已解析、`anchors` 是当前矩形），配色与摆位由插件决定。
- 悬浮视图的 SDK 注入一个根透明背景（`html, body { background: transparent }`，可被插件覆盖），避免默认白底盖住整个应用。
- 关闭自己走 `view.close` 请求，由外壳处理（它持有视图状态），**不**新增宿主消息类型。

最小示例（把 HUD 贴在输入区上方）：

```html
<!doctype html>
<html lang="zh">
<head><meta charset="utf-8"><style>
  html, body { margin: 0; height: 100%; font: 12px/1.5 'Segoe UI', system-ui; }
  .hud { position: fixed; right: 12px; width: 220px; padding: 8px 10px; border-radius: 10px;
         background: #111c; color: #fff; backdrop-filter: blur(6px); }
  .hud button { width: 100%; }
</style></head>
<body>
  <div class="hud" data-selfclaw-interactive>
    <strong>构建状态</strong>
    <div id="status">等待上下文…</div>
    <button id="run">把提示词送进输入框</button>
  </div>
  <script>
    // 悬浮视图：自己的坐标就是窗口坐标；用 anchors 对齐宿主地标。
    function place() {
      const composer = selfclaw.layout.anchors?.composer;
      const hud = document.querySelector('.hud');
      hud.style.bottom = composer ? (window.innerHeight - composer.y + 12) + 'px' : '12px';
    }
    place();
    selfclaw.on('anchors-changed', place);

    document.getElementById('run').onclick = () => selfclaw.insertPrompt('检查构建');
    selfclaw.getContext().then((c) => {
      document.getElementById('status').textContent = c.agentName || '（无会话）';
    });
    selfclaw.on('context-changed', (c) => {
      document.getElementById('status').textContent = c.agentName || '（无会话）';
    });

    selfclaw.ready();
  </script>
</body>
</html>
```

三条必须写进作者文档的约定：

1. 交互控件必须标 `data-selfclaw-interactive`（或走 `setInteractive`），否则点不到。
2. 不要在悬浮视图内部依赖 `pointer-events` 穿透回宿主——矩形之外由宿主保证穿透，矩形之内穿透不到。
3. 交互控件不要放进顶部 46px 条带（那里归窗口拖拽与控制按钮）。

---

## 9. 生命周期、持久化、上限与逃生口

- **打开入口只有一个**：左侧导航的「插件」→ `PluginLauncher`，列表分两段（面板 / 悬浮视图）。行点击 = 打开或显示；已打开的行有 `×` 关闭。悬浮视图没有标签栏，所以启动器是宿主侧保证的关闭入口；插件也可以自己调 `selfclaw.close()`。
- **持久化**（开发阶段直接换形状，不迁移）：`desktop-settings.json` 的 `pluginViews` 节点：

```json
{ "views": ["hud-demo/hud", "git-inspector/changes"], "activeView": "git-inspector/changes" }
```

  外壳本地偏好（localStorage）：`selfclaw:dock-width`、`selfclaw:dock-hidden`、`selfclaw:floats-hidden`。启动时按持久化的 `views` 逐一恢复（dock 恢复 `activeView` 与隐藏态；悬浮视图除 `floats-hidden` 外一律恢复并重走握手）。
- **上限**：dock ≤ 8、floating ≤ 4（宿主执行），因此最多 12 个插件帧；每个帧一条 host session 引用，租约计数规则不变。
- **逃生口**（三条，覆盖「插件盖住整个窗口」）：
  1. 标题栏恒在悬浮层之上 → 关窗/最小化/最大化、面板开关、悬浮层显隐开关永远可点；
  2. 启动器（z 500）在悬浮层之上 → 能打开它就一定能关掉任意悬浮视图；
  3. 设置页可禁用/删除插件——宿主先拆映射与租约再推 evict，卡死的帧拖不住设置操作（既有实现）。
- 悬浮层显隐是**外壳视图状态**，不是生命周期：帧与租约留着；再打开不走重载。收起用 `visibility: hidden`（见上），因此收起期间帧的视口尺寸与原点仍然有效：插件的 `window.innerWidth/innerHeight`、`getBoundingClientRect()` 与收到的 anchors 全部照常可用，不需要为“被收起”单写分支。从启动器打开一个悬浮视图会取消整体隐藏（否则用户点完看不到任何东西）。
- 职责边界（避免三个入口做同一件事）：“打开/关闭哪个视图”只在左侧「插件」的启动器里；“看得见/看不见”只在标题栏的两颗开关里。后者没有可显隐的内容时是**禁用态**（tooltip 告诉用户去左侧「插件」打开），不弹启动器：按钮从来不假扮成另一个入口。
- 多个悬浮视图在层内按 **viewKey 稳定排序**渲染，不做置顶/重排（移动 iframe 元素会重载文档）。重叠由插件作者避免，写进文档。

---

## 10. 安全分析

**不变的既有不变式**（继续成立，改动不得削弱）：

- 三层信任边界：`WebViewMessageRouter.RouteAsync` 的应用源硬门禁（读 `type` 之前判定）→ 外壳用 `event.origin` + `event.source === frame.contentWindow` 判身份、**永不信任 payload 里的 pluginId** → 插件帧沙箱 `allow-scripts allow-same-origin allow-forms allow-modals` + 宿主下发的 CSP。
- `allow-same-origin` 依然必需（否则源变成不透明 `null`，身份与 per-plugin 存储一起丢），依然只在「插件源 ≠ 应用源」成立时安全。
- 文件按请求整块读入内存返回、路径 containment、版本目录钉死、每个资源持有自己的租约。
- 权限从宿主状态解析（`GetPermissions(viewKey)`），不读 payload；未打开时返回 `null`。

**新增面与处理**：

| 风险 | 处理 |
| --- | --- |
| 悬浮视图覆盖整个应用并截获点击 | 默认 `pointer-events: none`；只有显式矩形生效；标题栏 / 启动器 / 确认框 / Toast / 缩放热区恒定在其上；提供「隐藏全部悬浮视图」开关 |
| 恶意或写错的矩形（NaN、负数、超大、海量） | 外壳侧校验：≤ 64 个、有限数、非负、clamp 到窗口；不合法即整批丢弃 |
| 用假矩形假装宿主控件 | 矩形只是宿主自己的几何回读，没有身份含义；插件无法借此获得任何宿主数据或调用 |
| 取走键盘焦点 | 与今天点击右侧面板完全一致的行为，不新增机制；宿主安全 UI（审批栏、确认框）仍然可用鼠标操作 |
| 盖住宿主安全提示 | z 序表保证：插件恒在确认框/Toast/审批栏之下 |
| 悬浮视图之间互相干扰 | 稳定顺序、无重排；文档要求避免重叠；上限 4 个 |
| 资源耗尽 | 一个视图一个帧、上限 12 个帧、租约引用计数与资源读取并发限制复用既有实现 |
| 依赖坐标不变式的代码被无意破坏 | §2.1 的不变式写进 `PluginFloatingLayer.vue` 注释；评审清单固定 |

---

## 11. 复用与清理清单

**复用（不新增第二份）**：安装/校验/权限确认/版本租约/CSP/资源读取、`plugin-host/*` 宿主通道、shell↔插件 `postMessage` 协议与身份判定、帧注册表与握手、context/transcript/appearance 推送管线、evict 与租约排空、设置页投影、`PluginLauncher`、`PluginFrame`。

**删除或合并（不留「保留」状态）**：

- `contributes.panels` 及 `RawPluginPanelContribution` / `PluginPanelContribution` / `PluginPanelIcons`（改名） / `PluginPanelOrigin`（改名） / `PluginPanelView` / `PluginPanelCatalog` 等旧命名（改名而非并存）。
- `usePluginPanels.js` 里的 `MAX_TABS`（与宿主上限重复）与 `save-tabs`。
- `PluginPanelHost.vue`（被 `PluginDockHost.vue` 取代）、`PluginFrame.vue` 里的定位与背景（移到各自容器）。
- `docs/plugin-panel-system-design.md`（内容并入本文后删除）、`Fixtures/panel-plugin` + `panel-demo.zip`（重建为 `view-plugin` + `view-demo.zip`）。
- AGENTS.md 的「插件面板」段落改写为「插件视图」（面板 + 悬浮视图）。

**明确不新增**：第二条 transport、第二条推送管线、第二个 WebView2、每视图独立控制器、anchors 的宿主侧实现、dock 与悬浮各一份的帧管理逻辑。

---

## 12. 明确不采用

1. **同源 DOM 注入 / 在应用源里执行插件代码**：与既有「进程内代码加载永远不做」一致。注入不受信 DOM 到应用源需要自建消毒器、交互回传协议、样式隔离，并把 origin 门禁的意义抹掉。
2. **每视图多个 iframe（surface 模型）**：宿主为每个「面板块」造一个小 iframe，指针穿透天然正确，但破坏「1 key ↔ 1 frame」的身份/注册表不变式，插件状态还要跨帧协调，多进程成本换不来能力。悬浮层用一帧 + 显式矩形即可覆盖同样的诉求。
3. **悬浮视图参与布局流**（把内容插进转录/消息内部）：需要宿主渲染的消毒 DOM 或 shadow DOM 沙箱，属独立的声明式贡献设计，本文不覆盖，也不预留半成品接口。
4. **整层可交互 + 插件内部穿透**：见 §2.3，跨文档命中测试不回落，做不到。
5. **悬浮视图置顶/点击时抬升**：移动 iframe 元素会重载文档并重走握手。
6. **切到设置页/其他主视图时自动隐藏悬浮视图**：一刀切会打断插件的状态感知；改由插件按 `context` 自行决定，用户侧提供总开关。
7. **为 anchors 单开 C# 通道**：只有外壳能量 DOM 矩形，走既有的外壳→帧事件通道即可。
8. **`ui.floating` 复用 `ui.panel`**：能力不同，披露必须如实（§4）。

---

## 13. 测试

- `PluginManifestReaderTests`：`views` 校验——未知 slot、`floating` 带 `defaultWidth`、跨 slot 的重复 id、缺 `ui.panel` / `ui.floating`、非法 icon、entry 逃逸或非 `.html`、非法 DNS label。
- `PluginViewHostControllerTests`（原 `PluginPanelHostControllerTests`）：按 slot 的上限、可用性过滤、租约配平、`GetPermissions` 对两种 slot 都只对已打开视图返回、`CloseAsync` 释放并推 evict、`TryResolvePackageAsset` 目录 containment。
- `PluginViewConcurrencyTests`：沿用既有的「两个视图打开时排空必须阻塞」等竞态用例，夹具带 slot。
- `PluginViewContextPublisherTests`：推与拉同一记录、去重、`ViewOpened` 跳过去重、外壳未 ready 不记账。
- `ExtensionSettingsServiceTests`：禁用/删除前先 evict（含悬浮视图）、投影含 slot、broken manifest 降级不贡献任何视图。
- `PluginViewFixtureTests`（原 `PanelPluginFixtureTests`）：用真实 installer 安装同时含两种 slot 的夹具包，保证文档里的端到端示例不会悄悄失效。
- `WebViewMessageRouterTests`：来源门禁对全部 `plugin-host/*` 消息类型（含新类型）继续断言。
- Vue（`tests/pluginViews.test.js`）—— 宿主会话与帧管道：在途合并、失效响应、persist 负载形状、伪造来源拒绝、握手携带 slot/anchors、anchors 变化推送、悬浮帧命中唤醒与释放、非法短形丢弃、暂停期不唤醒、evict 关闭帧并解除武装、`view.close` 与权限拒绝。
- Vue（`tests/pluginViewComponents.test.js`）—— 启动器两段列表与关闭入口、悬浮层逐帧 `pointer-events`、停靠栏只显示活动帧且不卸载其他帧。
- Vue（`tests/pluginViewIcons.test.js`）—— 前后端图标白名单不漂移。
- Vue（`tests/pluginSdk.test.js`）—— 插件作者契约：悬浮 slot 才启用交互矩形机制（未标的元素不发布、面板 slot 完全不发布）、`data-selfclaw-interactive` 自动发布、离开矩形立即 release、`hit-released` 送达处理器、`anchors` 推送与 `null` 地标、`close()` 走外壳本地处理、根透明默认值只注入悬浮视图。
- Playwright（`tests/e2e/activity.spec.js`）—— 真实 Chromium 下的几何命中：未声明区域的像素穿透、指针进入声明的矩形才唤醒该帧、离开后重新穿透；以及截图里那条 bug 的回归——右栏与悬浮同时打开时悬浮层正好等于 `.main-content` 的盒子（右栏左侧），且仍可被唤醒并命中。
- 图标白名单防漂移（既有小瑕疵）：C# 白名单与 `renderers/pluginIcons.js` 的映射是两份，`pluginViewIcons.test.js` 断言两者键集合一致，避免以后新增图标只改一边。
- `hit-released` 的语义：每次解除武装恰好一条通知（无论发起方），处理器幂等。设计初稿把这条写进了规范但实现漏了接线，现已补齐，并由 `pluginViews.test.js`（两条路径各一次）与 e2e（真实浏览器下插件文档确实收到）双重钉住。

需要 `SELFCLAW_DESKTOP_SMOKE=1` 的人工项：真实 WebView2 下的透明合成、指针武装/解除的实际手感（含拖动分隔条、打开对话框、切主视图）、DPI 缩放与最大化/还原后的 anchors 正确性。

### 13.1 未验证事项（主设计不依赖它们）

规划期未做真机验证，以下三点**不改变设计**，只影响实现细节：

1. ~~指针位于 iframe 上时父文档是否继续收到 `target === frame 元素` 的 `pointermove`~~ —— **已实测：不会**（Playwright/Chromium 探针：宿主区 2 次 pointermove，指针移到 iframe 上后仍是 2 次）。后果有三条，均已落进设计：① 命中测试只在指针位于宿主自己的元素上时运行，因此悬浮层必须停在主对话列里、不得与右栏的插件帧重叠（§2.1）；② 「目标不是被武装的帧就解除武装」只对宿主自己的上层元素有效，帧内的离开由插件的 `hit-regions-release` 负责（§2.3）；③ 帧内不再有第二条兜底路径，所以 SDK 的离开检测与 `hit-released` 回执是必须的，不是可选优化。
2. **子框架未声明背景时的合成行为**（预期透明）。若不是透明，SDK 已为悬浮视图注入根透明背景（§8），因此不构成阻塞风险。
3. **父文档把 `pointer-events` 从 `auto` 翻回 `none` 时，子文档是否收到 `pointerout`/`pointerleave`**。设计不依赖它：外壳在解除武装时主动发 `hit-released`（§7.2）。

---

## 14. 实施顺序

| 阶段 | 内容 | 完成标准 |
| --- | --- | --- |
| 1 | Core / Infrastructure：`views` + slot 校验、模型与接口改名、设置投影 | `SelfClaw.Tests` 里 manifest / catalog / settings 用例通过 |
| 2 | Desktop 宿主：控制器改名、按 slot 上限、消息改名、evict 路径 | 宿主与并发测试通过；`panelKey` / `pluginPanels` 全仓无残留 |
| 3 | 外壳：composables 拆分、`PluginDockHost`、`PluginFloatingLayer`、启动器两段、WindowControls 开关、`data-anchor` 地标、持久化键 | `npm test` 通过；dock 行为与今天逐项一致（回归） |
| 4 | SDK 与作者文档：`viewKey`/`slot`/`handshake`/`anchors`/`setInteractive`/`close`、`create-plugin` SKILL 更新、夹具与 zip 重建 | 手动装一个悬浮示例插件，能在窗口任意位置摆 UI 并正确穿透 |
| 5 | 文档收口：删除 `docs/plugin-panel-system-design.md`、更新 AGENTS.md 的插件段落、Desktop review 记录本次改动与限制 | 文档与代码一致 |

每个阶段独立可编译、可测试；阶段 3 之后 dock 面板的行为必须与今天无差别（除名称）。

**实施结果（2026-09-30）**：五个阶段均已落地。阶段 3 的回归由 Playwright 的 `plugin column confines the dock...` 与全量 Vue 测试覆盖；阶段 4 额外加入了真实 Chromium 下的悬浮层穿透/唤醒验证。实施后发现的唯一几何回归是悬浮层根元素默认可命中（无悬浮视图时会吃掉全窗口点击），修复为根元素自带 `pointer-events: none`，并由上述 e2e 用例钉住。

---

## 15. 确认结论（均已按上述方向采纳）

1. **命名**：通用概念统一叫 `view`，slot 取 `right` / `floating`；全面重命名（含 Core/DTO/JS/测试/夹具），不留旧名并存。是否接受这次跨文件改名？
2. **权限**：新增 `ui.floating`，与 `ui.panel` 分开披露。
3. **标题栏规则**：悬浮视图可以**显示**在顶部 46px 条带，但那里没有指针事件（窗口拖拽 + 控制按钮归宿主）。是否接受这条「任意位置」的例外？
4. **交互矩形默认**：未声明即完全穿透（安全默认），交互控件必须标 `data-selfclaw-interactive` 或调 `setInteractive`。是否接受这个强制约定？
5. **上限**：dock 8 / floating 4，全部由宿主执行（删掉 JS 里重复的那份）。
6. **不参与布局流**：悬浮视图无法把内容插进转录消息/推动布局，这是本设计的明确边界（§12.3）。
7. **持久化**：`pluginViews` 节点 + 三个 localStorage 键，换形状不迁移；并新增标题栏的「隐藏全部悬浮视图」开关。
8. **渲染顺序**：多个悬浮视图按 viewKey 稳定排序、不支持置顶（避免 iframe 重载）。
