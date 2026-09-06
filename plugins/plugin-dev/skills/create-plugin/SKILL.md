---
name: create-plugin
description: 当用户要求创建、开发、修改或打包 SelfClaw 插件（plugin.json、右侧面板、插件贡献的 Skill / MCP / 指令）时使用。按安装校验规则生成合规的插件包结构，完成自检并给出打包与安装步骤。
version: 1.0.0
triggers:
  - 创建插件
  - 开发插件
  - 打包插件
  - plugin.json
  - SelfClaw 插件
---

# 创建 SelfClaw 插件

用户想为 SelfClaw（Windows 桌面 AI 编程助手）做一个插件。你的任务是**在当前工作区里生成一个可直接打包安装的插件目录**，并给出打包与安装步骤。SelfClaw 插件是纯静态包：一个 `plugin.json` 加上若干静态文件，安装时全量校验，不合格的包根本装不进去——所以必须严格按下面的约束生成。

## 第 0 步：确定贡献类型

一个插件包可同时贡献四类能力，先和用户确认要哪些：

| 贡献                 | 用途                                         | 何时选                   |
| -------------------- | -------------------------------------------- | ------------------------ |
| `panels`             | 右侧栏浏览器式 UI 面板（独立源 iframe）      | 用户要看得见的界面       |
| `directInstructions` | 注入 Direct 回合的系统指令                   | 给代理补充行为规范       |
| `skills`             | 带命名空间的 Skill（`<pluginId>/<skillId>`） | 提供可按需激活的操作指南 |
| `mcpServers`         | MCP 服务器（stdio 或 http）                  | 提供外部工具             |

只有 `panels` 需要 `ui.panel` 权限；其余三类不强制任何权限。插件贡献的能力只在 **Direct 模式**回合生效（面板除外，面板只属于用户），且需在「设置 → 代理助手」里把插件绑定给对应代理。

## 第 1 步：目录结构

```
<plugin-id>/
├── plugin.json                  ← 必需，唯一入口
├── ui/index.html                ← panels[].entry，必须是 .html
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
	"permissions": ["ui.panel", "host.context.read"],
	"contributes": {
		"panels": [
			{
				"id": "overview",
				"title": "我的插件",
				"icon": "sparkles",
				"entry": "ui/index.html",
				"defaultWidth": 380
			}
		]
	}
}
```

逐字段约束（安装时校验，违反即拒收）：

| 字段                    | 约束                                                                                                                                                  |
| ----------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------- |
| `schemaVersion`         | 必须是 `1`                                                                                                                                            |
| `id`                    | 小写 ASCII 字母、数字、`-`，≤64，禁止大写。贡献面板时还必须是合法 DNS label（≤63、首尾不能是 `-`），因为面板源是 `https://<id>.plugin.selfclaw.local` |
| `name` / `version`      | 非空；`name` 可以是中文                                                                                                                               |
| `permissions`           | 数组、去重。有 `panels` 就必须含 `ui.panel`                                                                                                           |
| `panels[].id`           | 同 `id` 字符规则，插件内唯一                                                                                                                          |
| `panels[].title`        | ≤40 字符，无控制字符                                                                                                                                  |
| `panels[].icon`         | 只能取图标白名单（见下），包内 SVG 一律不收                                                                                                           |
| `panels[].entry`        | 包内相对路径（禁止绝对路径与 `..`），必须存在且是 `.html`                                                                                             |
| `panels[].defaultWidth` | 280–720，缺省 360                                                                                                                                     |
| `skills[].id`           | 同 `id` 字符规则，插件内唯一                                                                                                                          |
| `skills[].path`         | 包内相对目录，目录里必须有 `SKILL.md`                                                                                                                 |

图标白名单（`icon` 只能取这些名字）：
`activity` `book-open` `bookmark` `bug` `calendar` `clipboard` `code` `database` `eye` `file-code` `file-text` `filter` `folder` `folder-open` `git-branch` `globe` `image` `info` `key` `layers` `layout-grid` `lightbulb` `link` `list` `map` `message-square` `package` `play` `puzzle` `search` `settings` `shield` `sparkles` `star` `table` `tag` `terminal` `timer` `wrench` `zap`

## 第 3 步：权限怎么选

权限是披露清单：启用时弹出确认框逐项展示，用户确认才算授权。按需最小化声明。

| 权限                     | 解锁                                                                                                                                                 |
| ------------------------ | ---------------------------------------------------------------------------------------------------------------------------------------------------- |
| `ui.panel`               | 贡献面板时必需                                                                                                                                       |
| `host.context.read`      | `getContext()` 与 `context-changed` 事件                                                                                                             |
| `host.transcript.read`   | `transcript` 事件                                                                                                                                    |
| `host.composer.write`    | `insertPrompt(text)`（只插入不发送）                                                                                                                 |
| `host.workspace.read`    | `workspace.list / glob / read / search`                                                                                                              |
| `network.fetch:<origin>` | 放开面板 CSP 的 `connect-src`，如 `network.fetch:https://api.example.com`。必须是裸 origin（无 path/query/凭据），非环回强制 HTTPS。不声明即完全断网 |

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
				/* 用 selfclaw.panelKey / selfclaw.permissions 画界面 */
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
| `panelKey`                                               | —                      | `"<pluginId>/<panelId>"`                                                                                  |
| `permissions`                                            | —                      | 宿主认定的权限数组（handshake 后）                                                                        |
| `appearance`                                             | —                      | `{ theme, mode, uiFontFamily, uiFontScale, codeFontFamily, codeFontScale }`                               |
| `getContext()`                                           | `host.context.read`    | `{ conversationId, agentId, agentName, agentMode, isBusy, workspaceRootPath, workspaceRootName }`         |
| `on('context-changed', fn)`                              | `host.context.read`    | 同一个上下文记录                                                                                          |
| `on('transcript', fn)`                                   | `host.transcript.read` | `{ items: [{ id, kind, role, status, segments[], isThinking, timestamp, attachments?, errorMessage? }] }` |
| `insertPrompt(text)`                                     | `host.composer.write`  | 文本送入输入框，**不发送**                                                                                |
| `workspace.list({ relativePath })`                       | `host.workspace.read`  | `[{ relativePath, isDirectory, sizeBytes }]`                                                              |
| `workspace.glob({ pattern, relativePath })`              | `host.workspace.read`  | 同上                                                                                                      |
| `workspace.read({ relativePath, startLine, lineCount })` | `host.workspace.read`  | `{ relativePath, content, truncated, startLine, endLine, totalLines }`                                    |
| `workspace.search({ query })`                            | `host.workspace.read`  | `[{ relativePath, lineNumber, lineText }]`                                                                |
| `on('handshake', fn)`                                    | —                      | `{ panelKey, permissions, appearance }`                                                                   |
| `ready()`                                                | —                      | 通知外壳加载完成                                                                                          |

拿不到的：写文件、执行进程、发起对话回合、访问其他插件、会话历史数据库。`workspace.*` 的根永远是宿主当前选中的工作区。

## 第 5 步：其他贡献（可选）

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

## 第 6 步：打包

把插件目录压成 `.zip`，包内必须**恰好一个** `plugin.json`（它所在目录就是包根，带不带顶层文件夹都行）：

```powershell
Compress-Archive -Path <插件目录>/* -DestinationPath <插件id>.zip -Force
```

包限制：压缩包 100 MB / 解压后 300 MB / 5000 文件 / 单文件 50 MB / `plugin.json` 与 `SKILL.md` 各 256 KB。

安装路径：**设置 → 扩展 → 插件 → 导入插件**，选中 zip。导入后默认禁用；启用即授权（确认框列出全部 permissions）。面板从右侧栏面板启动器打开，最多同时 8 个。

## 第 7 步：生成后自检清单

交付前逐条核对：

- [ ] `plugin.json` 是合法 JSON，`schemaVersion` 为 1
- [ ] `id` 与所有贡献 id 只含小写字母/数字/`-`；贡献面板时 id 是合法 DNS label
- [ ] `permissions` 无重复；有面板必有 `ui.panel`；`network.fetch:` 的值是裸 origin
- [ ] 引用的每个文件（entry、SKILL.md、instruction、MCP stdio 入口）真实存在于即将打包的目录里
- [ ] 图标名在白名单内
- [ ] `defaultWidth` 在 280–720
- [ ] 面板 HTML：无 `eval`/`new Function`、无任何 CDN/外部资源引用、无嵌套 iframe、无表单提交；要联网已声明 `network.fetch:`
- [ ] 面板 HTML 遵循两个必备模式（握手先读再订阅；上下文先拉后订阅），末尾调用 `selfclaw.ready()`

## 常见报错对照

| 安装报错                                                              | 原因                                  |
| --------------------------------------------------------------------- | ------------------------------------- |
| `A Plugin package must contain exactly one plugin.json file.`         | 包里没有或有多个 `plugin.json`        |
| `Plugin id must use lowercase ASCII letters, digits, and '-'.`        | id 含大写或非法字符                   |
| `... must also declare the 'ui.panel' permission.`                    | 有面板但权限里没写 `ui.panel`         |
| `Plugin panel icon '...' is not a supported icon name.`               | 图标不在白名单                        |
| `Panel entry must be package-relative.` / `escapes the package root.` | entry 用了绝对路径或 `..`             |
| `... entry must be an .html file.`                                    | entry 不是 `.html`                    |
| `Plugin permission '...' must be a bare origin...`                    | `network.fetch:` 带了 path/query/凭据 |

## 更新与调试

- **更新**：改完重新打包、重新导入即可。已打开的面板继续跑旧版文件直到关闭；权限集合变了插件会回到待确认状态，需重新授权。
- **调试**：焦点在应用窗口时按 F12 打开 DevTools，在 frame 下拉里选 `https://<plugin-id>.plugin.selfclaw.local` 源。`console.log` 正常工作；面板崩溃不影响外壳（独立渲染进程）。
