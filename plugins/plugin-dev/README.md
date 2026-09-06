# plugin-dev — 插件开发工具包

一个 Skill 型 SelfClaw 插件：贡献 `create-plugin` Skill，教代理按当前插件系统的校验规则生成、
自检并打包新插件。适合装进日常使用的 SelfClaw，之后对代理说「帮我做一个 XXX 插件」或在输入框
输入 `[/plugin-dev/create-plugin]` 即可。

## 结构

```
plugin-dev/
├── plugin.json                     ← 只有 skills 贡献，permissions 为空
└── skills/
    └── create-plugin/
        └── SKILL.md                ← Skill 正文：插件规范 + 模板 + 自检清单
```

## 安装与使用

1. 打包：

   ```powershell
   Compress-Archive -Path plugins/plugin-dev/* -DestinationPath plugin-dev.zip -Force
   ```

2. 设置 → 扩展 → 插件 → 导入插件，选中 `plugin-dev.zip`，然后启用。
3. 在「设置 → 代理助手」里把 `plugin-dev` 绑定给目标代理（插件贡献的能力只在 Direct 回合生效）。
4. 对代理说「帮我做一个 XX 插件」，或显式输入 `[/plugin-dev/create-plugin]`。

## 维护

插件系统的校验规则或面板 SDK 变化时，同步更新
`skills/create-plugin/SKILL.md`（规范、模板与 SDK 速查都在正文里），重新打包导入即可。
`PluginDevFixtureTests` 会用真实安装器验证本包始终可安装、Skill 可解析。
