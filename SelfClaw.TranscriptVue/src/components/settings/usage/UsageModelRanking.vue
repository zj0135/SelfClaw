<script setup>
import { computed, ref } from 'vue';
import { formatCompactTokens, formatPercent, unknownText } from '../../../renderers/usageFormat.js';
import { buildRankingRows, buildRingSegments } from '../../../renderers/usageChartSeries.js';
import UsageTooltip from './UsageTooltip.vue';

const props = defineProps({
	models: { type: Array, default: () => [] },
});

const ring = computed(() => buildRingSegments(props.models));
const rows = computed(() => buildRankingRows(props.models));
const hasModels = computed(() => rows.value.length > 0);
const totalText = computed(() => (ring.value.totalTokens > 0
	? formatCompactTokens(ring.value.totalTokens)
	: unknownText));

const hoveredModel = ref(null);
const pointer = ref(null);

const hoveredItem = computed(() => {
	if (!hoveredModel.value) return null;
	return ring.value.segments.find((s) => s.model === hoveredModel.value)
		|| rows.value.find((r) => r.model === hoveredModel.value)
		|| null;
});

const tooltip = computed(() => {
	if (!hoveredItem.value || !pointer.value) return null;
	const item = hoveredItem.value;
	const detailRows = [
		{ label: 'Token', value: formatCompactTokens(item.totalTokens) },
		{ label: '占比', value: formatPercent(item.share) },
	];
	if (item.inputTokens > 0) {
		detailRows.push({ label: '输入', value: formatCompactTokens(item.inputTokens) });
	}
	if (item.outputTokens > 0) {
		detailRows.push({ label: '输出', value: formatCompactTokens(item.outputTokens) });
	}
	return {
		title: item.label,
		rows: detailRows,
		x: pointer.value.x,
		y: pointer.value.y,
	};
});

function onEnter(event, item) {
	hoveredModel.value = item.model;
	pointer.value = { x: event.clientX, y: event.clientY };
}

function onMove(event) {
	if (pointer.value) {
		pointer.value = { x: event.clientX, y: event.clientY };
	}
}

function onLeave() {
	hoveredModel.value = null;
	pointer.value = null;
}
</script>

<template>
	<section class="usage-card ranking-card">
		<header class="usage-card-head">
			<h3 class="usage-card-title">模型用量排行</h3>
		</header>

		<div v-if="hasModels" class="ranking-body">
			<div class="ranking-ring" :class="{ 'has-hover': Boolean(hoveredModel) }">
				<svg viewBox="0 0 100 100" role="img" aria-label="模型 Token 占比">
					<circle
						v-for="segment in ring.segments"
						:key="segment.model"
						class="ring-segment"
						:class="{ 'is-active': hoveredModel === segment.model }"
						cx="50"
						cy="50"
						:r="ring.radius"
						:stroke-dasharray="segment.dashArray"
						:stroke-dashoffset="segment.dashOffset"
						:style="{ stroke: segment.color }"
						@mouseenter="onEnter($event, segment)"
						@mousemove="onMove"
						@mouseleave="onLeave"
					/>
				</svg>
				<div class="ring-center">
					<strong class="ring-total usage-value">
						{{ hoveredItem ? formatCompactTokens(hoveredItem.totalTokens) : totalText }}
					</strong>
					<span class="ring-unit usage-label">
						{{ hoveredItem ? formatPercent(hoveredItem.share) : 'TOKENS' }}
					</span>
				</div>
			</div>

			<ul class="ranking-list" :class="{ 'has-hover': Boolean(hoveredModel) }">
				<li
					v-for="row in rows"
					:key="row.model"
					class="ranking-row"
					:class="{ 'is-active': hoveredModel === row.model }"
					@mouseenter="onEnter($event, row)"
					@mousemove="onMove"
					@mouseleave="onLeave"
				>
					<div class="row-head">
						<span class="row-rank">{{ row.rank }}</span>
						<span class="row-swatch" :style="{ background: row.color }" />
						<span class="row-name" :title="row.label">{{ row.label }}</span>
						<span class="row-tokens usage-value">{{ formatCompactTokens(row.totalTokens) }}</span>
						<span class="row-share">{{ formatPercent(row.share) }}</span>
					</div>
					<div class="row-bar">
						<span :style="{ width: row.barWidth, background: row.color }" />
					</div>
				</li>
			</ul>
		</div>

		<p v-else class="ranking-empty">范围内还没有模型用量</p>

		<UsageTooltip v-if="tooltip" :x="tooltip.x" :y="tooltip.y" :title="tooltip.title" :rows="tooltip.rows" />
	</section>
</template>

<style scoped>
@import '../../../styles/usage-dashboard.css';

.ranking-card {
	display: flex;
	min-width: 0;
	flex-direction: column;
}

.ranking-body {
	display: grid;
	grid-template-columns: minmax(0, 132px) minmax(0, 1fr);
	align-items: center;
	gap: 16px;
	min-width: 0;
}

.ranking-ring {
	position: relative;
	aspect-ratio: 1;
	width: 100%;
	max-width: 132px;
}

.ranking-ring svg {
	display: block;
	width: 100%;
	height: 100%;
	overflow: visible;
	transform: rotate(-90deg);
}

.ring-segment {
	fill: none;
	stroke-width: 15;
	cursor: pointer;
	transition:
		stroke-dasharray 0.3s var(--sc-ease-out),
		stroke-width 0.18s var(--sc-ease-out),
		opacity 0.18s var(--sc-ease-out);
}

.ranking-ring.has-hover .ring-segment:not(.is-active) {
	opacity: 0.38;
}

.ring-segment.is-active {
	stroke-width: 18;
	opacity: 1;
}

.ring-center {
	position: absolute;
	inset: 0;
	display: flex;
	flex-direction: column;
	align-items: center;
	justify-content: center;
	gap: 2px;
	pointer-events: none;
}

.ring-total {
	color: var(--sc-text);
	font-size: var(--fs-15);
	font-weight: 650;
	letter-spacing: -0.01em;
	transition: color 0.16s ease;
}

.ring-unit {
	font-size: var(--fs-9);
	letter-spacing: 0.08em;
	transition: color 0.16s ease;
}

.ranking-list {
	display: flex;
	min-width: 0;
	flex-direction: column;
	gap: 6px;
	margin: 0;
	padding: 0;
	list-style: none;
}

.ranking-row {
	display: flex;
	flex-direction: column;
	gap: 4px;
	min-width: 0;
	padding: 3px 6px;
	margin: 0 -6px;
	border-radius: 6px;
	cursor: pointer;
	transition:
		background 0.14s var(--sc-ease-out),
		opacity 0.14s var(--sc-ease-out);
}

.ranking-list.has-hover .ranking-row:not(.is-active) {
	opacity: 0.45;
}

.ranking-row.is-active {
	background: var(--sc-hover);
	opacity: 1;
}

.row-head {
	display: flex;
	align-items: center;
	gap: 7px;
	min-width: 0;
}

.row-rank {
	flex: none;
	color: var(--sc-faint);
	font-family: var(--sc-mono);
	font-size: var(--fs-9);
	font-variant-numeric: tabular-nums;
}

.row-swatch {
	flex: none;
	width: 7px;
	height: 7px;
	border-radius: 2px;
}

.row-name {
	flex: 1;
	min-width: 0;
	overflow: hidden;
	color: var(--sc-text);
	font-size: var(--fs-11);
	text-overflow: ellipsis;
	white-space: nowrap;
}

.row-tokens {
	flex: none;
	color: var(--sc-soft);
	font-size: var(--fs-10);
}

.row-share {
	flex: none;
	min-width: 34px;
	color: var(--sc-mute);
	font-family: var(--sc-mono);
	font-size: var(--fs-9);
	font-variant-numeric: tabular-nums;
	text-align: right;
}

.row-bar {
	height: 3px;
	border-radius: 2px;
	background: var(--sc-raise);
	overflow: hidden;
}

.row-bar span {
	display: block;
	height: 100%;
	border-radius: 2px;
}

.ranking-empty {
	margin: 0;
	padding: 24px 0;
	color: var(--sc-faint);
	font-size: var(--fs-11);
	text-align: center;
}

@media (max-width: 520px) {
	.ranking-body {
		grid-template-columns: minmax(0, 1fr);
		justify-items: center;
	}
}
</style>
