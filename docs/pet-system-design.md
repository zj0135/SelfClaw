# SelfClaw 桌宠系统设计文档

> 状态:浮窗、宠物包选择、交互/工作动画、Agent 节点气泡与 Direct 审批已实现
> 目标平台:WPF (.NET 10, `net10.0-windows10.0.19041.0`) + 独立桌面浮动窗口
> 参考来源:`open-design/apps/web/src/components/pet`(React/Web 实现,思路参考,代码不复用)

---

## 1. 目标与范围

给 SelfClaw 增加一个"桌面宠物":一个漂浮在桌面上的小精灵,能被拖拽、能对鼠标 hover 做出反应、在无人打扰时自己"活动"(环境动画),并可挂接应用状态(例如 Agent 正在运行 / 有任务完成)显示气泡。

### 1.1 第一版目标(MVP)

| 能力 | 说明 |
|------|------|
| 浮动窗口 | 无边框、透明背景、置顶、不占任务栏的独立 `Window`,浮在所有窗口之上 |
| 拖拽 | 按住宠物可拖动窗口到桌面任意位置,带抖动过滤 |
| Hover | 鼠标移入切换到"打招呼/挥手"动画 |
| Idle 行为 | 静止 → 长时间无操作转"等待"状态;交互随时打断 |
| 环境动画 | idle 时随机穿插挥手/跳/看四周等动作,节奏带随机方差 |
| 精灵图渲染 | 从 `.webp` spritesheet 按"行=状态、列=帧"逐帧播放 |
| 位置持久化 | 记住上次摆放位置,重启后回到原处 |
| 开关 | 托盘菜单/设置里能开关宠物 |

### 1.2 非目标(后续再议)

- 宠物商店、在线下载、用户自定义上传(open-design 有,SelfClaw 当前只提供内置宠物切换)
- 语音、TTS、复杂对话
- 拖到屏幕边缘吸附、多显示器智能定位(第一版做基础 clamp 即可)

---

## 2. 技术栈决策与关键约束

### 2.1 决策记录

| 决策点 | 选择 | 理由 |
|--------|------|------|
| UI 技术 | **纯 WPF 原生窗口** | 与 `open-design` 的 React/WebView 路线不同。桌宠需要真正脱离主窗口、浮在桌面上,WPF 原生 `Window` 是最直接的方案,避免 WebView2 承载浮层的额外开销与透明穿透难题 |
| 承载方式 | **独立桌面浮动窗** | 不嵌在主窗口内,宠物是独立的 `PetWindow`,主窗口关闭/最小化时它可独立存在(受开关控制) |
| 视觉素材 | **`.webp` spritesheet** | 用户已有素材 |

### 2.2 ⚠️ 关键约束:WPF 不能原生解码 WebP

这是整个渲染层设计的决定性约束,必须先说清楚:

- WPF 的 `BitmapImage` / `BitmapDecoder` 依赖 Windows 的 **WIC(Windows Imaging Component)** 编解码器。
- **WIC 默认不包含 WebP 解码器**。WebP 支持来自微软商店的 "WebP Image Extensions" 扩展包,**不能假设目标机器上一定安装**。
- 因此:**不能直接把 `.webp` 喂给 `BitmapImage`**,否则在没装扩展的机器上会解码失败 / 抛异常。

**已定方案:引入应用内自带的 WebP 解码库,运行期把 `.webp` 解到内存像素,再交给 WPF 显示。** 不走"构建期转 PNG",因为后续要支持**用户自定义宠物**(用户运行时丢 `.webp` 进来),必须具备运行期解码能力——一次性解决,避免将来返工。

解码库选型见 §5.3,核心是在应用内自带解码能力,完全不依赖系统 WIC 的 WebP 支持。

---

## 3. 从 open-design 借鉴的设计(思路移植,非代码)

open-design 的宠物系统在**行为设计**上已经比较成熟,以下几点直接影响我们的 WPF 设计:

### 3.1 表驱动的状态 → 动画行映射
参考 `pets.ts` 的 `INTERACTION_ROW_ID`。它把交互状态收敛成一张声明式表,每个状态映射到 spritesheet 的一行。我们在 WPF 里原样照搬为 `enum` + 字典。

```
idle          → 行: idle
hover         → 行: waving
drag-right    → 行: running-right
drag-left     → 行: running-left
drag-up       → 行: jumping
drag-down     → 行: waving
waiting       → 行: waiting
```

### 3.2 环境动画(ambient)与交互动画分离
参考 `PetOverlay.tsx` 的 ambient 调度器。关键设计:
- **只有 `idle` 时**才随机穿插 ambient 动作(挥手/跳/看四周)。
- 任何用户手势(hover/drag)**立即打断** ambient,交互动画优先。
- play 窗口和 rest 窗口**都带随机方差**,避免节奏机械化。
- rest 窗口刻意设长,让宠物读起来"平静"而非"多动"。

open-design 的时间参数(可作为我们的默认值起点):

| 参数 | 值 | 含义 |
|------|-----|------|
| `WAITING_AFTER_MS` | 45000 | 无操作 45s 后转 waiting |
| `AMBIENT_PLAY_MIN_MS` | 1400 | 单次 ambient 动作最短播放 |
| `AMBIENT_PLAY_VARIANCE_MS` | 900 | ambient 播放时长随机方差 |
| `AMBIENT_REST_MIN_MS` | 9000 | 两次 ambient 之间最短休息 |
| `AMBIENT_REST_VARIANCE_MS` | 9000 | 休息时长随机方差 |
| `AMBIENT_INITIAL_DELAY_MIN_MS` | 4000 | 首次 ambient 前的初始延迟 |

### 3.3 拖拽手势的工程细节
参考 `PetOverlay.tsx` 的 pointer 处理:
- **抖动过滤**:移动 < 4px 不算拖动(区分点击 vs 拖动)。
- **方向判定要求单轴主导**:`DRAG_AXIS_BIAS = 1.18`,即一个轴要明显大于另一个才判为该方向,避免对角线拖动时 running-left/right 来回跳。
- **方向存在字段里而非状态里**:避免每次 pointermove 都触发重渲染(WPF 里对应"只在方向真正变化时才切动画行")。
- **拖拽最小位移阈值**:`DRAG_GESTURE_MIN_PX = 14`,清除抖动地板后才提交方向动画。

### 3.4 位置持久化用"距边距"而非绝对坐标
open-design 存的是 `{ right, bottom }`(距屏幕右/下边距),这样窗口/屏幕 resize 后仍贴角。

**WPF 差异**:我们是独立窗口(不是 DOM overlay),更自然的做法是存**归一化坐标**或**距最近工作区边角的偏移**,以适配多显示器与分辨率变化(见 §6.3)。

### 3.5 点击 vs 拖拽的区分
参考 `onPointerUp`:如果按下到抬起没有超过位移阈值(`moved === false`),视为"点击",触发气泡开关;否则视为拖拽结束,回落到 idle/hover。

---

## 4. 架构总览

### 4.1 模块划分

```
SelfClaw.Desktop/Pet/
├─ Behavior/                             交互、工作状态、waiting、ambient
│  ├─ PetBehavior.cs / PetLayout.cs
│  └─ Models/                            行为事件、结果与 timer 命令
├─ Catalog/                              宠物包与 spritesheet catalog
│  ├─ PetPackageCatalog.cs
│  ├─ Abstractions/                      解码 seam
│  ├─ Adapters/                          libwebp adapter
│  └─ Models/                            manifest、grid 与 package DTO
├─ Hosting/                              生命周期、命令与持久化
│  ├─ PetHost.cs
│  ├─ Abstractions/                      Window 与 settings seam
│  ├─ Adapters/                          WPF 与 desktop-settings adapter
│  └─ Models/                            host command/state/settings DTO
├─ Presentation/                         Agent activity → 气泡生命周期
│  ├─ PetActivityPresenter.cs
│  ├─ Abstractions/ / Adapters/          presentation scheduler seam
│  └─ Models/                            气泡 view state
├─ Rendering/                            SpriteSheet、Animator 与帧模型
├─ ViewModels/                           WPF timer/绑定 adapter
└─ Views/                                PetWindow XAML 与 pointer 事件
```

### 4.2 分层职责

- **`PetHost`**:唯一外部 host module。interface 只有 `InitializeAsync`、`GetStateAsync`、`ExecuteAsync(PetHostCommand)`,统一生命周期、命令串行化、宠物选择和持久化。
- **`WpfPetWindowAdapter`**:内部 adapter,隐藏 `Application.Current`、Dispatcher、`Screen.AllScreens`、窗口创建/关闭、多屏 DPI 定位与拖拽位置防抖。
- **`DesktopPetSettingsRepository`**:内部 adapter,只负责 `PetSettings` 的 JSON 读写;`PetHost` 持有缓存和写入顺序规则。
- **`PetPackageCatalog`**:唯一解释 `assets/pets`、`pet.json`、旧 id、路径安全、grid 优先级和默认包回退的模块。设置页只消费它返回的 catalog。
- **`PetBehavior`**(纯逻辑,无 WPF 依赖):统一交互、waiting、ambient、Agent 工作状态优先级与动画行 fallback,通过行为事件输入和 timer 命令输出测试完整序列。
- **`SpriteSheet` + `SpriteAnimator`**:知道网格布局(列数、行数、每行帧数、fps),按当前行 + 帧号裁出要显示的那一格。
- **`WebpSpriteLoader`**:`IPetSpriteDecoder` 的生产 adapter,一次性把 `.webp` 解码成 `BitmapSource`(BGRA),后续所有裁切都从内存位图做。
- **`PetWindow`**:承载视觉 + 转发 `MouseDown/Move/Up/Enter/Leave` 到 ViewModel;负责窗口拖动。
- **`PetActivityPresenter`**:管理气泡 auto-hide、审批 pin、terminal 动画清理、当前审批动作和会话激活;调度由 `IPetPresentationScheduler` adapter 提供。
- **`PetViewModel`**:只把 `PetBehavior`、`PetActivityPresenter` 与 WPF timer/绑定接起来,不拥有行为或展示生命周期规则。

### 4.3 接入现有架构的方式

复用你们已有的基础设施,不引入新范式:

| 现有设施 | 复用方式 |
|----------|----------|
| `Host.CreateApplicationBuilder` + DI 单例 (`App.xaml.cs`) | 注册 `PetHost`、两个内部 adapter、catalog、presenter;不直接注册 `PetWindow` |
| `DesktopSettingsJsonStore` (按 node 名读写 JSON) | `DesktopPetSettingsRepository` 在 `"pet"` node 存 `PetSettings` |
| `SystemTrayService` 右键菜单 (`SystemTrayService.cs:33`) | 加一个可勾选的 "Show Pet" `ToolStripMenuItem` |
| `ShutdownMode.OnMainWindowClose` (`App.xaml.cs:38`) | PetWindow 不能算作 MainWindow;确保它的存在不阻止/不触发应用退出(见 §7.2) |
| WPF 与测试运行环境 | `IPetWindowAdapter` 分别由 `WpfPetWindowAdapter` 与测试 fake 实现,host 测试不启动真实 Window |

---

## 5. 渲染层详细设计

### 5.1 Spritesheet 契约

沿用 open-design/Codex hatch-pet 的网格约定(见 `codexAtlas.ts`),便于直接用现成素材:

- **网格**:8 列 × 9 行,每格 192 × 208 px(整图 1536 × 1872)。
- **行 = 动画状态**,列 = 该状态的帧序列(未用满的列要求透明,播放时按 `frames` 截断)。
- **每行的帧数与 fps** 各不相同,由行定义表描述。

行定义表(默认值,来自 `CODEX_ATLAS_ROWS_DEF`):

| 行 index | id | frames | fps |
|----------|-----|--------|-----|
| 0 | idle | 6 | 6 |
| 1 | running-right | 8 | 8 |
| 2 | running-left | 8 | 8 |
| 3 | waving | 4 | 6 |
| 4 | jumping | 5 | 7 |
| 5 | failed | 8 | 7 |
| 6 | waiting | 6 | 6 |
| 7 | running | 6 | 8 |
| 8 | review | 6 | 6 |

> 注:如果用户的 `.webp` 素材网格不同,`SpriteSheet` 应支持配置化的 cols/rows/cellSize/rowsDef,而非硬编码 8×9。

**布局来源优先级(为自定义宠物铺路)**:

```
pet.json 的可选 grid 字段  >  PetSettings.Grid 覆盖  >  内置 Codex 8×9 默认
```

- 每个宠物包(`assets/pets/<id>/`)的 `pet.json` 可选携带 `grid` 字段显式声明自己的布局;没有则回退到 Codex 8×9 默认。
- 这样既兼容现有走 Codex 约定的素材(`pet.json` 无 `grid` 即可),又给将来用户导入**非 8×9** 的自定义 `.webp` 留了显式声明布局的口子,避免光靠约定导致切帧错位。
- `pet.json` 的 `grid` 结构与 §8.1 `GridConfig` 一致:`cols` / `rows` / `cellWidth` / `cellHeight` / `rowsDef`(行定义数组,每项含 id/frames/fps)。
- `PetPackageCatalog` 是 Desktop 与 Vue 设置页共同依赖的唯一解释器:它返回排序后的 catalog、选中 id 和安全的预览相对路径。Vue 不再用 `import.meta.glob` 重新推导包结构。
- manifest 中的 `spritesheetPath` 必须留在包目录内;绝对路径和 `..` 逃逸会被拒绝。选中包解码失败时统一回退到默认包并返回 warning。

**已核对素材**:内置 `assets/pets/yorha-sit-2b/spritesheet.webp` 为 **VP8L 无损 WebP、1536 × 1872**,即标准 Codex 8×9(192 × 208 格),`pet.json` 无需 `grid` 字段,直接走默认。

### 5.2 帧切片数学

一次性把整张图解码为一个 `BitmapSource`(§5.3),然后每帧用 `CroppedBitmap` 取一格:

```
cellW = imageW / cols          // 192
cellH = imageH / rows          // 208
sourceRect(frame, rowIndex) = ( frame*cellW, rowIndex*cellH, cellW, cellH )
```

- 用 `CroppedBitmap(source, Int32Rect)` 得到当前格,绑定到 `Image.Source`。
- `Image` 上设 `RenderOptions.BitmapScalingMode`:像素风素材用 `NearestNeighbor`,插画风用 `HighQuality`(设为可配置)。
- `CroppedBitmap` 很轻(共享底层像素),逐帧新建可接受;若想更省,可预切所有格并缓存 `CroppedBitmap[rowIndex][frame]`。

### 5.3 WebP 解码方案(关键)

因为 WPF 不能可靠原生解码 WebP(见 §2.2),需要应用内自带解码库。**已定:运行期解码**,以支持将来用户导入自定义 `.webp` 宠物。候选:

| 方案 | 说明 | 取舍 |
|------|------|------|
| **libwebp P/Invoke** | 官方 Google libwebp,自带 `libwebp.dll`,`P/Invoke` 调 `WebPDecodeBGRA`/`WebPGetInfo` | ✅ **推荐**:BSD 许可无授权风险、体积小、解码是它的本职、纯 native 无托管依赖冲突 |
| **SixLabors.ImageSharp** | 纯托管、跨平台、支持 WebP 解码。解出 `Image<Bgra32>` 后拷进 WPF `WriteableBitmap` | 纯托管无 native 分发问题,但 3.x 起 Split License,商业营收超阈值需授权(见下)|
| **Magick.NET** | 功能全,但带原生二进制、体积大 | 偏重,除非已用 |
| 依赖系统 "WebP Image Extensions" | 走原生 WIC | ❌ 不可靠:默认不装,不能假设存在 |

**推荐:libwebp P/Invoke。** 理由:

- **许可零风险**:libwebp 是 BSD-3 许可,商业/闭源分发都无附加义务,规避 ImageSharp 的 Split License 授权问题。
- **职责单一、体积小**:我们只需要"WebP → BGRA 像素",libwebp 正是干这个的,不必为一个解码需求引入通用图像库。
- **代价可控**:需要随程序分发 `libwebp.dll`(区分 x64/arm64),并写一层薄 `P/Invoke` 封装——但接口极少(见下),维护成本低。

> 若不愿分发 native dll、想要纯托管方案,退回 **ImageSharp**;此时须先确认 SelfClaw 的分发场景(是否商业、营收规模)是否触发 Six Labors 商业授权要求(见 §11 风险 2)。

**libwebp 最小接口**(封装在 `WebpSpriteLoader` 内):

```
[DllImport("libwebp")] int  WebPGetInfo(byte* data, UIntPtr size, out int width, out int height);
[DllImport("libwebp")] IntPtr WebPDecodeBGRA(byte* data, UIntPtr size, out int width, out int height);
[DllImport("libwebp")] void WebPFree(IntPtr ptr);
```

解码流程(一次性,加载时):

```
1. 读取 .webp 字节到 byte[]
2. WebPGetInfo 取 width/height
3. WebPDecodeBGRA 解出 BGRA 像素缓冲(native 内存,IntPtr)
4. 创建 WPF WriteableBitmap(PixelFormats.Bgra32),WritePixels 从 native 缓冲拷入
   —— libwebp 输出的是直通(非预乘)alpha,WPF Bgra32 也是直通,直接拷即可,无需预乘换算
5. WebPFree 释放 native 缓冲;WriteableBitmap.Freeze() 后作为不可变 BitmapSource 缓存
6. 之后所有帧切片都从这张 BitmapSource 上 CroppedBitmap,不重复解码
```

> 安全提醒:用户自定义 `.webp` 属于**外部不可信输入**。解码前校验字节非空、`WebPGetInfo` 返回成功且尺寸在合理上限内(防超大图 OOM);解码失败要回退到内置默认宠物而非崩溃。native 缓冲务必在 `try/finally` 里 `WebPFree`,避免泄漏。

### 5.4 native 依赖分发(libwebp.dll)

**已定:方案2 — 官方预编译 + 手动纳管,仅 Windows x64。** 不走 NuGet native 包,来源明确、无第三方中间人,契合供应链可信度要求。

分发清单与工程约定:

| 项 | 约定 |
|----|------|
| 来源 | Google 官方 libwebp 预编译发行包(`libwebp-x.x.x-windows-x64`),仅取其中的 `libwebp.dll` |
| 目标架构 | **仅 win-x64**(不打包 arm64) |
| 仓库位置 | `SelfClaw.Desktop/runtimes/win-x64/native/libwebp.dll`(沿用 .NET RID 目录约定)|
| 版本纳管 | 记录所取 libwebp 版本号与官方下载来源(建议在同目录放 `README`/`VERSION` 标注版本、下载 URL、SHA256)|
| 打包方式 | `.csproj` 用 `<Content>` + `CopyToOutputDirectory=PreserveNewest` 把 dll 拷到输出目录;`[DllImport("libwebp")]` 按名加载 |
| 加载校验 | 启动或首次解码时确认 dll 可加载;缺失/加载失败要能明确报错并回退(禁用宠物或用内置静态帧),不静默崩溃 |

> 备注:目标框架已是 `net10.0-windows10.0.19041.0`,本就限定 Windows;仅 x64 使发布配置与 dll 分发保持单一 RID,后续若要支持 arm64 再补一份 `runtimes/win-arm64/native/`。

### 5.5 SpriteAnimator 帧驱动

- 用 `DispatcherTimer`(UI 线程,和 WPF 渲染同步,足够精确)。
- 间隔 = `max(16ms, 1000/fps)`(参考 open-design `AtlasSprite`)。
- 切换行时**重置到帧 0**,让新动作从头播,不会停在半途(参考 `PetSpriteFace` 的 `setFrame(0)`)。
- 单帧行(frames==1)不启动定时器,直接静态显示。

---

## 6. 交互与行为模块详细设计

### 6.1 状态定义

```
PetInteraction: Idle | Hover | DragRight | DragLeft | DragUp | DragDown | Waiting
```

`PetBehavior` 同时维护交互轴、Agent 工作轴和 ambient 覆盖。它接收 `PetBehaviorEvent`,返回当前动画行和 `PetTimerCommand`;`PetViewModel` 只执行 timer 命令。动画行优先级:
```
用户拖拽/hover > Agent 工作状态 > ambient > waiting/idle
```

目标 spritesheet 缺少工作行时,由同一模块按 `review → waiting → idle` 等候选顺序回退,调用方不自行判断行是否存在。

### 6.2 事件 → 状态迁移

| 事件 | 迁移 |
|------|------|
| 鼠标移入 | → Hover(若正在拖拽则不覆盖)|
| 鼠标移出 | → Idle(若正在拖拽则不覆盖)|
| 左键按下 | 记录起点;暂不改状态 |
| 拖动(过阈值 + 单轴主导)| → DragRight/Left/Up/Down(仅方向变化时切)|
| 左键抬起且未移动 | 视为点击 → 切换气泡;状态回 Hover/Idle |
| 左键抬起且移动过 | 拖拽结束 → Hover(若仍 hover)否则 Idle;持久化新位置 |
| 无操作 45s | Idle → Waiting |
| 任意交互 | 重置 waiting 定时器 |
| Idle 期间 ambient 调度触发 | 设 AmbientRowId;播完清空;下一轮 rest 后再触发 |

### 6.3 窗口拖动(WPF 特有)

open-design 拖的是 DOM 元素的 `right/bottom`;我们拖的是**整个窗口**:

- 方案 A(简单):`MouseDown` 时调 `DragMove()`。但 `DragMove` 会阻塞、拿不到细粒度位移,难做方向判定和抖动过滤。
- 方案 B(推荐):自己在 `MouseMove` 里改 `Window.Left/Top`,配合 `CaptureMouse()`。这样能同时:
  - 算 `dx/dy` 做方向分类(作为行为事件决定 running-left/right/jump)。
  - 做抖动过滤(< 4px 不算拖动)。
  - 结束时区分点击 vs 拖拽。

**位置持久化**(改进 open-design 的"距边距"):
- 存**距最近屏幕工作区左上角的偏移** + 该屏幕标识,或存归一化比例。
- 恢复时 clamp 进当前工作区,避免屏幕数量/分辨率变化后宠物落在不可见区域。
- 复用 `System.Windows.Forms.Screen` / `SystemParameters.WorkArea` 求工作区。

### 6.4 ambient 调度(移植 open-design)

- 仅当交互和工作状态都允许时运行 ambient 调度;离开 Idle 或进入 Agent 工作状态立即取消。
- 用 `DispatcherTimer` 实现 initial-delay → play → rest → play… 循环,每段时长带随机方差。
- ambient 行池排除 idle/waiting/failed(idle 是基线、waiting 留给长空闲、failed 是负面情绪),池内随机且避免连续重复(参考 `pickAmbientRow` 的 `avoidId`)。

---

## 7. 窗口与生命周期

### 7.1 PetWindow 关键属性

```
WindowStyle          = None            // 无边框
AllowsTransparency   = true            // 透明背景
Background           = Transparent
Topmost              = true            // 置顶
ShowInTaskbar        = false           // 不占任务栏
ResizeMode           = NoResize
SizeToContent        = WidthAndHeight  // 或固定小尺寸(如 128×128 含阴影余量)
```

- 窗口尺寸略大于精灵单格,给拖影/气泡留余量。
- 气泡可做成 PetWindow 内的一个 `Popup` 或同窗口内的元素;简单起见先放同窗口内、精灵上方。

### 7.2 生命周期与退出行为(易踩坑)

- 现有 `ShutdownMode = OnMainWindowClose`(`App.xaml.cs:38`)。必须保证:
  - PetWindow **不被设为** `Application.MainWindow`(否则关它就退出应用;或关主窗口时它意外成为新的 main)。
  - 主窗口关闭 → DI host 退出时,`WpfPetWindowAdapter.Dispose()` 显式关闭 PetWindow。
- 宠物"隐藏"用 `Hide()`(保留实例与状态),不是 `Close()`;"关闭宠物功能"才真正 `Close()`/停掉动画定时器。
- 定时器(waiting、ambient、animator)在隐藏时应暂停,避免后台空转耗电。

### 7.3 鼠标穿透(可选,后续)

MVP 不需要。若未来要让气泡/空白区域鼠标穿透到桌面,用 `WS_EX_TRANSPARENT` + `WS_EX_LAYERED`(P/Invoke `SetWindowLong`)。第一版整窗可交互即可。

---

## 8. 数据与持久化

### 8.1 PetSettings(存入 `desktop-settings.json` 的 `"pet"` node)

```
record PetSettings:
  bool     Enabled            // 宠物是否开启
  double?  OffsetX / OffsetY  // 距工作区角的偏移(或归一化)
  string?  ScreenDeviceName   // 上次所在显示器
  string?  SpriteSheetPath    // 素材路径(MVP 可用内置默认,置空)
  bool     PixelArt           // 缩放模式:true=NearestNeighbor
  // 网格布局覆盖(可选,默认 Codex 8×9)
  GridConfig? Grid
```

**网格布局结构(`GridConfig`)**,同时用于 `PetSettings.Grid` 与 `pet.json` 的可选 `grid` 字段:

```
record GridConfig:
  int  Cols                   // 列数(默认 8)
  int  Rows                   // 行数(默认 9)
  int  CellWidth              // 单格宽 px(默认 192);也可由整图宽/Cols 推导
  int  CellHeight             // 单格高 px(默认 208);也可由整图高/Rows 推导
  RowDef[] RowsDef            // 各行定义

record RowDef:
  string Id                   // 行语义 id(idle / waving / running-right ...)
  int    Frames               // 该行有效帧数
  int    Fps                  // 该行播放帧率
```

**布局解析优先级**(与 §5.1 一致):`pet.json.grid` > `PetSettings.Grid` > 内置 Codex 8×9 默认。

- 加载宠物包时:先读 `pet.json`,若含 `grid` 用之;否则看 `PetSettings.Grid`;都没有则套内置默认表。
- `CellWidth/CellHeight` 允许省略——省略时由"整图尺寸 / Cols(Rows)"推导,并**校验能整除**,不整除视为素材/配置不匹配,回退默认或报错(避免切帧错位)。

- `DesktopPetSettingsRepository` 复用 `DesktopSettingsJsonStore.ReadNodeAsync<PetSettings>("pet")` / `WriteNodeAsync`。
- JSON 选项照搬 `ProgrammingAssistantSettingsService`:CamelCase、忽略 null、`WriteIndented`。
- `PetHost` 用一个 `SemaphoreSlim` 串行初始化、命令和 placement 写入,调用方不需要掌握顺序规则。

### 8.2 何时持久化

- 拖拽结束(位置变化)→ 防抖后写(避免高频写盘)。
- 开关切换 → 立即写。

---

## 9. Agent 运行状态联动(已实现)

### 9.1 当前主链路与两个事件来源

当前代码已经不再由 `MainWindowViewModel.HandleAgentStreamEventAsync()` 直接消费事件。实际链路是:

```text
MainWindowViewModel.SendAsync()
  |- ResolveRuntimeAgent()                         // 得到 agent id/name/mode
  |- StartConversationRuntimeState()
  |- new AgentTurnState(runtimeAgent)
  |- ConversationTurnEngine.BeginAssistantMessage()
  `- await foreach AgentStreamEvent
       `- ConversationTurnEngine.ApplyEventAsync()
            |- assistant 文本/思考增量
            |- 工具开始/结束
            |- RunStatusEvent
            `- RunCompletedEvent
```

Direct 和 CLI 都已统一为 `AgentStreamEvent`,因此 Agent 的主要运行节点可以在 `SendAsync()` 的公共编排层旁路投影,不需要分别修改 provider adapter 或 CLI parser。

审批是例外。Direct 的 `write_file` / `run_shell_command` 在工具实现内部调用 `DesktopToolApprovalHandler.RequestApprovalAsync()`,通过 `ApprovalRequested` / `ApprovalCompleted` / `ApprovalExpired` 通知 UI。它**不经过** `AgentStreamEvent`;只监听运行事件会漏掉审批。当前 CLI 继续使用 CLI 自身权限策略,`PermissionRequestedEvent` 仍是未使用的预留事件,也没有对应的审批响应通道。

### 9.2 已实现 seam:独立的 Agent 活动投影模块

不要让 `PetViewModel` 直接订阅 `MainWindowViewModel`、transcript 快照或 runtime adapter。推荐新增桌面级单例 `AgentActivityCoordinator`,把高频且来源分散的运行信息收敛成低频、稳定、可供多个桌面 UI 使用的快照:

```text
AgentStreamEvent --------------------┐
                                     |
MainWindowViewModel turn lifecycle --+--> AgentActivityCoordinator
                                     |      |- 每个 turn 的当前阶段
DesktopToolApprovalHandler ----------┘      |- 审批 FIFO
                                            |- 去重/节流/优先级
                                            `- AgentActivitySnapshotChanged
                                                          |
                                             PetActivityPresenter
                                                          |
                                                  PetViewModel / XAML
```

这个 seam 放在 `SelfClaw.Desktop/Services/AgentActivity/`,原因是:

- `AgentStreamEvent` 仍保持运行时中立,不加入宠物专用事件。
- `ConversationTurnEngine` 继续只负责 transcript/工具记录归约和落库,不承担 WPF 展示策略。
- `PetViewModel` 只消费已经整理好的展示状态,不复制 Direct/CLI/审批判断。
- 后续托盘任务列表、任务中心也能复用同一活动投影。

当前文件拆分(DTO 与逻辑分文件):

```text
SelfClaw.Desktop/Services/AgentActivity/
├─ AgentActivityCoordinator.cs          // 业务逻辑:turn、事件、审批、优先级、去重
├─ AgentActivityContext.cs              // DTO:turn/conversation/agent/mode
├─ AgentActivitySnapshot.cs             // DTO:当前主要节点 + 当前审批
├─ AgentActivityPhase.cs                // enum
└─ AgentActivityOutcome.cs              // enum: succeeded/failed/cancelled

SelfClaw.Desktop/Pet/
├─ Presentation/PetActivityPresenter.cs         // ActivitySnapshot -> 宠物展示状态
├─ Presentation/Models/PetBubbleViewState.cs    // DTO:标题/详情/按钮/是否固定
└─ Behavior/PetWorkState.cs                      // enum:宠物工作轴,不与鼠标交互轴混用
```

`AgentActivityCoordinator` 的外部 interface 保持小而完整:

```csharp
BeginTurn(AgentActivityContext context);
ApplyEvent(Guid turnId, AgentStreamEvent streamEvent);
CompleteInterrupted(Guid turnId, AgentActivityOutcome outcome, string? errorMessage);
TryResolveApproval(Guid toolExecutionId, bool approved);
```

`BeginTurn()` 在真正调用 runtime 前发布"已接收任务"。正常成功/失败由 `ApplyEvent()` 消费 `RunCompletedEvent`;取消和 Desktop 消费侧异常没有 terminal event,必须由 catch 路径显式调用 `CompleteInterrupted()`。所有结束操作都要幂等。

`AgentActivityContext` 至少携带:

- `TurnId`、`ConversationId`、`ConversationTitle`。
- `AgentId`、`AgentName`。
- 最终解析后的 `AgentExecutionMode`(应用 composer override 之后的 Direct/CLI)。
- `StartedAtUtc`。

`RunStartedEvent` 到达后可补充实际 model 和 `CliAgentKind`;气泡主标题仍使用 SelfClaw Agent 名称,例如 `Build · CLI`。

### 9.3 接入 `SendAsync()` 的准确位置

在 `MainWindowViewModel.SendAsync()` 中,把活动投影作为与 transcript reducer 并列的消费者:

```text
runtimeAgent = ResolveRuntimeAgent(... with composer mode override)
turnState = new AgentTurnState(runtimeAgent)
activityCoordinator.BeginTurn(context)

ConversationTurnEngine.BeginAssistantMessage(...)
await foreach (var update in runtime.StreamTurnAsync(...))
{
    await ConversationTurnEngine.ApplyEventAsync(...)
    activityCoordinator.ApplyEvent(turnId, update)
}

catch user cancellation
  -> await ConversationTurnEngine.FinalizeInterruptedAsync(...Cancelled...)
  -> activityCoordinator.CompleteInterrupted(...Cancelled...)

catch runtime/consumer failure
  -> await ConversationTurnEngine.FinalizeInterruptedAsync(...Failed...)
  -> activityCoordinator.CompleteInterrupted(...Failed...)
```

顺序上先让 `ConversationTurnEngine` 完成归约/落库,再发布宠物节点。这样宠物显示"已完成"时,主窗口对应的最终消息已经可见;若事件消费失败,宠物直接走统一失败节点,不会先误报成功。

不要从 `TranscriptRenderState` 反推宠物状态。该快照只持续同步当前选中会话、流式发布有 75ms 节流且不包含 Direct 审批,会丢失后台会话和审批语义。

### 9.4 主要节点映射(不显示每一句话)

宠物只在阶段变化或工具类别变化时更新;`AssistantTextDeltaEvent` / `AssistantThinkingDeltaEvent` 的正文不进入气泡。

| 来源 | 聚合阶段 | 气泡示例 | 是否每次显示 |
|---|---|---|---|
| `BeginTurn` | `Starting` | `Build 正在接收任务` | 每轮一次 |
| `RunStartedEvent` | `Initializing` | `Build · CLI 正在启动` | 每轮一次 |
| `RunStatusEvent(Requesting)` | `Requesting` | `正在连接模型` | 阶段变化时 |
| 首个 `AssistantThinkingDeltaEvent` | `Thinking` | `正在思考方案` | 每个连续思考阶段一次 |
| 首个 `AssistantTextDeltaEvent` | `Responding` | `正在整理回复` | 不显示正文,连续 delta 去重 |
| `ToolCallStartedEvent(Read/List/Search)` | `UsingTool` | `正在查看项目` | 同类连续工具合并 |
| `ToolCallStartedEvent(Edit)` | `UsingTool` | `正在修改文件` | 同类连续工具合并 |
| `ToolCallStartedEvent(Run)` | `UsingTool` | `正在运行命令` | 同类连续工具合并 |
| `ToolCallCompletedEvent(Failed)` | `UsingTool` | `工具执行失败,正在继续处理` | 短暂显示,不等同整轮失败 |
| `ApprovalRequested` | `AwaitingApproval` | `需要批准:运行工作区命令` | 固定显示直到离开队列 |
| `RunCompletedEvent(Succeeded)` | `Succeeded` | `任务完成` | 显示 4-6 秒 |
| `RunCompletedEvent(Failed)` | `Failed` | `任务失败` + 短错误摘要 | 显示 6-10 秒 |
| Desktop cancellation catch | `Cancelled` | `任务已停止` | 显示 4-6 秒 |
| `UsageReportedEvent` / `RawOutputEvent` | 不变 | 不显示 | 忽略 |

工具名称和参数只用于生成短摘要,不能把完整 `ArgumentsJson` 或 assistant 文本塞进气泡。审批场景例外:需要显示操作名称与关键参数预览(文件路径或命令),并提供"查看详情"打开主窗口的完整审批栏。

### 9.5 审批必须共用一条队列

当前 `MainWindow` 自己维护 `_approvalQueue` / `_currentApprovalId`;若宠物再维护一份队列,并行 function call 下两个 UI 可能显示不同队首。实现 Agent 联动时应把这段 FIFO 收进 `AgentActivityCoordinator`(或一个被它组合的内部审批队列),让 Vue 确认栏、宠物和 toast 都基于同一个待决集合。

推荐审批链:

```text
DesktopToolApprovalHandler.ApprovalRequested(request)
  -> AgentActivityCoordinator enqueue
  -> 当前活动快照提升为 AwaitingApproval(全局最高优先级)
  -> PetBubble 固定显示,不启动 4 秒自动隐藏
  -> [拒绝] / [允许]
       `- AgentActivityCoordinator.TryResolveApproval(id, approved)
            `- DesktopToolApprovalHandler.TryResolve(id, approved)

DesktopToolApprovalHandler.ApprovalCompleted(id)
  -> 从唯一 FIFO 移除
  -> 有下一条则提升下一条
  -> 否则恢复该 conversation 在审批前的运行阶段
```

约束:

- 宠物、Vue、Windows toast 最终都调用同一个 `TryResolve()`,其幂等行为可以安全处理竞态;后到的一方收到 `false` 后只刷新快照。
- `ToolApprovalRequest.ToolExecutionId` 与 `ToolCallStartedEvent.ToolCallId` 不是同一个 id。审批只通过 `ConversationId` 关联 turn,不能尝试按这两个 id join。
- 多个审批按 `ApprovalRequested` 到达顺序 FIFO 展示,气泡显示 `还有 N 个请求`。
- 审批气泡不可自动消失;超时仍由现有 5 分钟 handler 控制。
- 订阅 `ApprovalRequested` 的处理器不得向外抛异常。当前 handler 会把订阅异常视为 UI 失败并安全拒绝,coordinator 内部必须捕获并记录异常。
- 主窗口隐藏时宠物可直接审批;宠物也隐藏时继续依赖 Windows toast。
- 当前可落地的是 **Direct 审批**。CLI 若要由 SelfClaw 审批,还需 CLI adapter/协议提供真正的 permission request + response 通道;仅渲染预留的 `PermissionRequestedEvent` 不能完成审批。

### 9.6 宠物动画的两条状态轴

`PetBehavior` 不把 Agent 阶段塞进 `PetInteraction`;鼠标交互与 Agent 工作状态分别维护:

```text
PetInteraction: Idle/Hover/Drag*/Waiting        // 用户交互轴
PetWorkState: None/Working/UsingTool/Approval/
              Succeeded/Failed/Cancelled        // Agent 工作轴
```

最终动画行按以下优先级解析:

1. 正在拖拽 / Hover:用户即时交互始终最高。
2. `Approval`:使用 `waiting` 行。
3. `Failed`:使用 `failed` 行。
4. `UsingTool(Read/List/Search)`:使用 `review` 行。
5. `UsingTool(Edit/Run)`:使用 `running` 行。
6. `Working/Thinking/Responding`:使用 `review` 行。
7. `Succeeded`:短播 `waving` 或 `jumping`,随后回到 idle。
8. 无工作状态:恢复现有 idle/waiting/ambient 调度。

Agent 活跃期间暂停 45 秒 waiting 定时器和 ambient 调度,用户结束 hover/drag 后恢复当前 `PetWorkState` 对应行。以上优先级、timer 命令和 `review -> waiting -> idle` 回退全部由 `PetBehavior` 统一实现并通过序列测试覆盖。

### 9.7 气泡 UI 与交互调整

`PetWindow` 为气泡预留稳定区域,`PetActivityPresenter` 提供普通、terminal 与审批三类生命周期:

- 为气泡预留固定区域并把宠物锚定在窗口底部,避免气泡高度变化导致桌宠在桌面上跳动。
- 主标题最多一行,详情最多两行并省略;常态节点 3-4 秒自动隐藏,terminal 节点稍长,审批固定。
- 审批态显示 `查看详情`、`拒绝`、`允许` 三个可点击控件;按钮区域不参与拖拽手势,点击事件停止向宠物根节点冒泡。
- `PetViewModel.ToggleBubble()` 显示 presenter 保存的最新主要节点;没有活动时才显示 `Ready.`。
- 点击普通节点或`查看详情`时,由 `MainWindow` 处理 `ConversationActivationRequested(conversationId)`:选择对应会话并激活主窗口。`PetHost` 与 `SystemTrayService` 保持单向依赖,host 不负责激活主窗口。
- 位置持久化以宠物 sprite 的锚点为基准,不要因扩大透明窗口而改变用户已保存的视觉位置。

### 9.8 多会话选择与展示优先级

`_conversationRuntimeStates` 按 conversation 保存,切换会话不会取消后台回合,所以 coordinator 必须保存多个活跃 turn,不能只保存一个全局 bool。

展示优先级建议:

1. 当前审批请求。
2. 当前选中 conversation 的活跃 turn。
3. 最近更新的后台活跃 turn。
4. 最近失败/完成的 terminal 通知。
5. idle。

快照带 `ActiveTurnCount`;有多个后台任务时气泡可显示 `Build 正在修改文件 · 另有 1 个任务运行中`。terminal 通知不得覆盖仍待审批的请求。

### 9.9 验证范围

新增测试至少覆盖:

- Direct/Claude/Codex/OpenCode 的不同事件序列映射为相同主要节点。
- 连续 text/thinking delta 不重复刷气泡。
- tool kind 合并、工具失败不误判整轮失败。
- 用户取消没有 `RunCompletedEvent` 时仍进入 `Cancelled`。
- 审批优先于运行节点,审批完成后恢复先前阶段。
- 多审批 FIFO,宠物/Vue/toast 同时 resolve 只有一次成功。
- 多会话时按审批、选中会话、最近活跃的顺序选择。
- Agent 工作期间 ambient/waiting 不覆盖工作动画,hover/drag 结束后恢复工作动画。

---

## 10. 实现与验证状态

当前实现按四个 deep module 组织:

1. **宠物行为**:`PetBehavior` 统一交互、工作状态、waiting、ambient、timer 命令与动画行 fallback。
2. **宠物包 catalog**:`PetPackageCatalog` 统一 manifest、id、排序、grid、路径安全、解码 fallback 与设置页 catalog。
3. **展示生命周期**:`PetActivityPresenter` 统一 bubble auto-hide、审批 pin/动作、terminal 清理和会话激活。
4. **桌宠 host**:`PetHost` 以三个入口封装生命周期、选择、命令串行化与持久化;WPF 和 JSON 通过内部 adapter 替换。

验证覆盖行为序列、catalog/fallback/path safety、presenter 生命周期和 host interface。桌面集成还应手工验证宠物切换、托盘显示/隐藏、跨屏拖拽、审批按钮和气泡会话激活。

---

## 11. 风险与待确认项

| # | 风险/问题 | 影响 | 建议 |
|---|-----------|------|------|
| 1 | **WebP 解码依赖** | 中 | 已定运行期解码;仅 win-x64,官方预编译 `libwebp.dll` 手动纳管到 `runtimes/win-x64/native/`,写薄 `P/Invoke` 封装(§5.3 / §5.4)。dll 缺失/加载失败要报错并回退,不静默崩溃 |
| 2 | **解码库许可** | 低 | 已选 libwebp(BSD-3),无商业授权风险。若改用 ImageSharp 需重新评估 Split License |
| 3 | `ShutdownMode.OnMainWindowClose` 与浮窗冲突 | 中 | 确保 PetWindow 不成为 MainWindow;退出时显式关闭(§7.2)|
| 4 | 素材网格是否为标准 8×9 | 中 | 需确认用户 `.webp` 的实际列/行/格尺寸;`SpriteSheet` 做成可配置 |
| 5 | 多显示器 / DPI 缩放下的定位 | 中 | 存工作区相对偏移 + 恢复时 clamp;测多屏 |
| 6 | 透明窗口 + 硬件加速的性能/黑边 | 低 | `AllowsTransparency=true` 会走软件渲染合成;精灵小、影响可控,需实测 |
| 7 | 定时器后台空转耗电 | 低 | 隐藏/失焦时暂停动画与调度定时器 |

### 需要你确认的点
1. 你的 `.webp` spritesheet 的**具体网格**(列数、行数、每格像素、各行代表什么动作、各行帧数)是否符合 §5.1 的 Codex 8×9 约定?若不同,请提供布局说明。

> 已确认:目标架构仅 **Windows x64**;native 依赖走官方预编译 + 手动纳管(§5.4)。
```
