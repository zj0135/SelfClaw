# 我的第一个 SelfClaw 插件

一个可以直接打包安装的示例插件。它只做一件事：把宿主给面板的上下文原样显示出来。没有实际用途，
目的是让你看清 **SelfClaw 插件的典型结构** 和 **完整的开发流程**。

完整的系统设计见 [plugin-panel-system-design.md](../plugin-panel-system-design.md)；
Plugins / Skills / MCP 三类能力的设计见 [direct-extensions-system-design.md](../direct-extensions-system-design.md)。

---

## 1. 目录结构

```
my-first-plugin/
├── plugin.json          ← 清单。整个插件唯一的必需文件
├── ui/
│   └── index.html       ← 面板入口，必须是 .html
└── README.md            ← 你正在看的这份（打包时会一起进包，无害）
```

这是最小结构。一个"典型"的完整插件会长成这样：

```
some-plugin/
├── plugin.json
├── instructions/direct.md        ← contributes.directInstructions
├── skills/
│   └── presentation/SKILL.md     ← contributes.skills[].path
├── server/index.js               ← contributes.mcpServers[] 的 stdio 入口
└── ui/
    ├── index.html                ← contributes.panels[].entry
    ├── app.js                    ← 自带，不能从 CDN 取
    └── style.css
```

一个包可以同时贡献这四类东西，装一次就都生效。本示例只用了 `panels`。

---

## 2. plugin.json 逐字段

```json
{
  "schemaVersion": 1,
  "id": "my-first-plugin",
  "name": "我的第一个插件",
  "version": "1.0.0",
  "description": "……",
  "publisher": "selfclaw-docs",
  "permissions": ["ui.panel", "host.context.read", "host.transcript.read"],
  "contributes": {
    "panels": [
      { "id": "overview", "title": "我的第一个插件", "icon": "sparkles",
        "entry": "ui/index.html", "defaultWidth": 380 }
    ]
  }
}
```

| 字段 | 约束 |
|---|---|
| `schemaVersion` | 必须是 `1` |
| `id` | 小写字母、数字、`-`，≤64；**贡献面板时还必须是合法 DNS label**（≤63、首尾非 `-`），因为面板的源由它派生 |
| `name` / `version` | 非空。`name` 可以是中文 |
| `permissions` | 有面板就必须含 `ui.panel`。裸 token 宽松，`network.fetch:` 严格校验 |
| `panels[].id` | 同 `id` 规则，插件内唯一。最终 key 是 `<pluginId>/<panelId>` |
| `panels[].title` | ≤40 字符，无控制字符 |
| `panels[].icon` | 只能取白名单里的名字，见 [`PluginPanelIcons.cs`](../../SelfClaw.Infrastructure/Extensions/Plugins/PluginPanelIcons.cs)（`sparkles` `git-branch` `terminal` `code` `search` … 共 40 个）。**包内的 SVG 一律不收**——tab 栏渲染在应用源里，接受包内图形等于给每个插件一个注入面 |
| `panels[].entry` | 包内相对路径，必须存在且是 `.html` |
| `panels[].defaultWidth` | 280–720，缺省 360 |

这些规则全部在**安装时**校验（[`PluginManifestReader.cs`](../../SelfClaw.Infrastructure/Extensions/Plugins/PluginManifestReader.cs)），
不合格的包装不进去，而不是等你打开面板才炸。

---

## 3. 打包与安装

**打包** —— 压成 `.zip`（或 `.selfclaw-plugin`）。包内必须**恰好一个** `plugin.json`，它所在的目录就是包根，
所以带不带顶层文件夹都行。

```powershell
# PowerShell，在仓库根目录执行
Compress-Archive -Path docs/my-first-plugin/* -DestinationPath my-first-plugin.zip -Force
```

```bash
# 或者 git bash
cd docs/my-first-plugin && zip -r ../../my-first-plugin.zip . && cd -
```

包限制：压缩包 100 MB / 解压后 300 MB / 5000 文件 / 单文件 50 MB / `plugin.json` 256 KB。

**安装** —— 应用里 **设置 → 扩展 → 插件 → 导入插件**，选中 zip。

**启用** —— 导入后**默认是禁用状态**。这是有意的：启用那一刻才是用户授予权限的时刻，确认对话框会列出
`permissions` 里的每一项。没启用的插件，它的面板根本不会出现在面板列表里。

**打开** —— 右侧栏的面板启动器里选它。最多同时打开 8 个插件。

**更新** —— 重新导入新版本。已经打开的面板会**继续跑在旧版本的文件上**直到关闭，不会被中途换掉；
如果新版本的权限集合变了，插件会回到"待确认"状态，需要重新授权。

---

## 4. UI 侧能拿到什么

`window.selfclaw` 由宿主在文档创建前注入，**不需要 import，也不需要构建步骤**。

| 调用 | 需要的权限 | 拿到 |
|---|---|---|
| `selfclaw.panelKey` | — | `"my-first-plugin/overview"` |
| `selfclaw.permissions` | — | 宿主认定的权限数组（handshake 后才有值） |
| `await selfclaw.getContext()` | `host.context.read` | 见下方上下文记录 |
| `selfclaw.on('context-changed', fn)` | `host.context.read` | **同一个**上下文记录 |
| `selfclaw.on('transcript', fn)` | `host.transcript.read` | `{ items: [...] }` |
| `selfclaw.on('handshake', fn)` | — | `{ panelKey, permissions }` |
| `selfclaw.insertPrompt(text)` | `host.composer.write` | 往输入框插入文本，**不发送** |
| `selfclaw.workspace.list({ relativePath })` | `host.workspace.read` | `[{ relativePath, isDirectory, sizeBytes }]` |
| `selfclaw.workspace.glob({ pattern, relativePath })` | `host.workspace.read` | 同上 |
| `selfclaw.workspace.read({ relativePath, startLine, lineCount })` | `host.workspace.read` | `{ relativePath, content, truncated, startLine, endLine, totalLines }` |
| `selfclaw.workspace.search({ query })` | `host.workspace.read` | `[{ relativePath, lineNumber, lineText }]` |
| `selfclaw.ready()` | — | 告诉外壳加载完成 |

上下文记录（拉和推是同一个形状）：

```js
{
  conversationId,      // 当前会话 id，未选中时为 null
  agentId, agentName,  // 当前代理
  agentMode,           // "cli" | "direct"
  isBusy,              // 是否有回合正在进行
  workspaceRootPath,   // 当前工作区根，由宿主决定，插件无法指定或覆盖
  workspaceRootName,
}
```

`transcript` 的 `items[]` 每项形如
`{ id, kind, role, status, segments[], isThinking, timestamp, attachments?, errorMessage? }`。

**拿不到**：写文件、执行进程、发起对话回合、别的插件、宿主的任意消息面、会话历史数据库。
`workspace.*` 解析的根永远是宿主当前选中的那个。

### 两个必须知道的模式

**先拉后订阅。** 推送只在上下文真正变化时发出，空闲会话里可能长时间没有下一次。所以：
`getContext()` 拉一次画出第一屏，再 `on('context-changed')` 跟进变化。两条路给的是同一个记录，
render 函数不用区分来源。

**握手可能比你的脚本早，也可能晚。** `selfclaw.permissions` 先直接读一次，再订阅 `handshake`——
两条路都覆盖，才不会出现"权限区永远显示等待中"。示例里 `renderIdentity()` 就是这么写的。

---

## 5. 运行环境的约束

面板跑在 `https://my-first-plugin.plugin.selfclaw.local` 这个**独立源**里，因此有独立的渲染进程和
独立的 localStorage / IndexedDB 分区（跨会话保留）。CSP 由宿主随响应头下发：

- 内联 `<script>` / `<style>` **可以**；
- `eval` / `new Function` **不行**（带运行时编译器的框架会直接挂）；
- 任何 CDN、外部脚本、外部样式**不行**，依赖必须打进包里；
- 不能再嵌套 iframe，不能提交表单；
- 不声明 `network.fetch:` 就是**完全断网**。

要联网，在 `permissions` 里加 `"network.fetch:https://api.example.com"`（必须是裸 origin，非环回强制
HTTPS）。注意它**只放开 `connect-src`**——`fetch`/XHR/WebSocket 通了，但远程图片仍被 `img-src` 拦，
需要 fetch 成 blob 再用。

---

## 6. 调试

WebView 的 DevTools 是开着的：焦点在应用窗口时按 **F12**，在 frame 下拉里选你的插件源。右键菜单被
禁用了，所以不能用"检查元素"。

`console.log` 正常工作。面板里的报错不会影响外壳——跨源 iframe 在独立渲染进程里，插件卡死也冻不住
tab 栏。

---

## 7. 装不进去时对照这张表

| 报错 | 原因 |
|---|---|
| `A Plugin package must contain exactly one plugin.json file.` | 包里没有或有多个 `plugin.json` |
| `Plugin id must use lowercase ASCII letters, digits, and '-'.` | id 有大写或非法字符 |
| `... panels, so it must also declare the 'ui.panel' permission.` | 贡献了面板但 `permissions` 里没写 `ui.panel` |
| `Plugin panel icon '...' is not a supported icon name.` | 图标不在白名单里 |
| `Panel entry must be package-relative.` / `escapes the package root.` | entry 用了绝对路径或 `..` |
| `Plugin panel '...' entry must be an .html file.` | entry 不是 `.html` |
| `Plugin permission '...' must be a bare origin such as https://api.example.com.` | `network.fetch:` 的值带了 path/query/凭据 |

---

## 8. 再往前走一步

想让这个示例做点实事，按需要往 `permissions` 里加，然后在 `ui/index.html` 里调用：

- `"host.workspace.read"` → `selfclaw.workspace.list({})` 列出工作区根目录
- `"host.composer.write"` → `selfclaw.insertPrompt('一段文字')` 把内容送进输入框
- `"network.fetch:https://api.github.com"` → 面板里直接 `fetch`

改完 `permissions` 后要重新打包、重新导入，并**重新确认权限**——权限集合变了，插件会回到待确认状态。

仓库里还有一个更小的端到端夹具在
[`SelfClaw.Tests/Infrastructure/Extensions/Fixtures/panel-plugin/`](../../SelfClaw.Tests/Infrastructure/Extensions/Fixtures/panel-plugin/)，
它由 `PanelPluginFixtureTests` 用真实安装器跑通，可以当作第二个参考。
