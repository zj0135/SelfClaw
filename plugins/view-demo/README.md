# view-demo — 插件视图示例

一个文件夹形式的示例 SelfClaw 插件，演示两类插件视图（`contributes.views`）：

- `inspector`（`slot: "right"`）：右侧停靠面板。展示 `viewKey` / `slot`、上下文、转录订阅、工作区
  写入入口（`insertPrompt`）与 `selfclaw.close()`。
- `hud`（`slot: "floating"`）：覆盖主对话列的悬浮 HUD。展示插件自己的视口坐标、用 `layout.anchors` 贴住输入区、
  `data-selfclaw-interactive` 声明的交互矩形、以及 `hit-released` 对 hover 态的复位。

它同时充当 manifest 校验的回归用例：`ViewDemoPluginTests` 用真实 `PluginManifestReader` 读取本目录，
确保示例不会随规则演进而失效。

## 结构

```
view-demo/
├── plugin.json      ← 声明 ui.panel / ui.floating 权限与两个视图
├── README.md
└── ui/
    ├── dock.html    ← slot: right
    └── hud.html     ← slot: floating
```

## 从文件夹安装

1. 设置 → 扩展 → 插件 → **从文件夹安装**，选择本目录（不要选整个仓库，仓库根目录没有 `plugin.json`）。
2. 安装后确认 `ui.panel` / `ui.floating` / `host.*` 权限并启用。
3. 左侧导航 → **插件** → 打开「视图演示」或「悬浮 HUD」。

## 悬浮视图能看到什么

| 现象 | 说明 |
|---|---|
| HUD 自动贴在输入区上方，右栏开合或侧栏折叠时会跟着动 | `layout.anchors.composer` / `dock` 在布局变化后重新推送；`dock` 在面板未打开时为 `null` |
| 卡片上的按钮能点，卡片以外的地方照常操作应用 | 只有 `data-selfclaw-interactive` 的矩形参与命中测试，其余像素对宿主完全透明 |
| 鼠标移出卡片后「指针」一行会回到「在卡片外（宿主已收回指针）」 | 外壳解除武装时发的 `hit-released`；JS 维护的 hover 态不会因浏览器不补 `pointerout` 而卡住 |
| 标题栏「隐藏悬浮视图」按钮点亮时可整体收起它 | 悬浮层显隐是外壳的视图状态，帧与租约保留；插件盖住界面时这颗按钮是退路 |
| 什么都没打开时，标题栏的两颗插件按钮是灰的 | 标题栏只管显隐；打开视图只有左侧「插件」一个入口（不再弹启动器），按钮也不假扮成另一个入口 |
| HUD 不会盖住侧栏与右栏，右栏开合时它跟着变宽变窄 | 悬浮层只覆盖主对话列（`.main-content`）。不只是审美：实测指针在子 iframe 上时父文档收不到 `pointermove`，悬浮层与面板重叠就会点不动 |

## 权限说明

| 权限 | 用途 |
|---|---|
| `ui.panel` | 贡献右侧停靠面板 |
| `ui.floating` | 贡献悬浮视图（在主对话列内可截获点击，因此与面板分开披露） |
| `host.context.read` | `getContext()` / `context-changed` |
| `host.transcript.read` | `transcript` 事件（停靠面板里显示回合状态） |
| `host.composer.write` | `insertPrompt(text)`，只插入不发送 |

两个页面都自带样式、不引用任何外部资源；悬浮 HUD 不声明 `network.fetch:`，因此 `connect-src 'self'`，
完全离线。写自己的插件时把这两个文件当骨架抄，规则见
`plugins/plugin-dev/skills/create-plugin/SKILL.md` 第 4、5 步。
