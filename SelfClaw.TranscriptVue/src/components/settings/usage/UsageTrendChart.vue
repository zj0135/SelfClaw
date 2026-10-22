<script setup>
import { computed, onMounted, onUnmounted, ref, watch } from 'vue';
import { formatCompactTokens, formatDayLabel, unknownText } from '../../../renderers/usageFormat.js';
import {
	buildLayout,
	buildSeriesPoints,
	buildSmoothPath,
	buildTrendAxisLabels,
	buildTrendSeries,
	indexFromPlotX,
	xForIndex,
} from '../../../renderers/usageChartSeries.js';
import UsageTooltip from './UsageTooltip.vue';

const props = defineProps({
	daily: { type: Array, default: () => [] },
	models: { type: Array, default: () => [] },
	range: { type: String, default: '7d' },
});

// 测量失败（或 jsdom 里没有 ResizeObserver）时用固定尺寸兜底，图表不会因为 0 宽而消失。
const DEFAULT_WIDTH = 560;
const DEFAULT_HEIGHT = 240;
const GRID_RATIOS = [0, 0.5, 1];

const plot = ref(null);
const measured = ref({ width: 0, height: 0 });
// { index, x, y }：index 决定画竖线与实心点，x/y 决定 tooltip 跟随指针。
const pointer = ref(null);
let observer = null;

// 窗口变短后旧的 hover 下标不再有意义：留着会让 points[index] 越界（切范围时键盘操作不会触发 mouseleave）。
watch(() => props.daily, () => { pointer.value = null; });

onMounted(() => {
	if (typeof ResizeObserver === 'undefined' || !plot.value) return;
	observer = new ResizeObserver((entries) => {
		const rect = entries[0]?.contentRect;
		if (rect) measured.value = { width: rect.width, height: rect.height };
	});
	observer.observe(plot.value);
});

onUnmounted(() => observer?.disconnect());

const layout = computed(() => buildLayout(
	measured.value.width || DEFAULT_WIDTH,
	measured.value.height || DEFAULT_HEIGHT,
));
const trend = computed(() => buildTrendSeries(props.daily, props.models));
const lines = computed(() => trend.value.series.map((series) => {
	const points = buildSeriesPoints(series.values, layout.value, trend.value.maxTokens);
	return { ...series, points, path: buildSmoothPath(points) };
}));
const axisLabels = computed(() => buildTrendAxisLabels(trend.value.labels, layout.value.plotWidth));
const gridLines = computed(() => GRID_RATIOS.map((ratio) => layout.value.top + layout.value.plotHeight * (1 - ratio)));
const hasSeries = computed(() => trend.value.series.length > 0);
const rangeLabel = computed(() => (props.range === '30d' ? '近 30 天' : '近 7 天'));
const peakText = computed(() => (trend.value.maxTokens > 0
	? formatCompactTokens(trend.value.maxTokens)
	: unknownText));
const cursorX = computed(() => (pointer.value
	? xForIndex(pointer.value.index, trend.value.labels.length, layout.value)
	: 0));

// 当天该模型为 0 的条目不进 tooltip —— 6 条线的图里大半是 0，列出来只会淹没重点。
const tooltip = computed(() => {
	if (!pointer.value) return null;
	const rows = lines.value
		.map((line) => ({ label: line.label, value: line.points[pointer.value.index]?.value ?? 0 }))
		.filter((row) => row.value > 0)
		.map((row) => ({ label: row.label, value: formatCompactTokens(row.value) }));
	return {
		title: formatDayLabel(trend.value.labels[pointer.value.index]),
		rows,
		x: pointer.value.x,
		y: pointer.value.y,
	};
});

function onMove(event) {
	if (!hasSeries.value) return;
	const rect = event.currentTarget.getBoundingClientRect();
	pointer.value = {
		index: indexFromPlotX(event.clientX - rect.left, trend.value.labels.length, layout.value),
		x: event.clientX,
		y: event.clientY,
	};
}

function onLeave() {
	pointer.value = null;
}

function anchorFor(index) {
	if (index === 0) return 'start';
	return index === trend.value.labels.length - 1 ? 'end' : 'middle';
}
</script>

<template>
	<section class="usage-card trend-card">
		<header class="usage-card-head">
			<div class="usage-card-heading">
				<span class="usage-kicker">Trend</span>
				<h3 class="usage-card-title">{{ rangeLabel }}每日 Token</h3>
			</div>
			<span class="trend-peak usage-label">单点峰值 {{ peakText }}</span>
		</header>

		<div ref="plot" class="trend-plot">
			<svg v-if="hasSeries" class="trend-svg" :width="layout.width" :height="layout.height" role="img"
				:aria-label="`${rangeLabel}每日 Token 趋势，按模型拆分`">
				<line v-for="(y, index) in gridLines" :key="`grid-${index}`" class="trend-grid" :x1="layout.left"
					:x2="layout.left + layout.plotWidth" :y1="y" :y2="y" />

				<path v-for="line in lines" :key="line.model" class="trend-line" :d="line.path"
					:style="{ stroke: line.color }" />

				<g v-if="pointer">
					<line class="trend-cursor" :x1="cursorX" :x2="cursorX" :y1="layout.top"
						:y2="layout.top + layout.plotHeight" />
					<circle v-for="line in lines" :key="`${line.model}-dot`" class="trend-dot"
						:cx="line.points[pointer.index].x" :cy="line.points[pointer.index].y" r="3"
						:style="{ fill: line.color }" />
				</g>

				<text v-for="entry in axisLabels" :key="`axis-${entry.index}`" class="trend-axis"
					:x="xForIndex(entry.index, trend.labels.length, layout)" :y="layout.top + layout.plotHeight + 18"
					:text-anchor="anchorFor(entry.index)">
					{{ formatDayLabel(entry.label) }}
				</text>

				<rect class="trend-hit" :x="layout.left" :y="layout.top" :width="layout.plotWidth"
					:height="layout.plotHeight" @mousemove="onMove" @mouseleave="onLeave" />
			</svg>
			<p v-else class="trend-empty">所选范围内没有可绘制的模型用量</p>
		</div>

		<ul v-if="hasSeries" class="trend-legend">
			<li v-for="line in lines" :key="`legend-${line.model}`">
				<span class="legend-dot" :style="{ background: line.color }" />
				<span class="legend-name" :title="line.label">{{ line.label }}</span>
				<span class="legend-value usage-value">{{ formatCompactTokens(line.totalTokens) }}</span>
			</li>
		</ul>

		<UsageTooltip v-if="tooltip" :x="tooltip.x" :y="tooltip.y" :title="tooltip.title" :rows="tooltip.rows" />
	</section>
</template>

<style scoped>
@import '../../../styles/usage-dashboard.css';

.trend-card {
	display: flex;
	min-width: 0;
	flex-direction: column;
}

.trend-peak {
	white-space: nowrap;
}

.trend-plot {
	position: relative;
	height: 240px;
	min-width: 0;
}

.trend-svg {
	display: block;
	overflow: visible;
}

.trend-grid {
	stroke: var(--sc-line);
	stroke-width: 1;
}

.trend-line {
	fill: none;
	stroke-width: 2;
	stroke-linecap: round;
	stroke-linejoin: round;
}

.trend-cursor {
	stroke: var(--sc-line-2);
	stroke-dasharray: 3 3;
	stroke-width: 1;
}

.trend-dot {
	stroke: var(--sc-panel);
	stroke-width: 1.5;
}

.trend-axis {
	fill: var(--sc-faint);
	font-family: var(--sc-mono);
	font-size: var(--fs-9);
}

.trend-hit {
	fill: transparent;
}

.trend-empty {
	margin: 0;
	padding-top: 92px;
	color: var(--sc-faint);
	font-size: var(--fs-11);
	text-align: center;
}

.trend-legend {
	display: flex;
	flex-wrap: wrap;
	gap: 6px 14px;
	margin: 8px 0 0;
	padding: 0;
	list-style: none;
}

.trend-legend li {
	display: inline-flex;
	align-items: center;
	gap: 6px;
	min-width: 0;
}

.legend-dot {
	width: 7px;
	height: 7px;
	border-radius: 50%;
}

.legend-name {
	max-width: 132px;
	overflow: hidden;
	color: var(--sc-mute);
	font-size: var(--fs-10);
	text-overflow: ellipsis;
	white-space: nowrap;
}

.legend-value {
	color: var(--sc-faint);
	font-size: var(--fs-9);
}
</style>
