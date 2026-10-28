---
name: create-plugin
description: 当用户要求创建、开发、修改或打包 SelfClaw 插件（plugin.json、插件贡献的 Skill / MCP / 指令）时使用。按安装校验规则生成合规的插件包结构，完成自检并给出打包与安装步骤。
version: 1.2.0
triggers:
  - 创建插件
  - 开发插件
  - 打包插件
  - plugin.json
  - SelfClaw 插件
---

# 创建 SelfClaw 插件

用户想为 SelfClaw（Windows 桌面 AI 编程助手）做一个插件。你的任务是**在当前工作区里生成一个可直接打包安装的插件目录**，并给出打包与安装步骤。SelfClaw 插件是纯静态包：一个 `plugin.json` 加上若干静态文件，安装时全量校验，不合格的包根本装不进去——所以必须严格按下面的约束生成。

插件不提供界面。不要生成 `contributes.views`、面板 HTML、悬浮层或 `window.selfclaw` 页面。那些能力已经从宿主移除；清单里如果还写着 `views` 或 `permissions`，安装时会被忽略，不会变成界面。

## 第 0 步：确定贡献类型

一个插件包可同时贡献三类能力，先和用户确认要哪些：

| 贡献                 | 用途                         | 何时选                   |
| -------------------- | ---------------------------- | ------------------------ |
| `directInstructions` | 注入 Direct 回合的系统指令   | 给代理补充行为规范       |
| `skills`             | 带命名空间的 Skill（`<pluginId>/<skillId>`） | 提供可按需激活的操作指南 |
| `mcpServers`         | MCP 服务器（stdio 或 http）  | 提供外部工具             |

这三类只在 **Direct** 回合生效，且需在「设置 → 代理助手」里把插件绑定给对应代理。

## 第 1 步：目录结构

```
<plugin-id>/
├── plugin.json                  ← 必需，唯一入口
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
	"contributes": {
		"directInstructions": "instructions/direct.md",
		"skills": [
			{ "id": "guide", "path": "skills/guide" }
		]
	}
}
```

逐字段约束（安装时校验，违反即拒收）：

| 字段            | 约束                                                                 |
| --------------- | -------------------------------------------------------------------- |
| `schemaVersion` | 必须是 `1`                                                           |
| `id`            | 小写 ASCII 字母、数字、`-`，≤64，禁止大写                            |
| `name` / `version` | 非空；`name` 可以是中文                                           |
| `skills[].id`   | 同 `id` 字符规则，插件内唯一                                         |
| `skills[].path` | 包内相对目录，目录里必须有 `SKILL.md`                                |

## 第 3 步：其他贡献

### directInstructions

一个 markdown 文件，内容整体作为 `[plugin:<id>]` 分节注入 Direct 回合的系统指令。写行为规范。

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

## 第 4 步：打包

把插件目录压成 `.zip`，包内必须**恰好一个** `plugin.json`（它所在目录就是包根，带不带顶层文件夹都行）：

```powershell
Compress-Archive -Path <插件目录>/* -DestinationPath <插件id>.zip -Force
```

包限制：压缩包 100 MB / 解压后 300 MB / 5000 文件 / 单文件 50 MB / `plugin.json` 与 `SKILL.md` 各 256 KB。

安装路径：**设置 → 扩展 → 插件 → 导入插件**，选中 zip。导入后默认禁用，启用后把插件绑定到目标代理。

## 第 5 步：生成后自检清单

交付前逐条核对：

- [ ] `plugin.json` 是合法 JSON，`schemaVersion` 为 1
- [ ] `id` 与所有贡献 id 只含小写字母/数字/`-`
- [ ] 没有 `contributes.views`，没有面板或悬浮页
- [ ] 引用的每个文件（SKILL.md、instruction、MCP stdio 入口）真实存在于即将打包的目录里

## 常见报错对照

| 安装报错                                                      | 原因                           |
| ------------------------------------------------------------- | ------------------------------ |
| `A Plugin package must contain exactly one plugin.json file.` | 包里没有或有多个 `plugin.json` |
| `Plugin id must use lowercase ASCII letters, digits, and '-'.` | id 含大写或非法字符          |
| `Plugin Skill '...' must contain SKILL.md.`                   | skill 目录里没有 `SKILL.md`    |
| `Plugin MCP '...' transport must be stdio or http.`           | 传输类型不支持                 |

## 更新

改完重新打包、重新导入即可。已打开的 Direct 回合继续用导入前的那一版，下一回合才换成新包。
