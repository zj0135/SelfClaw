<script setup>
import { computed } from 'vue';
import { formatCompactTokens, formatDayLabel, formatDuration, unknownText } from '../../../renderers/usageFormat.js';

const props = defineProps({
	summary: { type: Object, default: null },
});

const items = computed(() => {
	const summary = props.summary;
	if (!summary) return [];
	return [
		{ key: 'total', label: '累计 TOKEN', value: formatCompactTokens(summary.totalTokens), hint: '含子代理与自动化' },
		{
			key: 'peak',
			label: '峰值日 TOKEN',
			value: formatCompactTokens(summary.peakDayTokens),
			hint: summary.peakDayDate ? formatDayLabel(summary.peakDayDate) : unknownText,
		},
		{
			key: 'longest',
			label: '最长单回合',
			value: formatDuration(summary.longestTurnMs),
			hint: '单个回合耗时',
		},
		{ key: 'current', label: '当前连续', value: formatDays(summary.currentStreakDays), hint: '截至今天的连续天数' },
		{ key: 'longest-streak', label: '最长连续', value: formatDays(summary.longestStreakDays), hint: '历史最长连续天数' },
	];
});

function formatDays(days) {
	if (days === null || days === undefined || days === '') return unknownText;
	const value = Number(days);
	// 连续 0 天是「已知的断档」，不是「未上报」——只有缺字段才用 em dash。
	return Number.isFinite(value) && value >= 0 ? `${value} 天` : unknownText;
}
</script>

<template>
	<section class="summary-card usage-card sc-rise" aria-label="终身用量摘要">
		<div v-for="item in items" :key="item.key" class="summary-item">
			<span class="usage-label">{{ item.label }}</span>
			<strong class="summary-value usage-value">{{ item.value }}</strong>
			<span class="summary-hint">{{ item.hint }}</span>
		</div>
	</section>
</template>

<style scoped>
@import './usage-dashboard.css';

.summary-card {
	display: grid;
	grid-template-columns: repeat(5, minmax(0, 1fr));
	gap: 0;
	padding: 0;
	overflow: hidden;
}

.summary-item {
	display: flex;
	flex-direction: column;
	gap: 4px;
	min-width: 0;
	padding: 14px 16px;
	border-left: 1px solid var(--sc-line);
}

.summary-item:first-child {
	border-left: 0;
}

.summary-value {
	font-size: var(--fs-19);
	font-weight: 650;
	letter-spacing: -0.01em;
	line-height: 1.15;
	white-space: nowrap;
}

.summary-hint {
	overflow: hidden;
	color: var(--sc-faint);
	font-family: var(--sc-mono);
	font-size: var(--fs-9);
	letter-spacing: 0.02em;
	text-overflow: ellipsis;
	white-space: nowrap;
}

@media (max-width: 900px) {
	.summary-card {
		grid-template-columns: repeat(3, minmax(0, 1fr));
	}

	.summary-item:nth-child(4) {
		border-left: 0;
	}

	.summary-item:nth-child(n + 4) {
		border-top: 1px solid var(--sc-line);
	}
}

@media (max-width: 640px) {
	.summary-card {
		grid-template-columns: repeat(2, minmax(0, 1fr));
	}

	.summary-item:nth-child(odd) {
		border-left: 0;
	}

	.summary-item:nth-child(n + 3) {
		border-top: 1px solid var(--sc-line);
	}

	.summary-item:nth-child(even) {
		border-left: 1px solid var(--sc-line);
	}
}
</style>
