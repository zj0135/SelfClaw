# csharp-diag — C# 调试、性能优化与测试质量插件

从 [dotnet/skills](https://github.com/dotnet/skills) 官方仓库精选，重组为 SelfClaw 插件格式。
配套代理：`csharp-diag`（诊断、性能优化、测试质量审计）。编码类技能见姊妹插件 `csharp-dev`。

按上下文成本最小化裁剪：SelfClaw 每轮只注入技能目录（每技能一行），全文仅在激活时注入；
技能越少，误激活 20KB 全文的概率越低。

## 技能清单（7）

| 技能 | 用途 |
|---|---|
| `binlog-failure-analysis` | 从 `.binlog` 定位构建失败根因 |
| `build-perf-diagnostics` | 构建性能 / 增量构建失效 / 耗时 Target 定位 |
| `analyzing-dotnet-performance` | ~50 种性能反模式扫描（async/内存/分配等） |
| `dump-collect` | 崩溃/挂起/内存 dump 收集与分析 |
| `microbenchmarking` | BenchmarkDotNet 基准（建/跑/解读） |
| `coverage-analysis` | 覆盖率采集与解读、找未测代码（含 PowerShell 脚本） |
| `test-anti-patterns` | 测试反模式识别 |

刻意未收录（与保留技能重叠或低频）：`incremental-build`（被 build-perf-diagnostics 覆盖）、
`dotnet-trace-collect`（不做 CPU trace）、`test-gap-analysis`（被 coverage-analysis 覆盖）、
`assertion-quality`（被 test-anti-patterns 覆盖）。

## 安装与使用

1. 打包：`Compress-Archive -Path plugins/csharp-diag/* -DestinationPath csharp-diag.zip -Force`
2. 设置 → 扩展 → 插件 → 导入插件，选中 zip，启用。
3. 设置 → 代理助手，把 `csharp-diag` 插件绑定给 `csharp-diag` 代理。
4. 显式激活示例：`[/csharp-diag/dump-collect]`。

注意：`coverage-analysis` 等技能带 `.ps1` 脚本，经 `run_skill_script` 执行需桌面审批，属预期行为。

技能来源插件：`dotnet-test` v1.0.1、`dotnet-msbuild`、`dotnet-diag`。
上游更新后重新复制技能目录、升版本号重打包即可。
