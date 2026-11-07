# csharp-dev — C# 开发代理插件（编码 + 测试编写）

从 [dotnet/skills](https://github.com/dotnet/skills) 官方仓库精选，重组为 SelfClaw 插件格式。
配套代理：`csharp-dev`（编码与测试编写，xUnit 生态）。诊断/性能类技能见姊妹插件 `csharp-diag`。

按上下文成本最小化裁剪：SelfClaw 每轮只注入技能目录（每技能一行），全文仅在激活时注入；
技能越少，误激活 20KB 全文的概率越低。

## 技能清单（7）

| 技能 | 用途 |
|---|---|
| `csharp-expert` | C# 语义专家 + 专家路由（不确定用哪个技能时先激活它） |
| `csharp-refactoring` | 重构手法 |
| `dotnet-webapi` | Web API 端点、OpenAPI、错误处理中间件 |
| `optimizing-ef-core-queries` | EF Core 查询编写与优化 |
| `run-tests` | dotnet test 命令 / 过滤器 / 诊断（含 filter 语法） |
| `scaffold-dotnet-test-project` | 搭测试项目 |
| `analyzing-dotnet-performance` | 写码时规避 ~50 种性能反模式 |

刻意未收录（低频或冗余，需要时再单独导入）：`msbuild`、`setup-local-sdk`、`filter-syntax`
（被 run-tests 覆盖）、`writing-mstest-tests`（用 xUnit）、`dotnet-breaking-changes`（升级时再装）。

## 安装与使用

1. 打包：`Compress-Archive -Path plugins/csharp-dev/* -DestinationPath csharp-dev.zip -Force`
2. 设置 → 扩展 → 插件 → 导入插件，选中 zip，启用。
3. 设置 → 代理助手，把 `csharp-dev` 插件绑定给 `csharp-dev` 代理。
4. 显式激活示例：`[/csharp-dev/dotnet-webapi]`。

技能来源插件：`dotnet` v0.2.6、`dotnet-aspnetcore` v0.1.2、`dotnet-data`、`dotnet-test` v1.0.1、`dotnet-diag`。
上游更新后重新复制技能目录、升版本号重打包即可。
