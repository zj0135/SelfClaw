# hook-all-events — Direct hooks 全事件示例插件

覆盖 Direct 链路全部 **6 个 hook 事件**（回合 / 工具 / HTTP）的可运行示例。与最小示例
[`hook-examples`](../hook-examples) 互补：那个演示单一危险命令拦截，这个用于观察完整生命周期。

所有脚本把结构化记录追加到 `%LOCALAPPDATA%\SelfClaw\hook-all-events\`：

| 文件 | 写入者 | 内容 |
|---|---|---|
| `runs.jsonl` | `run-starting`、`run-completed` | 回合开始与结束的状态、origin、inherited、用量、耗时 |
| `tools.jsonl` | `tool-executing`、`tool-executed-audit` | 每次工具调用前后：参数、状态、deniedBy、耗时 |
| `http.jsonl` | `http-request-sending`、`http-response-received` | 每个真实 HTTP 请求/响应：方法、URL（不含 query 值）、头、序号、耗时、错误 |

## 六个事件

| hook id | 事件 | 执行 | 决策 / 作用 |
|---|---|---|---|
| `run-starting` | `runStarting` | 同步 | 记录回合开始；含哨兵时阻止回合或追加上下文 |
| `run-completed` | `runCompleted` | 异步 | 记录终止状态、用量、工具数与耗时 |
| `tool-executing` | `toolExecuting` | 同步（`onFailure: block`） | 记录调用；含哨兵时 `deny` / `ask` / 改写参数 |
| `tool-executed-feedback` | `toolExecuted` | 同步 | 工具非 `completed` 时向模型追加一条反馈 |
| `tool-executed-audit` | `toolExecuted` | 异步（`async: true`） | 记录每次工具结果 |
| `read-file-feedback` | `toolExecuted` | 同步 | `read_file` 结果被截断或像含密钥时，向模型追加修正性 feedback |
| `http-request-sending` | `httpRequestSending` | 同步 | 记录请求并追加 `x-hook-all-events` / `x-hook-body-bytes` 请求头 |
| `http-response-received` | `httpResponseReceived` | 异步 | 记录响应状态与耗时 |

> `runCompleted`、`httpResponseReceived` 本身永远是异步；`async: true` 只能声明在 `toolExecuted` 上。

## 触发哨兵（在对话或命令里写这些字符串）

| 哨兵 | 效果 |
|---|---|
| `HOOK-BLOCK-RUN` | `runStarting` 阻止整个回合（不发生模型调用，消息显示 blocked） |
| `HOOK-ADD-CONTEXT` | `runStarting` 注入一段回合上下文（助手消息开头出现 `Notice` 段） |
| `HOOK-DENY` | `toolExecuting` 拒绝该 shell 命令（卡片显示“已拦截”） |
| `HOOK-ASK` | `toolExecuting` 强制人工审批，即使 `FullAccess` |
| `HOOK-REWRITE` | `toolExecuting` 把命令改写为无害的 `echo`（卡片显示原始/实际参数两栏） |

例：`run_shell_command` 执行 `echo HOOK-DENY` 会被拦截；执行 `echo HOOK-REWRITE` 会实际运行改写后的命令。
其它命令一律放行，只写日志。

## 现实用法：用 feedback 补充/修正 `read_file` 结果

`toolExecuted` **不能替换**工具原始结果（设计上明确不做），但可以把一条宿主持久化、可回放的说明附加到结果后面。
`read-file-feedback.ps1` 就是一个真实场景：

- 结果被工具截断（`content.truncated`）时，告诉模型“文件在第 N 行还有后续”，并给出 `startLine` 分页建议，
  避免模型把“读到的片段”当成“文件全部”。
- 结果被宿主在 64 KiB 处截断（`contentTruncated`）时，提示模型把切口之后视为缺失。
- 文件内容像私钥/凭证（`-----BEGIN … PRIVATE KEY-----`、`AKIA…`、`ghp_…`、`sk-…`）时，
  要求模型不要原样引用、不要外发。

反馈在实时回合里作为工具结果 JSON 的 `hookFeedback` 数组给模型，下回合回放时变成
`ResultContent` 末尾的 `<selfclaw-hook-feedback>` 块（写入 `tool_runs.hook_feedback_json`，重启后仍在）。
看工具卡片即可看到来源与文本；想彻底替换结果内容需要自己实现工具/MCP，而不是 hook。

## 从文件夹安装

1. 设置 → 扩展 → 插件 → **从文件夹安装**，选择本目录。
2. 确认 `hooks.run` / `hooks.tool` / `hooks.http` / `hooks.http.body` 四项权限并启用。
   本示例声明了 `hooks.http.body`，意味着 `http-request-sending` 会收到发往模型提供商的**请求体**
   （含对话内容，最多 1 MiB）——仅用于演示，不需要时删掉该权限与 `includeRequestBody`。
3. 在「设置 → 代理助手」里把 `hook-all-events` 绑定给目标代理。
4. 发一轮对话即可在 `%LOCALAPPDATA%\SelfClaw\hook-all-events\` 看到三个 jsonl 文件。
5. 修改脚本后，详情抽屉点 **重新加载**；进行中的回合继续使用旧版本。

插件绑定后，该代理发起的**子代理与续跑回合也会继承这些 hook**（`inherited: true`，可在 jsonl 中看到）。
继承插件若被禁用/删除/更新，待执行的子任务与续跑会被阻止（fail-closed）。

## 顺序与 matcher

- 顺序：插件 id（Ordinal 升序）→ 同一插件内 manifest 声明顺序；同一事件的同步 hook 串行执行。
- matcher 字段（字段之间“与”，字段内多值“或”）：
  - 所有事件：`origins` = `interactive` / `subagent` / `continuation`
  - 工具事件：`tools`（模型可见工具名，`*` 通配）、`sourceIds`（MCP 服务器 id）、
    `kinds` = `other/read/edit/run/search/list`、`sources` = `builtIn/mcp/skill/plugin`
  - HTTP 事件：`hosts`（如 `api.openai.com`、`*.openai.azure.com`、`127.0.0.1`、`::1`）
- 本示例为了让 hook 一定触发，只在 `tool-executing` 收窄到 `run_shell_command`；要按 provider 收窄 HTTP，
  给 `http-request-sending` / `http-response-received` 加 `hosts` matcher（例如
  `"matcher": { "hosts": ["api.openai.com", "*.openai.azure.com"] }`）。

## 脚本结构

```
hook-all-events/
├── plugin.json
├── README.md
├── lib/
│   └── common.ps1                     ← UTF-8、stdin 读取、JSON 决策输出、带重试追加日志
└── hooks/
    ├── run-starting.ps1
    ├── run-completed.ps1
    ├── tool-executing.ps1
    ├── tool-executed-feedback.ps1
    ├── tool-executed-audit.ps1
    ├── read-file-feedback.ps1
    ├── http-request-sending.ps1
    └── http-response-received.ps1
```

每个脚本通过 `$env:SELFCLAW_PLUGIN_ROOT`（宿主注入的版本目录）dot-source `lib/common.ps1`。
`HookAllEventsPluginTests` 用真实 `PluginManifestReader` 校验本包清单，防止示例与规则漂移。
