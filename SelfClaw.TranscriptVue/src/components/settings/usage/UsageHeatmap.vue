<script setup>
import { computed, ref } from 'vue';
import { formatCompactTokens, formatCount, formatDayLabel } from '../../../renderers/usageFormat.js';
import { HEATMAP_MODES, buildHeatmapWeeks, buildMonthLabels, buildWeeklyColumns } from '../../../renderers/usageChartSeries.js';
import UsageTooltip from './UsageTooltip.vue';

const props = defineProps({
	days: { type: Array, default: () => [] },
});

const mode = ref('daily');
const hovered = ref(null);

const heatmap = computed(() => buildHeatmapWeeks(props.days));
const columnData = computed(() => buildWeeklyColumns(heatmap.value.weeks, mode.value));
const monthLabels = computed(() => buildMonthLabels(columnData.value.columns));
const legend = [0, 1, 2, 3, 4];
// 格子本身不可聚焦、tooltip 只在 hover 出现，所以给整块网格一个可朗读的汇总，键盘/读屏用户至少能拿到总量。
const summaryText = computed(() => {
	let total = 0;
	let active = 0;
	for (const day of props.days) {
		const tokens = Number(day?.totalTokens) || 0;
		total += tokens;
		if (tokens > 0) active += 1;
	}

	return `Token 活动：${formatCompactTokens(total)} Token，${active} 天有用量`;
});

function onEnter(event, columnIndex, column, cell) {
	// 每日模式下「今天之后」的格子没有意义；每周/累计模式整列代表一周总量，仍然可以 hover。
	if (cell.isFuture && mode.value === 'daily') return;
	hovered.value = {
		column: columnIndex,
		x: event.clientX,
		y: event.clientY,
		title: mode.value === 'daily'
			? formatDayLabel(cell.date)
			: `${formatDayLabel(column.startDate)} – ${formatDayLabel(column.endDate)}`,
		rows: mode.value === 'daily'
			? [
				{ label: 'Token', value: formatCompactTokens(cell.totalTokens) },
				{ label: '回合', value: formatCount(cell.turnCount) },
				{ label: '工具', value: formatCount(cell.toolCallCount) },
			]
			: [
				{ label: mode.value === 'cumulative' ? '累计 Token' : '本周 Token', value: formatCompactTokens(column.value) },
			],
	};
}

function onLeave() {
	hovered.value = null;
}


</script>

<template>
	<section class="usage-card heatmap-card">
		<header class="usage-card-head">
			<div class="usage-card-heading">

				<h3 class="usage-card-title">Token 活动</h3>
			</div>
			<div class="heatmap-modes" role="tablist" aria-label="热力图模式">
				<button v-for="item in HEATMAP_MODES" :key="item.id" type="button" role="tab"
					:aria-selected="mode === item.id" :class="{ 'is-active': mode === item.id }" @click="mode = item.id">
					{{ item.label }}
				</button>
			</div>
		</header>

		<div class="heatmap-scroll usage-scroll">
			<div class="heatmap-body">
				<div class="heatmap-months" aria-hidden="true">
					<span v-for="group in monthLabels" :key="group.key" class="heatmap-month"
						:style="{ gridColumn: `span ${group.span}` }">{{ group.label }}</span>
				</div>

				<div class="heatmap-grid" role="img" :aria-label="summaryText">
					<div v-for="(column, columnIndex) in columnData.columns" :key="column.key" class="heatmap-week"
						:class="{ 'is-hovered': hovered?.column === columnIndex }">
						<span v-for="cell in column.cells" :key="cell.date" class="heatmap-cell"
							:class="{ 'is-future': mode === 'daily' && cell.isFuture }" :data-level="cell.level"
							@mouseenter="onEnter($event, columnIndex, column, cell)" @mouseleave="onLeave" />
					</div>
				</div>

				<div class="heatmap-legend">
					<span class="legend-label">少</span>
					<span v-for="level in legend" :key="level" class="legend-swatch" :data-level="level" />
					<span class="legend-label">多</span>
				</div>
			</div>
		</div>

		<UsageTooltip v-if="hovered" :x="hovered.x" :y="hovered.y" :title="hovered.title" :rows="hovered.rows" />
	</section>
</template>

<style scoped>
@import '../../../styles/usage-dashboard.css';

.heatmap-modes {
	display: inline-flex;
	gap: 2px;
	padding: 2px;
	border: 1px solid var(--sc-line);
	border-radius: 8px;
	background: var(--sc-raise);
}

.heatmap-modes button {
	height: 22px;
	padding: 0 9px;
	border: 0;
	border-radius: 6px;
	background: transparent;
	color: var(--sc-mute);
	font-family: inherit;
	font-size: var(--fs-9);
	font-weight: 600;
	cursor: pointer;
	transition:
		background 0.16s var(--sc-ease-out),
		color 0.16s var(--sc-ease-out);
}

.heatmap-modes button:hover {
	color: var(--sc-text);
}

.heatmap-modes button.is-active {
	background: var(--sc-acid-soft);
	color: var(--sc-text);
}

.heatmap-modes button:focus-visible {
	outline: 2px solid var(--sc-acid);
	outline-offset: 1px;
}

.heatmap-body {
	display: flex;
	flex-direction: column;
	gap: 4px;
}

.heatmap-months,
.heatmap-grid {
	display: grid;
	grid-template-columns: repeat(52, minmax(0, 1fr));
	gap: 0 2px;
}

.heatmap-month {
	overflow: hidden;
	color: var(--sc-faint);
	font-family: var(--sc-mono);
	font-size: var(--fs-9);
	letter-spacing: 0.04em;
	white-space: nowrap;
}

.heatmap-month:not(:empty) {
	padding-bottom: 2px;
}

.heatmap-week {
	display: flex;
	min-width: 0;
	flex-direction: column;
	gap: 4px;
	border-radius: 4px;
	transition: background 0.12s var(--sc-ease-out);
}

.heatmap-week.is-hovered {
	background: var(--sc-acid-soft);
}

.heatmap-cell {
	width: 100%;
	min-width: 0;
	aspect-ratio: 1;
	border: 1px solid transparent;
	border-radius: 4px;
	background: var(--sc-heat-0);
	transition:
		transform 0.12s var(--sc-ease-spring),
		border-color 0.12s;
}

.heatmap-cell[data-level='1'] {
	background: var(--sc-heat-1);
}

.heatmap-cell[data-level='2'] {
	background: var(--sc-heat-2);
}

.heatmap-cell[data-level='3'] {
	background: var(--sc-heat-3);
}

.heatmap-cell[data-level='4'] {
	background: var(--sc-heat-4);
}

.heatmap-cell:hover {
	z-index: 2;
	border-color: var(--sc-acid);
	transform: scale(1.18);
}

.heatmap-cell.is-future {
	background: transparent;
	pointer-events: none;
}

.heatmap-legend {
	display: flex;
	align-items: center;
	gap: 4px;
	margin-top: 8px;
}

.legend-label {
	color: var(--sc-faint);
	font-family: var(--sc-mono);
	font-size: var(--fs-9);
}

.legend-swatch {
	width: 10px;
	height: 10px;
	border-radius: 3px;
	background: var(--sc-heat-0);
}

.legend-swatch[data-level='1'] {
	background: var(--sc-heat-1);
}

.legend-swatch[data-level='2'] {
	background: var(--sc-heat-2);
}

.legend-swatch[data-level='3'] {
	background: var(--sc-heat-3);
}

.legend-swatch[data-level='4'] {
	background: var(--sc-heat-4);
}

@media (max-width: 640px) {
	.heatmap-body {
		min-width: 620px;
	}
}
</style>
