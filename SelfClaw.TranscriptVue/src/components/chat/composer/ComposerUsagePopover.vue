<script setup>
import { computed } from 'vue';

const props = defineProps({
	metrics: {
		type: Object,
		required: true,
	},
});

// 有读数时至少留 3% 的可见宽度，否则 <1% 的上下文在条上什么都看不到。
const fillWidth = computed(() => {
	const percentage = props.metrics.percentage;
	return `${Math.min(100, Math.max(percentage > 0 ? 3 : 0, percentage))}%`;
});

const triggerLeft = computed(() =>
	props.metrics.triggerRatio == null ? null : `${props.metrics.triggerRatio * 100}%`,
);
</script>

<template>
	<!-- 标题栏与状态徽标 -->
	<div class="popover-head">
		<span class="popover-title">Token 用量</span>
		<span class="popover-badge" :class="`badge--${metrics.tone}`">
			<span class="badge-dot" aria-hidden="true"></span>
			{{ metrics.percentageText }}
		</span>
	</div>

	<!-- 当前上下文容量卡片 -->
	<div class="context-card">
		<div class="context-header">
			<span class="context-label">当前上下文</span>
			<span class="context-tag">
				{{ metrics.isAbsoluteThreshold ? '绝对阈值' : '上下文窗口' }}
			</span>
		</div>
		<div class="context-metric">
			<span class="context-current">{{ metrics.contextText }}</span>
			<span class="context-sep">/</span>
			<span class="context-limit">{{ metrics.limitText }}</span>
			<span v-if="metrics.cacheHitText" class="context-cache">{{ metrics.cacheHitText }}</span>
		</div>
		<div class="progress-bar" aria-hidden="true">
			<div class="progress-bar-track">
				<div class="progress-bar-fill" :class="`tone--${metrics.tone}`" :style="{ width: fillWidth }"></div>
			</div>
			<span v-if="triggerLeft" class="progress-bar-trigger" :style="{ left: triggerLeft }"></span>
		</div>
		<div v-if="metrics.triggerText" class="trigger-row">
			<span class="trigger-label">
				<span class="trigger-mark" aria-hidden="true"></span>
				自动压缩
			</span>
			<span class="trigger-value">
				{{ metrics.triggerText }}
				<span class="trigger-share">{{ metrics.triggerShareText }}</span>
			</span>
		</div>
	</div>

	<div class="popover-divider" aria-hidden="true"></div>

	<!-- 会话累计用量 -->
	<div class="totals-section">
		<div class="totals-title">会话累计</div>
		<div class="totals-grid">
			<div class="total-col">
				<span class="total-label">输入</span>
				<span class="total-val">{{ metrics.inputTokensText }}</span>
			</div>
			<div class="total-col">
				<span class="total-label">输出</span>
				<span class="total-val">{{ metrics.outputTokensText }}</span>
			</div>
			<div class="total-col">
				<span class="total-label">总计</span>
				<span class="total-val total-val--sum">{{ metrics.totalTokensText }}</span>
			</div>
		</div>
	</div>

	<div v-if="metrics.contextIsEstimated" class="usage-note">
		* 当前上下文规模由本地估算近似
	</div>
</template>

<style scoped>
/* 顶部栏 */
.popover-head {
	display: flex;
	align-items: center;
	justify-content: space-between;
	margin-bottom: 10px;
}

.popover-title {
	font-size: 11px;
	font-weight: 650;
	letter-spacing: 0.04em;
	text-transform: uppercase;
	color: var(--muted);
}

.popover-badge {
	display: inline-flex;
	align-items: center;
	gap: 5px;
	padding: 2px 7px;
	border-radius: 99px;
	font-size: 11px;
	font-weight: 650;
	font-variant-numeric: tabular-nums;
	line-height: 1.2;
}

.badge-dot {
	width: 5px;
	height: 5px;
	border-radius: 50%;
	background: currentColor;
}

.popover-badge.badge--normal {
	background: color-mix(in srgb, var(--accent) 12%, transparent);
	color: var(--accent);
}

.popover-badge.badge--warm {
	background: color-mix(in srgb, #f59e0b 14%, transparent);
	color: #d97706;
}

.popover-badge.badge--critical {
	background: color-mix(in srgb, #ef4444 14%, transparent);
	color: #ef4444;
}

/* 上下文卡片 */
.context-card {
	padding: 9px 10px;
	border-radius: 9px;
	background: var(--panel-soft);
	border: 1px solid color-mix(in srgb, var(--border) 60%, transparent);
}

.context-header {
	display: flex;
	align-items: center;
	justify-content: space-between;
	margin-bottom: 4px;
}

.context-label {
	font-size: 11.5px;
	font-weight: 550;
	color: var(--text-soft);
}

.context-tag {
	font-size: 10px;
	font-weight: 500;
	color: var(--muted);
	padding: 1px 5px;
	border-radius: 4px;
	background: color-mix(in srgb, var(--border) 60%, transparent);
}

.context-metric {
	display: flex;
	align-items: baseline;
	gap: 4px;
	margin-bottom: 7px;
	font-variant-numeric: tabular-nums;
}

.context-current {
	font-size: 15px;
	font-weight: 700;
	color: var(--text-strong);
	letter-spacing: -0.02em;
}

.context-sep {
	font-size: 11px;
	color: var(--muted-soft);
}

.context-limit {
	font-size: 11.5px;
	font-weight: 500;
	color: var(--muted);
}

/* 命中率只留数字与百分号，靠右贴齐：它是同一行的次级读数，不参与“当前/上限”的对比。 */
.context-cache {
	margin-left: auto;
	font-size: 11.5px;
	font-weight: 600;
	color: var(--muted);
	letter-spacing: -0.01em;
}

/* 线性进度条：刻度要高出轨道，所以裁切只留在轨道上，刻度挂在外层。 */
.progress-bar {
	position: relative;
	margin-bottom: 6px;
}

.progress-bar-track {
	width: 100%;
	height: 4px;
	border-radius: 99px;
	background: color-mix(in srgb, var(--text) 8%, transparent);
	overflow: hidden;
}

.progress-bar-fill {
	height: 100%;
	border-radius: 99px;
	transition: width 0.45s cubic-bezier(0.16, 1, 0.3, 1), background-color 0.25s ease;
}

.progress-bar-fill.tone--normal {
	background: var(--accent);
}

.progress-bar-fill.tone--warm {
	background: #f59e0b;
}

.progress-bar-fill.tone--critical {
	background: #ef4444;
}

/* 压缩触发点：一道压在进度条上的细刻度，外圈描一层卡片底色把它从填充色里切出来。 */
.progress-bar-trigger,
.trigger-mark {
	width: 2px;
	height: 10px;
	border-radius: 1px;
	background: var(--text-strong);
}

.progress-bar-trigger {
	position: absolute;
	top: 50%;
	transform: translate(-50%, -50%);
	box-shadow: 0 0 0 1.5px var(--panel-soft);
	transition: left 0.45s cubic-bezier(0.16, 1, 0.3, 1);
}

.trigger-row {
	display: flex;
	align-items: center;
	justify-content: space-between;
	font-size: 10.5px;
	color: var(--muted);
	font-variant-numeric: tabular-nums;
}

.trigger-label {
	display: inline-flex;
	align-items: center;
	gap: 6px;
}

.trigger-mark {
	flex: none;
	height: 8px;
}

.trigger-value {
	font-weight: 600;
	color: var(--text-soft);
	letter-spacing: -0.01em;
}

.trigger-share {
	margin-left: 4px;
	font-weight: 500;
	color: var(--muted);
}

.popover-divider {
	height: 1px;
	background: color-mix(in srgb, var(--border) 70%, transparent);
	margin: 11px 0 9px;
}

/* 累计用量 */
.totals-section {
	display: flex;
	flex-direction: column;
	gap: 6px;
}

.totals-title {
	font-size: 10.5px;
	font-weight: 600;
	color: var(--muted);
	letter-spacing: 0.02em;
}

.totals-grid {
	display: grid;
	grid-template-columns: repeat(3, 1fr);
	gap: 6px;
}

.total-col {
	display: flex;
	flex-direction: column;
	gap: 2px;
	padding: 5px 6px;
	border-radius: 6px;
	background: color-mix(in srgb, var(--panel-soft) 70%, transparent);
}

.total-label {
	font-size: 10px;
	color: var(--muted);
}

.total-val {
	font-size: 11.5px;
	font-weight: 600;
	color: var(--text);
	font-variant-numeric: tabular-nums;
	letter-spacing: -0.01em;
}

.total-val--sum {
	color: var(--text-strong);
}

.usage-note {
	margin-top: 8px;
	font-size: 9.5px;
	color: var(--muted-soft);
	line-height: 1.3;
}
</style>
