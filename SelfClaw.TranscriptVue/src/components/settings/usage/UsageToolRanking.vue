<script setup>
import { computed } from 'vue';
import { formatCount, formatDuration, formatPercent } from '../../../renderers/usageFormat.js';

const props = defineProps({
	tools: { type: Array, default: () => [] },
});

const maxCalls = computed(() => props.tools.reduce(
	(peak, tool) => Math.max(peak, Number(tool?.callCount) || 0),
	0,
));

const rows = computed(() => props.tools.map((tool, index) => {
	const calls = Number(tool?.callCount) || 0;
	return {
		key: tool?.toolName || `tool-${index}`,
		rank: String(index + 1).padStart(2, '0'),
		name: tool?.displayName || tool?.toolName || '—',
		toolName: tool?.toolName || '',
		calls,
		barWidth: `${maxCalls.value > 0 ? Math.round((calls / maxCalls.value) * 100) : 0}%`,
		errorText: formatPercent(tool?.errorRate),
		hasError: (Number(tool?.errorCount) || 0) > 0,
		durationText: formatDuration(tool?.avgDurationMs),
	};
}));

const hasTools = computed(() => rows.value.length > 0);
</script>

<template>
	<section class="usage-card tools-card">
		<header class="usage-card-head">
			<h3 class="usage-card-title">工具调用排行</h3>
			<span class="usage-label">前 20 项</span>
		</header>

		<div v-if="hasTools" class="usage-scroll">
			<table class="tools-table">
				<thead>
					<tr>
						<th class="col-rank" scope="col">#</th>
						<th class="col-name" scope="col">工具</th>
						<th class="col-bar" scope="col">调用占比</th>
						<th class="col-num" scope="col">调用</th>
						<th class="col-num" scope="col">失败率</th>
						<th class="col-num" scope="col">平均耗时</th>
					</tr>
				</thead>
				<tbody>
					<tr v-for="row in rows" :key="row.key">
						<td class="col-rank">{{ row.rank }}</td>
						<td class="col-name">
							<span :title="row.toolName || row.name">{{ row.name }}</span>
						</td>
						<td class="col-bar">
							<span class="tools-bar">
								<span :style="{ width: row.barWidth }" />
							</span>
						</td>
						<td class="col-num usage-value">{{ formatCount(row.calls) }}</td>
						<td class="col-num" :class="{ 'is-error': row.hasError }">{{ row.errorText }}</td>
						<td class="col-num">{{ row.durationText }}</td>
					</tr>
				</tbody>
			</table>
		</div>

		<p v-else class="tools-empty">所选范围内还没有工具调用</p>
	</section>
</template>

<style scoped>
@import '../../../styles/usage-dashboard.css';

.tools-card {
	display: flex;
	min-width: 0;
	flex-direction: column;
}

.usage-label {
	white-space: nowrap;
}

.tools-table {
	width: 100%;
	border-collapse: separate;
	border-spacing: 0;
	table-layout: fixed;
}

.tools-table thead tr {
	background: var(--sc-raise);
}

.tools-table th {
	padding: 8px 12px;
	border-bottom: 1px solid var(--sc-line);
	color: var(--sc-mute);
	font-family: var(--sc-mono);
	font-size: var(--fs-10);
	font-weight: 600;
	letter-spacing: 0.04em;
	text-align: left;
	white-space: nowrap;
}

.tools-table th:first-child {
	border-top-left-radius: 6px;
	border-bottom-left-radius: 6px;
	padding-left: 14px;
}

.tools-table th:last-child {
	border-top-right-radius: 6px;
	border-bottom-right-radius: 6px;
	padding-right: 14px;
}

.tools-table td {
	padding: 9px 12px;
	border-bottom: 1px solid var(--sc-line);
	font-size: var(--fs-11);
	vertical-align: middle;
}

.tools-table td:first-child {
	padding-left: 14px;
}

.tools-table td:last-child {
	padding-right: 14px;
}

.tools-table tbody tr {
	transition: background 0.14s ease;
}

.tools-table tbody tr:hover {
	background: var(--sc-hover);
}

.tools-table tbody tr:last-child td {
	border-bottom: 0;
}

.col-rank {
	width: 44px;
	color: var(--sc-faint);
	font-family: var(--sc-mono);
	font-size: var(--fs-10);
	font-variant-numeric: tabular-nums;
}

.col-name {
	min-width: 0;
	overflow: hidden;
	color: var(--sc-text);
	text-overflow: ellipsis;
	white-space: nowrap;
}

.col-bar {
	width: 32%;
}

.col-num {
	width: 86px;
	color: var(--sc-soft);
	font-family: var(--sc-mono);
	font-size: var(--fs-10);
	font-variant-numeric: tabular-nums;
	text-align: right;
	white-space: nowrap;
}

.col-num.is-error {
	color: var(--sc-err);
}

.tools-bar {
	display: block;
	height: 5px;
	border-radius: 3px;
	background: var(--sc-raise);
	overflow: hidden;
}

.tools-bar span {
	display: block;
	height: 100%;
	border-radius: 3px;
	background: var(--sc-chart-1);
	transition: width 0.3s var(--sc-ease-out);
}

.tools-empty {
	margin: 0;
	padding: 24px 0;
	color: var(--sc-faint);
	font-size: var(--fs-11);
	text-align: center;
}

@media (max-width: 720px) {
	.col-bar {
		display: none;
	}

	.col-num {
		width: 68px;
	}
}
</style>
