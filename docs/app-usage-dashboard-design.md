# 关于页应用用量仪表盘设计

> 状态：P0 已实现，并顺带落地「工具调用排行」；下方规格与代码一致
> 目标平台：WPF (.NET 10) + WebView2 内 Vue 3 前端（`SelfClaw.TranscriptVue`）
> 落点：设置页 → 关于（`src/components/settings/About.vue`）
> 参考来源：ZCode「设置 → 使用统计 → 应用用量」的布局与交互（React + Tailwind + Recharts），只借鉴版式与信息结构，组件与图表全部用 Vue 自绘

---

## 1. 目标与范围

把当前只有占位内容的「关于」页升级成一张长期有信息量的页面：上半部分是产品身份信息（版本、build、运行时），下半部分是本地真实用量的可视化仪表盘。数据全部来自本机 SQLite，不联网、不上报。

### 1.1 为什么放在「关于」

- `SettingsView.vue` 的 `activeTarget` 默认值是 `about`，本来就是设置页的默认落地页，目前却是空壳，浪费了首屏。
- 「关于」是用户主动查看「这个软件到底是什么、我用了多少」的位置，语义匹配。
- 不新增导航项，不动侧栏分组结构。

### 1.2 P0（本期必做）

| 能力 | 说明 |
|------|------|
| 品牌信息条 | 产品名、副标题、版本号、build 短哈希、运行时 |
| 终身摘要条 | 5 项：累计 Token、峰值日 Token、最长单回合时长、当前连续、最长连续 |
| Token 活动热力图 | 52 个自然周 × 7 天，三种模式（每日 / 每周 / 累计），hover tooltip |
| 范围切换 | 近 7 天 / 近 30 天，只影响趋势与排行，不影响终身摘要与热力图 |
| 每日 Token 趋势 | 按模型拆线，最多 6 条，原生 SVG 折线 |
| 模型用量排行 | 环形图 + 排行列表（含占比条） |
| 空态 / 错误态 / 刷新 | 空库不显示一排 0；失败可重试 |

### 1.3 P1（其余项）

- ~~工具调用排行~~ → 已随 P0 落地（`UsageToolRanking.vue`）
- 范围概要卡（输入 / 输出 / 缓存命中率 / 回合数 / 工具调用数）
  > 该卡没有渲染方时，数据面不得下发 `rangeData.summary`。P1 实现时再同时加回 DTO、读取器与接线。
- hover 联动：趋势图 hover 某天时高亮排行里当天有量的模型
- 环形图与折线的绘制动画（`pathLength="1"`）

### 1.4 明确不做

- **不改 schema**：数据库仍是 v32，只读，不新增表、不新增索引、不做迁移。见 §9.1。
- **不引图表库**：页面只用到热力网格、折线、环形、横向条四种图形，用 CSS Grid + SVG 足够，且能直接复用 `--sc-*` 主题 token。ECharts 留到将来真需要缩放/刷选时再按需懒加载。
- **不做实时推送**：P0 用「进入页面拉一次 + 手动刷新 + 模块级缓存」。回合结束后主动 push 留到 P2。
- **不做按项目/工作区拆分**：P0 只做全局口径。
- **不做导出**：CSV / 截图导出不在本期。

---

## 2. ZCode 的版式规格（借用的部分）

ZCode 的 `AppUsagePanel` 自上而下的顺序，以及可以直接照搬的比例关系：

| 区块 | ZCode 实现 | 规格 |
|------|-----------|------|
| 页面纵向节奏 | `space-y-5` | 块间距约 20px |
| 主卡片 | `rounded-xl bg-surface p-4` | 12px 圆角、16px 内边距 |
| 终身摘要条 | 单行 flex，5 等分 + 1px 竖分隔 | 每项 `px-4 py-3`，数值大字号、标签弱化色 |
| 热力图网格 | `repeat(52, minmax(0, 1fr))` | 列间距 2px、行间距 4px，格子 `aspect-square`、圆角 4px |
| 热力图色阶 | 5 级 | 0 / 18% / 36% / 58% / 82% 强调色与 surface 混色 |
| 范围切换 | 胶囊 + `h-7` 外壳、`p-0.5` | 选中项 `h-6`、`px-2.5`、小字号 |
| 趋势图 | Recharts LineChart，高 240px | 折线 2px、monotone、无常驻点、`activeDot` 半径 4、隐藏 Y 轴 |
| 环形图 | Recharts Pie，内径 56% / 外径 86% | 分段间隙 2°，桌面端图形区约 256px，与明细两栏 |
| 图表配色 | 6 个 token | sky / teal / violet / rose / indigo / cyan |
| 刷新 | outline 小按钮 | 高 32px，右下角 |

### 2.1 值得保留的四个逻辑（不是样式，是行为）

1. **热力图补齐并对齐周日**：数据不足 52 周也要补 0 格，且第一行永远是周日，末端对齐「本周」。
2. **每周 / 累计不是换数据源**：把一周汇总成单值后，从底部向上填充 7 行；hover 整列高亮。
3. **月份文案锚定「包含每月 1 日」的那一列**，最多显示最近 12 个月，多出的起始月份保留占位但文字留空。
4. **趋势图 Y 轴上限只取可见序列的单点峰值**，不取当日所有模型总量（图中是独立折线而非堆叠），否则曲线会被压扁。

### 2.2 不照搬的部分

- ZCode 是圆润白卡片 + Tailwind 灰阶；SelfClaw 走 console 风格：细描边卡片、等宽数字、mono 小标签、更克制的强调色。
- ZCode 环形图右侧是纯文本列表；SelfClaw 增加一条 3px 占比条，更适合「排行」的扫读。
- ZCode 有「刷新」按钮在最后一个区块下面；SelfClaw 放在页头右上角，与其它设置页的页头动作一致。

---

## 3. SelfClaw 现有数据条件

数据是齐的，缺的只是「全局聚合接口」。

| 表 | 可用字段 | 支撑的指标 |
|----|---------|-----------|
| `turn_usage` | `model`、`input_tokens`、`uncached_input_tokens`、`cached_input_tokens`、`cache_write_input_tokens`、`output_tokens`、`reasoning_tokens`、`total_tokens`、`cost_usd_micros`、`created_at_utc` | 总量、每日趋势、模型排行、缓存命中率、成本 |
| `conversation_turns` | `started_at_utc`、`completed_at_utc`、`status` | 单回合时长、活跃天数、连续天数、失败率 |
| `tool_runs` | `tool_name`、`display_name`、`status`、`duration_ms`、`created_at_utc` | 工具排行、错误率、平均耗时 |
| `conversations` | `kind`（Interactive / Subagent / Automation） | 统计范围控制（P0 不过滤，全量） |

现有 `IUsageStatisticsReader` 只提供「单个会话」和「单个父会话的子代理」两个方法，没有全库聚合能力，因此需要新增一个只读 reader，而不是扩展它——两者职责不同，混在一起会让「会话详情」被迫背上级别的时间窗口参数。

现有可复用的前端逻辑：

- `src/renderers/usageMeter.js` 里的 `formatTokenCount`（K/M 缩写，含 999999 → 1M 的进位修正）。P1 把它抽到 `src/renderers/usageFormat.js`，`usageMeter.js` 改为 re-export，避免出现第二份缩写实现。
- `src/styles/settings-console.css` 的 `sc-page` / `sc-page-head` / `sc-page-body` / `sc-rise` 阶梯入场。
- `src/styles/tokens.css` 的 `--fs-*` 字号刻度、`--sc-*` 别名层、`--sc-mono`。

---

## 4. 信息架构与视觉规格

### 4.1 页面结构

```
关于
├─ 页头：关于 / ABOUT                          [刷新]
├─ 品牌信息条
│  SELFCLAW  ·  Windows 桌面 AI 编程助手
│  v1.0.0 · build 38f3535 · .NET 10 · Windows 11
├─ 应用用量
│  ├─ 区块标题行：USAGE / 应用用量        [近 7 天][近 30 天]
│  ├─ 终身摘要条（5 等分）
│  ├─ Token 活动（52 周热力图）           [每日][每周][累计]
│  ├─ 两栏
│  │  ├─ 左：每日 Token 趋势（按模型）
│  │  └─ 右：模型用量排行（环形图 + 列表）
│  └─ 工具调用排行（跟随上方范围）
└─ 数据说明（折叠）：口径、时区、CLI 未上报说明
```

### 4.2 视觉语言

沿用 console 风格，不引入新的圆角/阴影体系：

| 元素 | 规格 |
|------|------|
| 卡片 | `background: var(--sc-panel)`；`border: 1px solid var(--sc-line)`；`border-radius: 10px`；`padding: 16px` |
| 区块间距 | 16px；卡片内标题到内容 12px |
| 卡片标题 | 中文 13px / 650 |
| Kicker | 10px mono、`letter-spacing: .08em`、`var(--sc-faint)`，前面可带 14px 强调色短横线 |
| 数值 | `var(--sc-mono)` + `font-variant-numeric: tabular-nums` |
| 标签 | 10px mono、`var(--sc-mute)` |
| 强调色 | `var(--sc-acid)`，只用于当前 tab、热力图高等级、hover 数据点 |
| 动效 | 页面块沿用 `.sc-rise` 的 `--i` 阶梯；图表只做一次性绘制动画；`prefers-reduced-motion` 下全部关闭 |

### 4.3 新增设计 token

按 `tokens.css` 头部的三层结构添加，**不允许**在组件里写字面量颜色。

```css
/* ── 1 · 基元层（:root 浅色默认） ── */
--chart-base-1: #3b5bfd;
--chart-base-2: #0f9d63;
--chart-base-3: #7c3aed;
--chart-base-4: #dc4545;
--chart-base-5: #b7791f;
--chart-base-6: #0e7490;

/* ── 1 · 深色覆盖（html[data-theme='dark']） ── */
--chart-base-1: #6d86ff;
--chart-base-2: #34d399;
--chart-base-3: #a78bfa;
--chart-base-4: #f87171;
--chart-base-5: #d9b25f;
--chart-base-6: #22d3ee;
```

```css
/* ── 2 · 语义层（:root） ── */
--chart-1: var(--chart-base-1);
--chart-2: var(--chart-base-2);
--chart-3: var(--chart-base-3);
--chart-4: var(--chart-base-4);
--chart-5: var(--chart-base-5);
--chart-6: var(--chart-base-6);
--chart-other: var(--ink-4);

/* 热力图 5 级：0 级必须与卡片底可见，但不能抢戏 */
--heat-0: color-mix(in srgb, var(--ink-1) 6%, var(--surface-base));
--heat-1: color-mix(in srgb, var(--chart-1) 14%, var(--surface-base));
--heat-2: color-mix(in srgb, var(--chart-1) 30%, var(--surface-base));
--heat-3: color-mix(in srgb, var(--chart-1) 50%, var(--surface-base));
--heat-4: color-mix(in srgb, var(--chart-1) 74%, var(--surface-base));
```

```css
/* ── 3 · 别名层（:root, .sc-root） ── */
--sc-chart-1: var(--chart-1);
--sc-chart-2: var(--chart-2);
--sc-chart-3: var(--chart-3);
--sc-chart-4: var(--chart-4);
--sc-chart-5: var(--chart-5);
--sc-chart-6: var(--chart-6);
--sc-chart-other: var(--chart-other);
--sc-heat-0: var(--heat-0);
--sc-heat-1: var(--heat-1);
--sc-heat-2: var(--heat-2);
--sc-heat-3: var(--heat-3);
--sc-heat-4: var(--heat-4);
```

`color-mix` 里的 `var(--surface-base)` 在使用点惰性求值，因此一套定义就能同时正确服务浅色与深色，不需要重复声明。

### 4.4 响应式

| 断点 | 摘要条 | 两栏 | 热力图 |
|------|--------|------|--------|
| ≥ 900px | 5 列 | `1.35fr / minmax(320px, .9fr)` | 正常等分 52 列 |
| 640–900px | 3 + 2 两行 | 上下堆叠 | 正常等分 |
| < 640px | 2 列 | 上下堆叠 | 外层横向滚动，网格 `min-width: 620px` |

窄屏不让 52 列压成 4px 小点——那样既看不清也无法 hover，宁可横向滚动。

---

## 5. 前端组件拆分

### 5.1 目录

```
src/components/settings/usage/
├─ UsageSummaryStrip.vue
├─ UsageHeatmap.vue
├─ UsageTrendChart.vue
├─ UsageModelRanking.vue
├─ UsageToolRanking.vue        # P1
├─ UsageRangeTabs.vue
├─ UsageTooltip.vue
├─ UsageEmptyState.vue
├─ UsageErrorNotice.vue
├─ usage-dashboard.css         # 共用卡片 / 标题 / kicker / 滚动条
└─ usageChartSeries.js         # 纯函数：热力图补格、月份标签、折线点、环形 dash

src/composables/useUsageOverview.js
```

### 5.2 职责与 props

| 组件 | 职责 | props |
|------|------|-------|
| `UsageSummaryStrip` | 5 项终身指标，等分 + 竖分隔 | `summary` |
| `UsageHeatmap` | 52 周网格、三模式切换、月份标签、tooltip | `days`（`UsageHeatmapDay[]`） |
| `UsageTrendChart` | 多模型每日折线、X 轴抽稀、hover 竖线 | `daily`、`models`、`range` |
| `UsageModelRanking` | 环形图 + 排行列表 + 占比条 | `models` |
| `UsageToolRanking` | 工具横向条排行 | `tools` |
| `UsageRangeTabs` | 胶囊 tab，`v-model` | `modelValue` |
| `UsageTooltip` | `position: fixed` 浮层，自动翻转防溢出 | `x`、`y`、`title`、`rows` |

组件一律 `<style scoped>`，共有部分 `@import './usage-dashboard.css'`。

### 5.3 `About.vue` 结构

```vue
<script setup>
import { ref, computed } from 'vue';
import { RefreshCw } from 'lucide-vue-next';
import { useUsageOverview } from '../../composables/useUsageOverview.js';
import UsageSummaryStrip from './usage/UsageSummaryStrip.vue';
import UsageHeatmap from './usage/UsageHeatmap.vue';
import UsageRangeTabs from './usage/UsageRangeTabs.vue';
import UsageTrendChart from './usage/UsageTrendChart.vue';
import UsageModelRanking from './usage/UsageModelRanking.vue';
import UsageEmptyState from './usage/UsageEmptyState.vue';
import UsageErrorNotice from './usage/UsageErrorNotice.vue';

const { range, overview, loading, error, refresh } = useUsageOverview();
const hasActivity = computed(() => (overview.value?.lifetime.summary.totalTokens ?? 0) > 0);
</script>

<template>
	<main class="about-page sc-root sc-stage sc-page">
		<header class="sc-page-head sc-rise" style="--i: 0">
			<div>
				<h1 class="sc-page-title">关于</h1>
				<p class="sc-page-sub">SELFCLAW / ABOUT</p>
			</div>
			<button type="button" class="usage-refresh" :disabled="loading" @click="refresh">
				<RefreshCw :size="14" :stroke-width="2" :class="{ 'is-spinning': loading }" aria-hidden="true" />
				<span>刷新</span>
			</button>
		</header>

		<div class="sc-page-body">
			<section class="about-hero sc-rise" style="--i: 1">
				<!-- 字标 + 副标题 + 版本 / build / 运行时 -->
			</section>

			<section class="usage-section">
				<header class="usage-head sc-rise" style="--i: 2">
					<div>
						<span class="usage-kicker">USAGE / 应用用量</span>
						<h2 class="usage-title">你的 Agent 用量</h2>
					</div>
					<UsageRangeTabs v-model="range" />
				</header>

				<template v-if="hasActivity">
					<UsageSummaryStrip :summary="overview.lifetime.summary" class="sc-rise" style="--i: 3" />
					<UsageHeatmap :days="overview.lifetime.heatmap.days" class="sc-rise" style="--i: 4" />
					<div class="usage-grid sc-rise" style="--i: 5">
						<UsageTrendChart :daily="overview.rangeData.daily" :models="overview.rangeData.models" :range="range" />
						<UsageModelRanking :models="overview.rangeData.models" />
					</div>
				</template>
				<UsageErrorNotice v-else-if="error" :message="error" @retry="refresh" />
				<UsageEmptyState v-else-if="!loading" />
			</section>
		</div>
	</main>
</template>

<style scoped>
@import '../../styles/settings-console.css';
@import './usage/usage-dashboard.css';
</style>
```

### 5.4 热力图实现要点

```css
.heatmap-scroll { overflow-x: auto; }

.heatmap-grid {
	display: grid;
	grid-template-columns: repeat(52, minmax(0, 1fr));
	gap: 0 2px;
}

.heatmap-week {
	display: flex;
	min-width: 0;
	flex-direction: column;
	gap: 4px;
}

.heatmap-cell {
	width: 100%;
	min-width: 0;
	aspect-ratio: 1;
	border: 1px solid transparent;
	border-radius: 4px;
	background: var(--sc-heat-0);
	transition: transform 0.12s var(--sc-ease-spring), border-color 0.12s;
}

.heatmap-cell[data-level='1'] { background: var(--sc-heat-1); }
.heatmap-cell[data-level='2'] { background: var(--sc-heat-2); }
.heatmap-cell[data-level='3'] { background: var(--sc-heat-3); }
.heatmap-cell[data-level='4'] { background: var(--sc-heat-4); }

.heatmap-cell:hover {
	z-index: 2;
	border-color: var(--sc-acid);
	transform: scale(1.18);
}

@media (max-width: 640px) {
	.heatmap-grid { min-width: 620px; }
}
```

计算逻辑全部放在 `usageChartSeries.js`，组件里不写算术：

- `buildHeatmapWeeks(days, weekCount = 52)`：按日期补 0 格，末端对齐本周，第一行固定周日，列宽可收缩。
- `levelForValue(value, max)`：`Math.min(4, Math.max(1, Math.ceil(value / max * 4)))`；`value <= 0 || max <= 0` 返回 0。
- `buildWeeklyColumns(weeks, mode, metric)`：先算每周汇总，累计模式再前缀求和，然后 `filledRows = ceil(value / max * 7)`，从底部向上填色。
- `buildMonthLabels(columns, locale)`：取每列「包含 1 日」的日期（没有则用周日起始日）归月，`span` 累加，最后把超出 12 个月的起始月份文字置空但保留 `span`。

三个模式的等级必须**各自重算**，因为「每日最大值」和「每周最大值」不是一个量纲，后端不提供 level。

### 5.5 趋势图实现要点

用 `ResizeObserver` 拿容器实际像素，用真实坐标画 SVG，不用 `viewBox` 拉伸（会同时放大线宽和字号）。

- 取模型总用量 Top 6，颜色按 `modelIndex % 6` 固定分配，保证跨刷新稳定。
- `maxTokens` 只遍历已展示序列的单点峰值，不含未展示模型，也不含当日总量。
- 30 天时 X 轴每 5 天一个标签、首尾必显；7 天全显。
- hover：覆盖一层透明 `rect`，按鼠标 x 反推最近索引，画竖线 + 各模型实心点 + tooltip；tooltip 过滤掉当天该模型为 0 的条目。
- 折线用 Catmull-Rom 转三次贝塞尔做平滑，`stroke-width: 2`，不画常驻圆点。

### 5.6 模型排行实现要点

环形图用 SVG `<circle>` 的 `stroke-dasharray` / `stroke-dashoffset`，不引库：

```text
半径 r = 40（viewBox 100×100），周长 C = 2πr
每段 dasharray = `${share * C} ${C}`，dashoffset = -累计占比 * C
每个 circle 额外扣掉 2° 的间隙（gap = C * 2 / 360）
```

- 半径 `r = 40`（`viewBox="0 0 100 100"`）、`stroke-width: 15`，即内径 65% / 外径 95%。ZCode 用内径 56% / 外径 86%，这里按卡片实际高度调厚了一点。
- 中心显示总 Token（大号 mono）+ 单位（弱化色）。
- 超过 6 个模型时：取前 5 + 合并的「其他模型」（用 `--sc-chart-other`）。
- 右侧列表每行：`01` 序号、颜色块、模型名（ellipsis + `title`）、Token 数、占比；下一行是该模型的 3px 占比条。

---

## 6. 数据契约

### 6.1 请求

```json
{
  "type": "usage/get-overview",
  "requestId": "host-1730000000000-1",
  "range": "30d",
  "timeZone": "Asia/Shanghai"
}
```

- `range`：`"7d" | "30d"`，缺省 `"7d"`。
- `timeZone`：IANA 时区名，由前端 `Intl.DateTimeFormat().resolvedOptions().timeZone` 提供；缺省回退 `TimeZoneInfo.Local`。
- 非法值一律 `ArgumentException`，由 `WebViewMessageRouter` 统一转成 `invalid-request`。

### 6.2 响应

```json
{
  "type": "usage-overview",
  "requestId": "host-1730000000000-1",
  "generatedAt": 1730000000000,
  "timeZone": "Asia/Shanghai",
  "range": "30d",
  "app": {
    "version": "1.0.0",
    "build": "38f3535",
    "runtime": ".NET 10.0",
    "os": "Microsoft Windows NT 10.0.22631.0"
  },
  "lifetime": {
    "summary": {
      "totalTokens": 1240000,
      "inputTokens": 980000,
      "outputTokens": 260000,
      "reasoningTokens": 42000,
      "cachedInputTokens": 510000,
      "cacheWriteInputTokens": 32000,
      "cacheHitRate": 0.52,
      "totalTurns": 86,
      "totalSessions": 14,
      "activeDays": 12,
      "currentStreakDays": 3,
      "longestStreakDays": 7,
      "longestTurnMs": 412000,
      "peakDayTokens": 86400,
      "peakDayDate": "2026-08-02",
      "favoriteModel": { "model": "glm-4.6", "totalTokens": 620000, "share": 0.5 },
      "toolCallCount": 128,
      "toolErrorRate": 0.023,
      "firstActivityDate": "2026-06-11",
      "lastActivityDate": "2026-08-31"
    },
    "heatmap": {
      "startDate": "2025-09-01",
      "endDate": "2026-08-31",
      "maxTokens": 86400,
      "days": [
        { "date": "2026-08-31", "totalTokens": 1234, "turnCount": 2, "toolCallCount": 5 }
      ]
    }
  },
  "rangeData": {
    "daily": [
      {
        "date": "2026-08-31",
        "totalTokens": 1234,
        "inputTokens": 1000,
        "outputTokens": 234,
        "turnCount": 2,
        "models": [{ "model": "glm-4.6", "totalTokens": 1234 }]
      }
    ],
    "models": [
      {
        "model": "glm-4.6",
        "totalTokens": 620000,
        "inputTokens": 500000,
        "outputTokens": 120000,
        "turnCount": 42,
        "share": 0.5
      }
    ],
    "tools": [
      {
        "toolName": "search_text",
        "displayName": "搜索文本",
        "callCount": 128,
        "errorCount": 3,
        "errorRate": 0.023,
        "avgDurationMs": 420
      }
    ]
  }
}
```

约定：

- 所有 Token 字段是**真实数字**（`long`），前端负责缩写；后端不做单位格式化。
- `share` 是 0–1 的小数，前端负责按 locale 转百分比。
- `heatmap.days` 只覆盖最近 52 周（364 天），且**已补齐空白日**；终身摘要另行聚合，不受这 52 周限制。
- `maxTokens` 是这 52 周内的日峰值，前端三模式各自重算等级，不直接用它。
- 时间戳统一用 Unix 毫秒（`generatedAt`），日期统一 `yyyy-MM-dd` 本地自然日字符串。
- `rangeData` **只有** `daily` / `models` / `tools` 三段。范围概要（输入 / 输出 / 缓存命中率 / 回合数）目前没有渲染方，因此不下发；加 UI 时再同时加回 DTO。
- 不带模型名的回合，`model` 是空串 `""` 而不是「未知模型」：数据层不产出中文展示文案。
- 未来日期的回合（时钟回拨 / 跨时区写入）不参与「最后活动日 / 峰值日 / 连续天数」这类日期指标，但它的 token 仍计入总量——那次用量确实发生过。

---

## 7. 宿主实现

### 7.1 新增文件

```
SelfClaw.Core/Interfaces/Usage/IUsageOverviewReader.cs
SelfClaw.Core/Models/Usage/UsageRange.cs
SelfClaw.Core/Models/Usage/UsageOverviewQuery.cs
SelfClaw.Core/Models/Usage/UsageOverview.cs
SelfClaw.Core/Models/Usage/UsageAppInfo.cs
SelfClaw.Core/Models/Usage/UsageLifetimeSnapshot.cs
SelfClaw.Core/Models/Usage/UsageSummary.cs
SelfClaw.Core/Models/Usage/UsageFavoriteModel.cs
SelfClaw.Core/Models/Usage/UsageHeatmap.cs
SelfClaw.Core/Models/Usage/UsageHeatmapDay.cs
SelfClaw.Core/Models/Usage/UsageDailyPoint.cs
SelfClaw.Core/Models/Usage/UsageDailyModelCount.cs
SelfClaw.Core/Models/Usage/UsageModelUsage.cs
SelfClaw.Core/Models/Usage/UsageModelTotals.cs
SelfClaw.Core/Models/Usage/UsageToolUsage.cs

SelfClaw.Infrastructure/Data/Sqlite/Repositories/SqliteUsageOverviewReader.cs
SelfClaw.Desktop/Services/Usage/UsageStatisticsBridge.cs
```

按 `AGENTS.md` 约定：DTO 一律 `record` + 主构造器，一个文件一个 DTO，无方法无副作用；reader 用 `internal sealed`，`private readonly _camelCase` 字段，Infrastructure 全量 `ConfigureAwait(false)`。

### 7.2 接口

```csharp
public interface IUsageOverviewReader
{
    Task<UsageOverview> ReadAsync(UsageOverviewQuery query, CancellationToken cancellationToken = default);
}
```

`UsageOverviewQuery(UsageRange Range, TimeZoneInfo TimeZone)`。

### 7.3 取数策略

**两次查询 + C# 侧分桶**，不做 SQL `date()` 分组。

理由见 §9.2。具体：

1. **终身聚合**（一条 `SUM` 语句，不分组）：
   ```sql
   SELECT COUNT(*),
          COUNT(DISTINCT u.conversation_id),
          COALESCE(SUM(COALESCE(u.input_tokens, 0)), 0),
          COALESCE(SUM(COALESCE(u.uncached_input_tokens, 0)), 0),
          COALESCE(SUM(COALESCE(u.cached_input_tokens, 0)), 0),
          COALESCE(SUM(COALESCE(u.cache_write_input_tokens, 0)), 0),
          COALESCE(SUM(COALESCE(u.output_tokens, 0)), 0),
          COALESCE(SUM(COALESCE(u.reasoning_tokens, 0)), 0),
          COALESCE(SUM(COALESCE(u.total_tokens,
              COALESCE(u.input_tokens, 0) + COALESCE(u.output_tokens, 0))), 0)
   FROM turn_usage u;
   ```

2. **窗口内的逐回合明细**（起始 = 距今 52 周；范围窗口是它的子集，用 `JOIN conversation_turns` 拿开始时间）：
   ```sql
   SELECT t.started_at_utc, COALESCE(u.model, ''), u.input_tokens, u.output_tokens,
          u.reasoning_tokens, u.total_tokens, t.completed_at_utc, u.conversation_id
   FROM turn_usage u
   JOIN conversation_turns t ON t.id = u.turn_id
   WHERE t.started_at_utc >= $since
   ORDER BY t.started_at_utc;
   ```
   `turn_usage` 本身就是「一回合一行」，所以行数等于窗口内回合数，不构成数据放大。同一个结果集同时供热力图、每日趋势与模型排行；近 7 / 30 天只是它上面的一次过滤。

2b. **终身日桶（只取两列）**：`activeDays` / `peakDay` / 连续天数需要全历史，但只需要「哪天、多少 token」。宽列查询限制在 52 周窗口内，终身口径另走一条窄查询：
   ```sql
   SELECT t.started_at_utc,
          COALESCE(u.total_tokens, COALESCE(u.input_tokens, 0) + COALESCE(u.output_tokens, 0))
   FROM turn_usage u
   JOIN conversation_turns t ON t.id = u.turn_id;
   ```

3. **工具聚合**（窗口内）：
   ```sql
   SELECT COALESCE(NULLIF(TRIM(display_name), ''), tool_name) AS display_name,
          tool_name,
          COUNT(*),
          SUM(CASE WHEN status = 2 THEN 1 ELSE 0 END),
          AVG(duration_ms)
   FROM tool_runs
   WHERE created_at_utc >= $since
   GROUP BY tool_name, COALESCE(NULLIF(TRIM(display_name), ''), tool_name)
   ORDER BY COUNT(*) DESC
   LIMIT 20;
   ```
   `status = 2` 是 `ToolExecutionStatus.Failed`。

4. **单回合最长时长**：
   ```sql
   SELECT MAX((julianday(completed_at_utc) - julianday(started_at_utc)) * 86400000)
   FROM conversation_turns
   WHERE completed_at_utc IS NOT NULL;
   ```
   这条用 `julianday` 只做**差值**，不做日期归桶，因此不受 §9.2 的格式风险影响；数值再夹到 `>= 0`。

### 7.4 分桶与派生指标

全部在 C# 侧用 `TimeZoneInfo.ConvertTime` 完成：

| 指标 | 口径 |
|------|------|
| 范围窗口 | 本地自然日含今天：起点 = `今天 00:00 (本地) - (N-1) 天` |
| 每日分桶 | `TimeZoneInfo.ConvertTime(started_at_utc, query.TimeZone).Date` |
| 活跃天 | 有 ≥1 回合的本地日期数 |
| 当前连续 | 从「今天」开始向前数；今天无活动则从「昨天」起算（否则每天零点都会掉到 0） |
| 最长连续 | 全历史连续活跃日的最大长度 |
| 峰值日 | 终身逐日汇总里的最大 Token 日，同值取最近的一天 |
| 最常用模型 | 终身按 `model` 汇总 Token 降序第一；`model` 为 `NULL` 时原样输出空串，由前端 `resolveModelLabel` 显示「未知模型」——展示文案不下沉到 Infrastructure |
| 缓存命中率 | 复用现有公式 `cached / (cached + uncached)`，cache write 不计入（与 `SqliteUsageStatisticsReader.CacheHitRate` 保持一致，避免同一应用出现两种命中率） |
| 比例 `share` | `模型 Token / 窗口总 Token`，分母为 0 时全 0 |

### 7.5 桥接与路由

`UsageStatisticsBridge`：

- `TryHandleAsync(string type, JsonElement payload, CancellationToken)`，只认 `usage/get-overview`，其它返回 `null`。
- 读 `range`、`timeZone`；`TimeZoneInfo.FindSystemTimeZoneById` 失败时回退 `TimeZoneInfo.Local`（前端时区串来自浏览器，可能带平台差异，不值得为此报错）。
- 返回匿名对象，字段名走 `WebViewHostChannel` 的 `JsonNamingPolicy.CamelCase`，与 §6.2 对齐。
- 异常不吞：`OperationCanceledException` 必须重抛，其余交给 router 的 `PostFailure` 统一成 `operation-failed`。

`WebViewMessageRouter`：

- 在 `switch (type)` 的 `default:` 之前插入：
  ```csharp
  case var name when name.StartsWith("usage/", StringComparison.Ordinal):
      response = await _usageStatisticsBridge.TryHandleAsync(type, payload, cancellationToken);
      break;
  ```
- 构造函数在可选参数区、`ILogger` 之前插入 `UsageStatisticsBridge? usageStatisticsBridge = null`；`_usageStatisticsBridge` 为 `null` 且收到 `usage/` 消息时抛 `ArgumentException("Unsupported usage message.")`。
  > 之所以放可选参数区，是因为现有 `WebViewMessageRouterTests` 的工厂按位置传参，插到必需参数区会连带改测试代码；这条与既有的 `gitWorkspaceBridge` / `automationBridge` 处理方式一致。
- `RouteShellIntentAsync` 不动。

### 7.6 DI 注册

- `SelfClaw.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs`，紧挨现有 `IUsageStatisticsReader` 注册后面一行：
  ```csharp
  services.AddSingleton<IUsageOverviewReader, SqliteUsageOverviewReader>();
  ```
- `SelfClaw.Desktop/Composition/DesktopServiceRegistration.cs`：`services.AddSingleton<UsageStatisticsBridge>();`，并在 `new WebViewMessageRouter(...)` 的调用里补一个实参。

### 7.7 app 信息

`UsageAppInfo` 由 Desktop 侧填充（Infrastructure 不感知 WPF/入口程序集）：

- `version`：`Assembly.GetEntryAssembly()?.GetName().Version` → `major.minor.patch`
- `build`：`AssemblyInformationalVersionAttribute` 里 `+` 之后的短哈希，取前 7 位；没有则 `null`
- `runtime`：`RuntimeInformation.FrameworkDescription`
- `os`：`RuntimeInformation.OSDescription`

---

## 8. 前端数据层

`useUsageOverview.js`：

```js
const CACHE_TTL_MS = 30_000;

export function useUsageOverview() {
	const { requestLatest } = useHostBridge();
	const range = ref('7d');
	const overview = ref(null);
	const loading = ref(false);
	const error = ref('');

	async function load(nextRange) {
		loading.value = true;
		error.value = '';
		try {
			overview.value = await requestLatest('usage-overview', 'usage/get-overview', {
				range: nextRange,
				timeZone: Intl.DateTimeFormat().resolvedOptions().timeZone,
			});
		} catch (failure) {
			if (isSuperseded(failure)) return;
			error.value = failure.message;
		} finally {
			loading.value = false;
		}
	}

	watch(range, (next) => load(next), { immediate: true });
	return { range, overview, loading, error, refresh: () => load(range.value) };
}
```

- 用 `requestLatest` 而不是 `request`：快速来回切 7d/30d 时，旧请求必须被作废，否则慢的那次会覆盖新结果。
- 缓存放在模块级 `Map`（key = `range|timeZone`），TTL 30 秒。`About` 是设置页默认页，用户反复开关设置会反复扫库，缓存是最省事的一层保护。
- 不用 `onMounted` 拉数据，改为 `watch(..., { immediate: true })`，理由同上：组件是 `defineAsyncComponent`，卸载/重建很频繁。

---

## 9. 关键约束与陷阱

### 9.1 不改 schema

数据库固定 v32，非 v32 或未版本化一律只读拒绝。本设计**不新增表、不新增索引、不做迁移**。

代价：`turn_usage` 只有 `(conversation_id, created_at_utc)` 复合索引，没有 `created_at_utc` 单列索引，所以全库按时间过滤会走扫描。个人数据量下（万级回合）可以接受；真到十万级再单独走一次带版本号的索引迁移，**不要在本功能里顺手改**。

### 9.2 日期分桶不能交给 SQLite

`created_at_utc` / `started_at_utc` 是 .NET `DateTimeOffset.ToString("O")` 写入的，形态是 `2026-08-31T12:34:56.7890000+00:00`（7 位小数 + 偏移量）。SQLite 的 `date()` / `strftime()` 对这种形态的容忍度不值得赌，一旦解析失败会静默返回 `NULL`，把整张热力图变成空白——而且很难在测试里发现。

因此：**日期归桶只在 C# 侧做**。SQL 里只做「范围过滤」和「`julianday` 差值」，不产出日期字符串。

### 9.3 时区必须是调用端的

热力图的「今天」、连续天数、7d/30d 的窗口边界都依赖用户本地时区。宿主进程与 WebView 通常同机同时区，但用户可能改了系统时区而进程已启动，所以请求里显式带 `timeZone`，不让宿主隐式取 `TimeZoneInfo.Local`。

### 9.4 CLI 回合可能没有用量

Claude Code / Codex / OpenCode 的 usage 依赖各自 parser；解析不到时 `turn_usage` 可能整行缺失或字段全 `NULL`。页面底部必须有一句说明：

> 统计来自本地 `turn_usage`，仅包含供应商上报的用量。未上报的 CLI 回合不计入。

不要用「字符数 ÷ 3」去补——那会造出一份和真实 token 不可比的数字，比空白更糟。

### 9.5 子代理与自动化的口径

`turn_usage` 里父回合与子回合是两行，直接求和就是「应用总用量」，不是重复计算。P0 全量统计（含 Interactive / Subagent / Automation），文案里写明「含子代理与自动化」。

将来若要「只统计主对话」，加 `JOIN conversations c ON c.id = u.conversation_id AND c.kind = 0`，**不要**用 `parent_conversation_id IS NULL` 之类容易误伤的判据。

### 9.6 `About` 是设置页默认页

`SettingsView.vue` 的 `activeTarget = ref('about')`，意味着一次设置页打开就会触发用量查询。因此：

- 组件必须 `defineAsyncComponent` 懒加载（已在 `SettingsView.vue` 注册，不要改回同步导入）。
- 数据层必须带缓存（§8）。
- 首屏绝不能因为图表模块加载失败而白屏——所有 SVG 都是内联的，不引入外部图表依赖，天然规避 ZCode 那边「Recharts 在模块初始化时阻断 renderer 启动」的问题。

### 9.7 数字与空态

- 空库显示空态卡片，不显示一排 `0`。
- 除 `0` 与 `--` 之外的未知值用 `—`（em dash），不用 `0` 冒充。
- 占比为 0 的模型不进排行；`share` 小于 1% 显示 `<1%`，不要显示 `0%`。

---

## 10. 落地步骤

按顺序执行，每一步都可独立编译。

### P0-1 数据模型与读取器

1. 新增 §7.1 列出的 Core DTO 与接口。
2. 实现 `SqliteUsageOverviewReader`：终身聚合 → 窗口明细 → 工具聚合 → 最长回合；分桶与派生指标按 §7.4。
3. `ServiceCollectionExtensions` 注册。
4. 新增 `SelfClaw.Tests/Infrastructure/Data/Sqlite/Repositories/SqliteUsageOverviewReaderTests.cs`。

### P0-2 桥接与路由

5. 新增 `UsageStatisticsBridge`。
6. `DesktopServiceRegistration` 注册 + `WebViewMessageRouter` 加 case 与构造参数。
7. 新增 `SelfClaw.Tests/Desktop/Services/Usage/UsageStatisticsBridgeTests.cs`。

### P0-3 设计 token

8. `tokens.css` 按 §4.3 补三处（基元浅色、基元深色、语义 + 别名）。

### P0-4 前端数据层与纯函数

9. 新增 `composables/useUsageOverview.js`。
10. 新增 `renderers/usageFormat.js`，把 `usageMeter.js` 的 `formatTokenCount` 迁入并 re-export；新增 `formatCompactTokens`、`formatPercent`、`formatDuration`、`formatDayLabel`、`formatMonthLabel`。
11. 新增 `components/settings/usage/usageChartSeries.js`（补格、等级、周聚合、月份标签、折线点、环形 dash）。

### P0-5 组件

12. `usage-dashboard.css`（卡片、kicker、标题、tab、滚动条）。
13. `UsageSummaryStrip` → `UsageHeatmap` → `UsageRangeTabs` → `UsageTrendChart` → `UsageModelRanking`。
14. 重写 `About.vue`（品牌条 + 仪表盘 + 空态/错误态 + 刷新）。

### P0-6 测试与验收

15. 前端单测：`tests/usageChartSeries.test.js`、`tests/usageFormat.test.js`、`tests/aboutUsageDashboard.test.js`（组件挂载 + mock `window.chrome.webview`）。
16. 手动验收见 §11.2。

### P1

17. `UsageToolRanking` + 范围概要卡。
18. hover 联动、绘制动画。

---

## 11. 测试与验收

### 11.1 必须写的测试

| 层 | 覆盖点 |
|----|--------|
| Reader | 空库返回全 0 且不抛；单模型；多模型排序与 `share`；跨天分桶；`model` 为 `NULL` 归「未知模型」；`total_tokens` 为 `NULL` 时回退 `input + output`；缓存命中率与现有 reader 一致；工具错误率与平均耗时；连续天数（今/昨/断档三种）；范围窗口边界（本地 00:00） |
| Bridge | 正常返回、非法 `range` 抛 `ArgumentException`、非法 `timeZone` 回退本地时区、未知 `type` 返回 `null` |
| 纯函数 | 52 周补格与周日对齐；三模式等级各自重算；月份标签最多 12 个且 `span` 总和等于列数；环形 dash 累加为整周长；`formatTokenCount` 的 999999 → 1M |
| 组件 | 空态不渲染图表；错误态显示重试；切 range 后旧请求被 `SupersededError` 吞掉且不写 `error` |

### 11.2 手动验收

```powershell
dotnet build SelfClaw.slnx
dotnet run --project SelfClaw.Desktop/SelfClaw.Desktop.csproj
```

- 打开设置 → 关于（默认页即触发）：3 秒内出现品牌条与仪表盘，无白屏、无控制台报错。
- 摘要条 5 项在当前窗口宽度下不换行、不截断成 `...`。
- 热力图：第一行是周日；最后一行最后一天是今天；月份文案不重叠；hover 任一天出现 tooltip，不超出窗口边界。
- 切「每周 / 累计」：整列填充、hover 整列高亮、tooltip 显示周区间。
- 切「近 30 天」：趋势图 X 轴标签不重叠，排行数字随之变化，摘要条与热力图不变。
- 快速连点 7d / 30d 五次：最终显示与最后选中项一致，无闪烁回退。
- 暗色主题：热力图 0 级与卡片底可区分、4 级与文字对比度可读；6 条折线区分度足够。
- 窗口缩到 720px：摘要条两行、两栏堆叠、热力图可横向滚动。
- 断网 / 空库：显示空态或错误态，不显示 `NaN`、`Infinity`、`undefined`。

### 11.3 前端单测

```powershell
cd SelfClaw.TranscriptVue
npm test
```

---

## 12. 后续演进

| 方向 | 说明 |
|------|------|
| 实时更新 | 回合终态后由 `TranscriptPublisher` 旁路 push `usage-overview-changed`，前端增量刷新 |
| 按项目拆分 | 用 `conversations.workspace_root_id` 分组，加维度切换 |
| 成本视图 | `turn_usage.cost_usd_micros` 已有数据，可加「累计花费」与逐日成本 |
| 性能 | 回合数到十万级时，单独走一次带版本号的索引迁移（`turn_usage(created_at_utc)`），不要顺手改 schema |
| 导出 | 导出 CSV / 分享长图 |
