# hook-examples — Direct hooks 示例插件

一个文件夹形式的示例 SelfClaw 插件，演示 Direct 链路的两个 hook 事件：

- `guard-shell`（`toolExecuting`，同步，`onFailure: block`）：在 `run_shell_command` 执行前扫描命令，
  命中破坏性模式时返回 `deny`，工具卡片显示“已拦截”与原因。
- `audit-run`（`runCompleted`，异步）：把每个回合的 `turnId`、`conversationId`、`origin`、`inherited`、
  `agentId`、`status`、`toolCallCount`、`durationMs`、`timestampUtc` 追加为一行 JSON 到
  `%LOCALAPPDATA%\SelfClaw\hook-examples\audit.jsonl`。

它同时充当 manifest 校验的回归用例：`HookExamplesPluginTests` 用真实 `PluginManifestReader` 读取本目录，
确保示例不会随规则演进而失效。

## 结构

```
hook-examples/
├── plugin.json              ← 声明 hooks.run / hooks.tool 权限与两个 hook
├── README.md
└── hooks/
    ├── guard-shell.ps1      ← toolExecuting：拒绝危险 shell 命令
    └── audit-run.ps1        ← runCompleted：追加审计日志
```

## 从文件夹安装

1. 设置 → 扩展 → 插件 → **从文件夹安装**，选择本目录（不要选整个仓库，仓库根目录没有 `plugin.json`）。
2. 安装后确认 `hooks.run` / `hooks.tool` 权限并启用。
3. 在「设置 → 代理助手」里把 `hook-examples` 绑定给目标代理。

文件夹安装会快照复制内容并记住源路径；修改脚本后回到详情抽屉点 **重新加载**，下一回合即生效，
进行中的回合继续使用旧版本。

## 绑定与生效范围

hook 只在 Direct 回合生效。绑定该插件的代理发起的**子代理与续跑回合也会继承这两个 hook**
（策略继承，不贡献指令/Skill/MCP）；继承时插件若被禁用、删除或更新，待执行的子任务与续跑会被阻止。

## 查看结果

- 危险的 `run_shell_command` 会被拦截：工具卡片状态为“已拦截”，展开可见拦截者 `hook-examples/guard-shell`
  与原因。
- 设置 → 扩展 → 插件 → 选中 `hook-examples`：详情里有 hooks 列表、源路径与执行日志（手动刷新）；
  日志显示每次执行的决策、耗时、退出码与 stderr 摘要。
- 审计日志：`%LOCALAPPDATA%\SelfClaw\hook-examples\audit.jsonl`，每行一个 JSON 对象。

## 权限说明

| 权限 | 用途 |
|---|---|
| `hooks.run` | 运行 `audit-run`，读取回合状态并写审计日志 |
| `hooks.tool` | 运行 `guard-shell`，读取工具参数并拒绝调用 |

hook 命令以当前用户身份运行并继承环境变量，不能绕过审批。`guard-shell` 只读取 `arguments.command`，
不写任何文件；`audit-run` 只写自己的 `%LOCALAPPDATA%` 目录。
