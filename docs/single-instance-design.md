# 主进程单例设计

关系：桌面外壳的启动顺序见 `docs/network-proxy-design.md` §4（`App.StartAsync` 的等待点）与 `docs/pet-system-design.md`（托盘 / 宠物窗口）。本文只管「一个用户会话里只允许一个 SelfClaw 主进程」这一件事。

## 1. 需求

用户把窗口收进托盘（前端 `minimize-to-tray` → `MainWindow.Hide()`）后，再从桌面图标双击启动。以前这会拉起第二个完整进程：两套 TickerQ 调度同时跑自动化、两个 SQLite 写者对同一个库、两份托盘图标、两个宠物窗口，还可能出现「关掉的那个窗口其实不是我在用的那个」。

期望行为：已经在跑 → 把**原来那个**窗口叫到前面（隐藏 / 最小化 / 被压住都要复原），新进程立刻退出；没有在跑 → 正常启动。

## 2. 契约

| 内核对象 | 名字 | 语义 |
|---|---|---|
| 命名 mutex | `Local\SelfClaw.Desktop` | 主进程持有到 `OnExit`，是整个单例的唯一真值 |
| 命名事件（auto-reset） | `Local\SelfClaw.Desktop.Activate` | 后启动的进程 `Set()` 一次，主进程的监听线程接住 |

`Local\` 前缀是刻意的：作用域是当前登录会话，所以快速用户切换下每个用户各自一个 SelfClaw（互不打扰），而同一个用户在同一会话里仍然只有一个。没有用 `Global\`，也没有加用户名后缀。

实现：`SelfClaw.Desktop/Services/SingleInstance/DesktopSingleInstanceGuard.cs`（不注册进 DI，`App.OnStartup` 在建宿主之前直接 new）。

## 3. 两条路径

主进程（`App.OnStartup`）：

1. `TryAcquire(TimeSpan.Zero)` 拿到锁 → `StartListening()` 起一个后台线程等命名事件。
2. `RegisterSingleInstanceActivation()` 在 `mainWindow.Show()` 之后才挂上处理函数（`DesktopConversationActivationService.ActivateAsync()`，即托盘双击走的那条路径）。窗口建好之前到的信号记在 `_pendingActivation` 里，挂处理函数时补放一次——事件是自动复位的，丢掉那次信号用户看到的就是「双击没反应」，而且再点也不会自愈。

后启动的进程：

1. `TryAcquire` 失败 → `SignalPrimaryInstance()`（`EventWaitHandle.OpenExisting` 有 300 ms 容错窗口：主进程刚拿到锁、事件还没建好的那几毫秒里打开会抛 `WaitHandleCannotBeOpenedException`）。
2. 不建宿主、不碰数据库，`Shutdown()` 立刻退出。`Shutdown()` 在 `OnStartup` 里调用是安全的：WPF 把关闭回调投给调度器，`OnStartup` 返回后消息循环第一次转起来就退出（已用一次性 WPF 验证）。

## 4. 关闭竞态：2 秒宽限

`OnMainWindowClosing` 会先 `Hide()` 再走 `ShutdownCoreAsync`，整套拆卸最长 20 秒；这段时间锁还在退出的那个进程手里。此时双击图标，信号会打到一个正在拆的进程上（`ActivateFromSecondInstanceAsync` 见 `_shutdownTask` 非空就直接返回，绝不把正在拆的窗口又 `Show` 回来），用户就会看到「双击毫无反应」。

所以后启动的进程在发完信号后还会 `TryAcquire(2s)`：

- 主进程是活的（收在托盘）→ 那次 `TryAcquire` 必然失败，2 秒后自己退出，只是多一个不可见的进程短暂存在。
- 主进程正在退出 → 锁很快放开，这次启动直接接管，等价于正常启动。

代价是「已有实例」那条路径最多 2 秒才退出；收获是关闭瞬间的双击不会白点。

## 5. 前台激活

`DesktopConversationActivationService.RestoreWindow()` 除了 `Show()` / 复原 `WindowState` / `Activate()` / `Focus()`，还会调 `WindowActivation.BringToFront()`。原因：被桌面图标那次点击授予前台权限的是**新**进程，不是这个老进程，所以 `SetForegroundWindow` 会被前台锁静默拒绝，窗口只是任务栏闪一下。`WindowActivation` 临时 `AttachThreadInput` 到当前前台线程再 `SetForegroundWindow`，用完立刻解挂。

托盘双击、宠物气泡打开会话、toast 深链都复用这条路径，因此三者的行为保持一致。

## 6. 刻意不做

- **不转发命令行的深链参数**。假想的场景是「toast 点击 » 系统拉起新进程 » 带上会话 id」，但 `ToastNotificationManagerCompat` 在应用已运行时把激活投回同一个进程（COM 服务器在进程内注册），只有应用没运行时才由系统拉起进程——那一次新进程本身就是主实例，参数不会经过单例通道。真要支持「`selfclaw.exe --open <id>`」再引入带载荷的 IPC（命名管道），现在不预先造。
- **不做跨完整性级别**。管理员身份启动的那份用不同的 mutex 命名空间，`OpenExisting` 会抛 `UnauthorizedAccessException`，此时安静退出而不是强行拉起第二个实例。
- **不做跨机器 / 跨会话**（`Local\` 的语义就是不要这个）。
- **不按可执行文件路径区分实例**。名字里没有路径或版本，`dotnet run` 出来的调试版与已安装版共用同一把锁：已安装版在跑时 `dotnet run` 只会把那个窗口叫出来（这正是单例该有的语义），要并行调试先退出已安装版。反过来，如果把路径塞进名字，用户挪一次目录就会出现两个进程对着同一个库。
- **不做单实例内的多窗口**：`ShutdownMode` 仍是 `OnExplicitShutdown`，主窗口的关闭语义（隐藏 → 拆卸 → 退出）不变。

## 7. 验证

- 单元测试：`SelfClaw.Tests/Desktop/Services/SingleInstance/DesktopSingleInstanceGuardTests.cs`（拿不到锁、信号经调度器到达、早到信号被补放、无人监听时信号失败、释放后可被下一个进程接管）。mutex 按线程递归，所以「第二个进程」那一侧必须换线程去试。
- 手工：启动一次 → 托盘最小化 → 再次双击图标；只应看到一个 `SelfClaw.exe`，且窗口回到前面。日志里那次启动应当没有任何 `SelfClaw starting` 记录。
