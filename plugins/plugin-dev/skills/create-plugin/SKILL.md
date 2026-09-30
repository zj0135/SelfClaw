---
name: create-plugin
description: 当用户要求创建、开发、修改或打包 SelfClaw 插件（plugin.json、右侧面板、悬浮视图、插件贡献的 Skill / MCP / 指令）时使用。按安装校验规则生成合规的插件包结构，完成自检并给出打包与安装步骤。
version: 1.1.0
triggers:
  - 创建插件
  - 开发插件
  - 打包插件
  - plugin.json
  - SelfClaw 插件
  - 悬浮视图
---

# 创建 SelfClaw 插件

用户想为 SelfClaw（Windows 桌面 AI 编程助手）做一个插件。你的任务是**在当前工作区里生成一个可直接打包安装的插件目录**，并给出打包与安装步骤。SelfClaw 插件是纯静态包：一个 `plugin.json` 加上若干静态文件，安装时全量校验，不合格的包根本装不进去——所以必须严格按下面的约束生成。

## 第 0 步：确定贡献类型

一个插件包可同时贡献四类能力，先和用户确认要哪些：

| 贡献                 | 用途                                             | 何时选                     |
| -------------------- | ------------------------------------------------ | -------------------------- |
| `views`              | 界面：右侧面板（`right`）或窗口悬浮层（`floating`） | 用户要看得见的界面         |
| `directInstructions` | 注入 Direct 回合的系统指令                       | 给代理补充行为规范         |
| `skills`             | 带命名空间的 Skill（`<pluginId>/<skillId>`）     | 提供可按需激活的操作指南   |
| `mcpServers`         | MCP 服务器（stdio 或 http）                       | 提供外部工具               |

视图需要权限：`right` 要 `ui.panel`，`floating` 要 `ui.floating`（两者分开披露，因为悬浮视图能覆盖整个窗口）。其余三类不强制任何权限。插件贡献的能力只在 **Direct 模式**回合生效（视图除外，视图只属于用户），且需在「设置 → 代理助手」里把插件绑定给对应代理。

## 第 1 步：目录结构

```
<plugin-id>/
├── plugin.json                  ← 必需，唯一入口
├── ui/dock.html                 ← views[].entry，必须是 .html（右侧面板）
├── ui/hud.html                  ← views[].entry（悬浮视图）
├── instructions/direct.md       ← directInstructions（可选）
├── skills/<skill-id>/SKILL.md   ← skills[].path 指向的目录（可选）
└── server/index.js              ← mcpServers 的 stdio 入口（可选）
```

## 第 2 步：plugin.json

模板（按需删减 `contributes` 里不用的段）：

```json
{
	"schemaVersion": 1,
	"id": "my-plugin",
	"name": "我的插件",
	"version": "1.0.0",
	"description": "一句话说明插件做什么。",
	"publisher": "作者名",
	"permissions": ["ui.panel", "ui.floating", "host.context.read"],
	"contributes": {
		"views": [
			{
				"id": "overview",
				"slot": "right",
				"title": "我的插件",
				"icon": "sparkles",
				"entry": "ui/dock.html",
				"defaultWidth": 380
			},
			{
				"id": "hud",
				"slot": "floating",
				"title": "悬浮提示",
				"icon": "layers",
				"entry": "ui/hud.html"
			}
		]
	}
}
```

逐字段约束（安装时校验，违反即拒收）：

| 字段                    | 约束                                                                                                                                                   |
| ----------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------ |
| `schemaVersion`         | 必须是 `1`                                                                                                                                             |
| `id`                    | 小写 ASCII 字母、数字、`-`，≤64，禁止大写。贡献视图时还必须是合法 DNS label（≤63、首尾不能是 `-`），因为视图源是 `https://<id>.plugin.selfclaw.local` |
| `name` / `version`      | 非空；`name` 可以是中文                                                                                                                                |
| `permissions`           | 数组、去重。含 `slot: "right"` 的视图就必须有 `ui.panel`，含 `slot: "floating"` 的就必须有 `ui.floating`                                               |
| `views[].id`            | 同 `id` 字符规则，**整个数组内唯一**（key 是 `<pluginId>/<viewId>`，跨 slot 也不能重名）                                                               |
| `views[].slot`          | 必填，只能是 `"right"`（右侧面板）或 `"floating"`（窗口悬浮层）                                                                                        |
| `views[].title`         | ≤40 字符，无控制字符                                                                                                                                   |
| `views[].icon`          | 只能取图标白名单（见下），包内 SVG 一律不收                                                                                                            |
| `views[].entry`         | 包内相对路径（禁止绝对路径与 `..`），必须存在且是 `.html`                                                                                              |
| `views[].defaultWidth`  | **只在 `slot: "right"` 时允许**，280–720，缺省 360；写在悬浮视图上会被判为非法                                                                        |
| `skills[].id`           | 同 `id` 字符规则，插件内唯一                                                                                                                           |
| `skills[].path`         | 包内相对目录，目录里必须有 `SKILL.md`                                                                                                                  |

图标白名单（`icon` 只能取这些名字）：
`activity` `book-open` `bookmark` `bug` `calendar` `clipboard` `code` `database` `eye` `file-code` `file-text` `filter` `folder` `folder-open` `git-branch` `globe` `image` `info` `key` `layers` `layout-grid` `lightbulb` `link` `list` `map` `message-square` `package` `play` `puzzle` `search` `settings` `shield` `sparkles` `star` `table` `tag` `terminal` `timer` `wrench` `zap`

## 第 3 步：权限怎么选

权限是披露清单：启用时弹出确认框逐项展示，用户确认才算授权。按需最小化声明。

| 权限                     | 解锁                                                                                                                                                 |
| ------------------------ | ---------------------------------------------------------------------------------------------------------------------------------------------------- |
| `ui.panel`               | 贡献右侧面板时必需                                                                                                                                   |
| `ui.floating`            | 贡献悬浮视图时必需（悬浮视图可覆盖整个主窗口，所以与面板分开披露）                                                                                   |
| `host.context.read`      | `getContext()` 与 `context-changed` 事件                                                                                                             |
| `host.transcript.read`   | `transcript` 事件                                                                                                                                    |
| `host.composer.write`    | `insertPrompt(text)`（只插入不发送）                                                                                                                 |
| `host.workspace.read`    | `workspace.list / glob / read / search`                                                                                                              |
| `network.fetch:<origin>` | 放开视图 CSP 的 `connect-src`，如 `network.fetch:https://api.example.com`。必须是裸 origin（无 path/query/凭据），非环回强制 HTTPS。不声明即完全断网 |

## 第 4 步：面板页面

面板跑在 `https://<plugin-id>.plugin.selfclaw.local` 的沙箱 iframe 里，**拿不到宿主的设计系统，样式必须自带**。`window.selfclaw` 由宿主在文档创建前注入，不需要 import、不需要构建步骤。直接用这个骨架：

```html
<!doctype html>
<html lang="zh" data-theme="light">
	<head>
		<meta charset="utf-8" />
		<title>我的插件</title>
		<style>
			:root,
			html[data-theme='light'] {
				color-scheme: light;
				--ink: #171a1f;
				--muted: #6b7280;
				--line: #e5e7eb;
				--surface: #fff;
			}
			html[data-theme='dark'] {
				color-scheme: dark;
				--ink: #e8eaf0;
				--muted: #8b93a3;
				--line: #2b303a;
				--surface: #171a20;
			}
			body {
				margin: 0;
				padding: 14px;
				background: var(--surface);
				color: var(--ink);
				font-family: var(--host-font-ui, 'Segoe UI'), system-ui, sans-serif;
				font-size: calc(13px * var(--host-ui-scale, 1));
				line-height: 1.6;
			}
		</style>
	</head>
	<body>
		<h1 style="margin:0 0 10px;font-size:15px;">我的插件</h1>
		<div id="app">加载中…</div>
		<script>
			// 必备模式一：握手可能早于也可能晚于本脚本，先读一次再订阅，两条路都覆盖。
			render();
			selfclaw.on('handshake', () => {
				render(); // 此时 selfclaw.permissions 才有值
				applyAppearance(selfclaw.appearance || {});
			});

			// 必备模式二：先拉一次画第一屏，再订阅变化。拉和推是同一个数据形状。
			selfclaw.getContext().then(render).catch(showError);
			selfclaw.on('context-changed', render);

			// 宿主只报外观事实（theme 已解析为 light/dark），配色由面板自己给。
			function applyAppearance(a) {
				document.documentElement.dataset.theme = a.theme === 'dark' ? 'dark' : 'light';
				const s = document.documentElement.style;
				s.setProperty('--host-font-ui', a.uiFontFamily || '');
				s.setProperty('--host-ui-scale', String(a.uiFontScale ?? 1));
			}
			selfclaw.on('appearance-changed', applyAppearance);

			function render() {
				/* 用 selfclaw.viewKey / selfclaw.permissions 画界面 */
			}
			function showError(error) {
				document.getElementById('app').textContent = error.message;
			}

			selfclaw.ready(); // 告诉外壳加载完成；不调用也能工作
		</script>
	</body>
</html>
```

### SDK 速查（`window.selfclaw`）

| 调用                                                     | 权限                   | 返回 / 行为                                                                                               |
| -------------------------------------------------------- | ---------------------- | --------------------------------------------------------------------------------------------------------- |
| `viewKey`                                                | —                      | `"<pluginId>/<viewId>"`                                                                                    |
| `slot`                                                   | —                      | `"right"` 或 `"floating"`                                                                                   |
| `permissions`                                            | —                      | 宿主认定的权限数组（handshake 后）                                                                        |
| `appearance`                                             | —                      | `{ theme, mode, uiFontFamily, uiFontScale, codeFontFamily, codeFontScale }`                               |
| `getContext()`                                           | `host.context.read`    | `{ conversationId, agentId, agentName, agentMode, isBusy, workspaceRootPath, workspaceRootName }`         |
| `on('context-changed', fn)`                              | `host.context.read`    | 同一个上下文记录                                                                                          |
| `on('transcript', fn)`                                   | `host.transcript.read` | `{ items: [{ id, kind, role, status, segments[], isThinking, timestamp, attachments?, errorMessage? }] }` |
| `insertPrompt(text)`                                     | `host.composer.write`  | 文本送入输入框，**不发送**                                                                                |
| `close()`                                                | —                      | 关闭自己这个视图（外壳本地处理）                                                                          |
| `layout.anchors`                                         | —                      | 五个宿主地标矩形（见第 5 步），每个键都可能为 `null`                                                      |
| `on('anchors-changed', fn)`                              | —                      | 布局变化后重新推送同一份地标                                                                              |
| `layout.setInteractive(rects \| null)`                   | —                      | 手动声明交互矩形；传 `null` 回到自动模式                                                                  |
| `workspace.list({ relativePath })`                       | `host.workspace.read`  | `[{ relativePath, isDirectory, sizeBytes }]`                                                              |
| `workspace.glob({ pattern, relativePath })`              | `host.workspace.read`  | 同上                                                                                                      |
| `workspace.read({ relativePath, startLine, lineCount })` | `host.workspace.read`  | `{ relativePath, content, truncated, startLine, endLine, totalLines }`                                    |
| `workspace.search({ query })`                            | `host.workspace.read`  | `[{ relativePath, lineNumber, lineText }]`                                                                |
| `on('handshake', fn)`                                    | —                      | `{ viewKey, slot, permissions, appearance, anchors }`                                                     |
| `on('hit-released', fn)`                                 | —                      | 指针已不再命中你的交互区域（清 hover 态用；幂等处理，不必区分是谁发起的）                                 |
| `ready()`                                                | —                      | 通知外壳加载完成                                                                                          |

拿不到的：写文件、执行进程、发起对话回合、访问其他插件、会话历史数据库。`workspace.*` 的根永远是宿主当前选中的工作区。

`transcript` 条目的 `status` 取值：`streaming` / `completed` / `failed` / `cancelled` / `truncated` / `blocked`（被插件 hook 阻止）。
`segments[].kind` 取值：`content` / `thinking` / `tool` / `notice`；`notice` 是回合级 hook 通知，纯文本，不走 markdown。

## 第 5 步：悬浮视图（`slot: "floating"`）

悬浮视图是**一层覆盖主对话区（转录 + 输入区 + 终端那一列）的透明 iframe**：你可以在自己的视口里把 UI 放到任意位置。它不会盖住左侧导航与右侧面板——这既是产品约定，也是指针机制成立的前提：外壳的命中测试跑在窗口 `pointermove` 上，而实测指针位于子 iframe 上时父文档收不到任何 `pointermove`，所以悬浮层一旦与面板重叠就会变成「点了没反应」。它与右侧面板共用同一套沙箱、CSP、权限与 `window.selfclaw`，区别只有坐标与指针。

**坐标就是你自己的视口**：插件文档的视口等于主对话列，`position: fixed; right: 16px` 就是「离这一列右边缘 16px」。列会随侧栏折叠、右栏开合变宽变窄，页面按当前可用宽度自动重排，不会有一半内容藏在面板下面。宿主的五个地标由 `selfclaw.layout.anchors` 给出——**它们已经换算到你的坐标系里**，直接可用（不要硬编码偏移，布局变化会移动它们）：

| 地标       | 含义（数值在你自己的视口坐标系里）              |
| ---------- | --------------------------------------------- |
| `titlebar` | 顶部 46px 标题栏条带（在你视口之外，通常是负 y） |
| `sidebar`  | 左侧导航栏（负 x）                            |
| `stage`    | 转录舞台                                      |
| `composer` | 输入区                                        |
| `dock`     | 右侧面板列（关闭时为 `null`）                 |

每个键都可能为 `null`（切到设置页时 `stage`/`composer` 不存在），必须处理。

**指针默认全透明**：未声明的像素对宿主完全透明（点击、悬停、滚轮、拖动都不受影响）。要有交互，必须把控件标上 `data-selfclaw-interactive`（或调 `selfclaw.layout.setInteractive(rects)` 做 canvas 自绘命中测试）：

```html
<!doctype html>
<html lang="zh">
	<head>
		<meta charset="utf-8" />
		<style>
			/* 根背景由宿主默认设为透明，这里只需要画自己的卡片。 */
			html,
			body {
				margin: 0;
				height: 100%;
			}
			.hud {
				position: fixed;
				right: 14px;
				width: 232px;
				padding: 10px 12px;
				border-radius: 12px;
				background: rgba(255, 255, 255, 0.96);
				box-shadow: 0 14px 40px rgba(15, 23, 42, 0.18);
				font: 12px/1.5 'Segoe UI', system-ui;
			}
		</style>
	</head>
	<body>
		<!-- 这个属性就是「这里的点击归我」的声明；不加则整块卡片都点不到。 -->
		<div class="hud" data-selfclaw-interactive>
			<strong>构建状态</strong>
			<div id="status">等待上下文…</div>
			<button id="run">把提示词送进输入框</button>
			<button id="close">关闭</button>
		</div>
		<script>
			function place() {
				const composer = selfclaw.layout.anchors && selfclaw.layout.anchors.composer;
				document.querySelector('.hud').style.bottom = composer ? window.innerHeight - composer.y + 12 + 'px' : '14px';
			}
			place();
			selfclaw.on('anchors-changed', place);

			document.getElementById('run').onclick = () => selfclaw.insertPrompt('检查构建');
			document.getElementById('close').onclick = () => selfclaw.close();
			selfclaw.getContext().then((c) => (document.getElementById('status').textContent = c.agentName || '（无会话）'));
			selfclaw.on('context-changed', (c) => (document.getElementById('status').textContent = c.agentName || '（无会话）'));
			selfclaw.ready();
		</script>
	</body>
</html>
```

三条必须遵守的约定：

1. **交互控件必须标 `data-selfclaw-interactive`**（或有 canvas 时调 `setInteractive`），否则点不到；未声明矩形的悬浮视图只能看不能碰。
2. **不要依赖视图内部的 `pointer-events` 穿透回宿主**：矩形之外由宿主保证穿透，矩形之内穿透不到宿主。
3. **交互控件要顺着 `anchors` 摆**：视口宽度会变（侧栏折叠、右栏开合），硬编码的窗口坐标偏移会在布局变化后错位或跑到视口外——跑到视口外的像素既看不见也点不到。

打开与关闭：悬浮视图从左侧导航的「插件」启动器打开（面板打开最多 8 个、悬浮视图最多 4 个）。关闭可以用自己的按钮调 `selfclaw.close()`，也可以让用户在启动器里关。

用户可以用标题栏的图层按钮**整体收起**悬浮层。收起期间你的文档视口与坐标系仍然有效（外壳用 `visibility` 隐藏，不会把视口压成 0×0），所以不必为「被收起」写分支；但**位置计算要放在 `anchors-changed`（和 `handshake`）里，不要只在启动时算一次**——侧栏折叠、面板开合、窗口缩放都靠这条事件重算。

## 第 6 步：其他贡献（可选）

### directInstructions

一个 markdown 文件，内容整体作为 `[plugin:<id>]` 分节注入 Direct 回合的系统指令。写行为规范，不写 UI。

### skills

每个 skill 是一个含 `SKILL.md` 的目录。`SKILL.md` 以 YAML front matter 开头：

```markdown
---
name: <skill-id>
description: 一句话说明何时用（会出现在 Skill 目录里供模型判断）。
version: 1.0.0
triggers:
  - 触发词一
  - 触发词二
---

正文 = 激活后注入给模型的完整指南。
```

`name` 必须是小写 ASCII 字母/数字/`-`/`_`（可用 `/` 分层，段 ≤64，总长 ≤256）。最终对外 id 是 `<pluginId>/<贡献id>`，用户用 `[/pluginId/贡献id]` 显式激活，模型也可通过 `activate_skill` 按需激活。**贡献 id 不要和已安装独立 Skill 重名**——冲突会让绑定该插件的回合直接降级。

### mcpServers

- `transport: "stdio"`：`command` + `arguments`（字符串数组）。可用 `${pluginRoot}`、`${workspaceRoot}` 模板（不许有其他 `${...}`），`.dll` 入口一律拒收；裸命令（如 `node`）从 PATH 解析。
- `transport: "http"`：`endpoint` 必须是绝对 http/https 地址，非环回强制 HTTPS，禁止携带凭据；`transportMode` 可选 `auto`/`streamableHttp`/`sse`；`connectionTimeoutSeconds` 1–300。
- `requiredSettings`：stdio 只能 target `env`，http 只能 target `header`；每项 `{ key, target, secret }`，用于向用户收集密钥类配置。

## 第 7 步：打包

把插件目录压成 `.zip`，包内必须**恰好一个** `plugin.json`（它所在目录就是包根，带不带顶层文件夹都行）：

```powershell
Compress-Archive -Path <插件目录>/* -DestinationPath <插件id>.zip -Force
```

包限制：压缩包 100 MB / 解压后 300 MB / 5000 文件 / 单文件 50 MB / `plugin.json` 与 `SKILL.md` 各 256 KB。

安装路径：**设置 → 扩展 → 插件 → 导入插件**，选中 zip。导入后默认禁用；启用即授权（确认框列出全部 permissions）。视图从左侧导航的「插件」启动器打开。

## 第 8 步：生成后自检清单

交付前逐条核对：

- [ ] `plugin.json` 是合法 JSON，`schemaVersion` 为 1
- [ ] `id` 与所有贡献 id 只含小写字母/数字/`-`；贡献视图时 id 是合法 DNS label
- [ ] `permissions` 无重复；`slot: "right"` 的视图有 `ui.panel`，`slot: "floating"` 的有 `ui.floating`
- [ ] 每个视图都写了 `slot`，取值只有 `right` / `floating`；`defaultWidth` 只出现在 `right` 上
- [ ] 引用的每个文件（entry、SKILL.md、instruction、MCP stdio 入口）真实存在于即将打包的目录里
- [ ] 图标名在白名单内；`defaultWidth` 在 280–720
- [ ] 页面 HTML：无 `eval`/`new Function`、无任何 CDN/外部资源引用、无嵌套 iframe、无表单提交；要联网已声明 `network.fetch:`
- [ ] 页面 HTML 遵循两个必备模式（握手先读再订阅；上下文先拉后订阅），末尾调用 `selfclaw.ready()`
- [ ] 悬浮视图的交互控件都标了 `data-selfclaw-interactive`（或用了 `setInteractive`），并且用 `selfclaw.layout.anchors` 定位（不用硬编码窗口坐标，否则布局变化后会跑出视口）
- [ ] 悬浮视图用 `selfclaw.layout.anchors` 定位，并对 `null` 地标有兜底

## 常见报错对照

| 安装报错                                                              | 原因                                       |
| --------------------------------------------------------------------- | ------------------------------------------ |
| `A Plugin package must contain exactly one plugin.json file.`         | 包里没有或有多个 `plugin.json`             |
| `Plugin id must use lowercase ASCII letters, digits, and '-'.`        | id 含大写或非法字符                        |
| `... must declare a slot of 'right' or 'floating'.`                   | 视图没写 `slot`（或写了空串）              |
| `... slot 'bottom' is not supported; use 'right' or 'floating'.`      | slot 取值不在枚举里                        |
| `... is not docked, so it must not declare defaultWidth.`             | 悬浮视图上写了 `defaultWidth`              |
| `... must also declare the 'ui.panel' permission.`                    | 有 `right` 视图但没写 `ui.panel`           |
| `... must also declare the 'ui.floating' permission.`                 | 有 `floating` 视图但没写 `ui.floating`     |
| `Plugin view icon '...' is not a supported icon name.`                | 图标不在白名单                             |
| `View entry must be package-relative.` / `escapes the package root.`  | entry 用了绝对路径或 `..`                  |
| `... entry must be an .html file.`                                    | entry 不是 `.html`                         |
| `Duplicate Plugin view id '...'.`                                     | 视图 id 在数组内重复（跨 slot 也算重复）   |
| `Plugin id '...' cannot host views: ...`                              | id 是合法包 id 但不是合法 DNS label        |
| `Plugin permission '...' must be a bare origin...`                    | `network.fetch:` 带了 path/query/凭据      |

## 更新与调试

- **更新**：改完重新打包、重新导入即可。已打开的视图继续跑旧版文件直到关闭；权限集合变了插件会回到待确认状态，需重新授权。
- **调试**：焦点在应用窗口时按 F12 打开 DevTools，在 frame 下拉里选 `https://<plugin-id>.plugin.selfclaw.local` 源。`console.log` 正常工作；插件崩溃不影响外壳（独立渲染进程）。悬浮视图调试时注意：看不到指针事件多半是控件没标 `data-selfclaw-interactive`。
