<script setup>
import { computed, nextTick, onBeforeUnmount, onMounted, ref } from 'vue';
import { calculateUsageProgress } from '../../renderers/usageMeter.js';
import { useCompressionThreshold } from '../../composables/useCompressionThreshold.js';

const props = defineProps({
	usage: {
		type: Object,
		default: null,
	},
});

const { absoluteThreshold } = useCompressionThreshold();

const metrics = computed(() =>
	calculateUsageProgress(props.usage, absoluteThreshold.value),
);

const RADIUS = 7.5;
const CIRCUMFERENCE = 2 * Math.PI * RADIUS;

const circumference = CIRCUMFERENCE;
const dashOffset = computed(() => {
	if (!metrics.value) return CIRCUMFERENCE;
	return CIRCUMFERENCE * (1 - metrics.value.ratio);
});

const open = ref(false);
const rootRef = ref(null);
const popoverRef = ref(null);
const placement = ref('up');
const estimatedPopoverHeight = 220;

let enterTimer = null;
let leaveTimer = null;

function updatePlacement() {
	const rect = rootRef.value?.getBoundingClientRect();
	if (!rect) return;

	const popoverHeight = popoverRef.value?.offsetHeight || estimatedPopoverHeight;
	const spaceBelow = window.innerHeight - rect.bottom;
	const spaceAbove = rect.top;
	placement.value = spaceAbove < popoverHeight + 16 && spaceBelow > spaceAbove ? 'down' : 'up';
}

function openPanel() {
	if (leaveTimer) {
		clearTimeout(leaveTimer);
		leaveTimer = null;
	}
	open.value = true;
	updatePlacement();
	nextTick(updatePlacement);
}

function closeWithDelay() {
	if (enterTimer) {
		clearTimeout(enterTimer);
		enterTimer = null;
	}
	if (!leaveTimer) {
		leaveTimer = setTimeout(() => {
			open.value = false;
			leaveTimer = null;
		}, 180);
	}
}

function onMouseEnter() {
	if (leaveTimer) {
		clearTimeout(leaveTimer);
		leaveTimer = null;
	}
	enterTimer = setTimeout(openPanel, 40);
}

function onMouseLeave() {
	if (enterTimer) {
		clearTimeout(enterTimer);
		enterTimer = null;
	}
	closeWithDelay();
}

function onPopoverEnter() {
	if (leaveTimer) {
		clearTimeout(leaveTimer);
		leaveTimer = null;
	}
}

function onPopoverLeave() {
	closeWithDelay();
}

function togglePanel() {
	if (open.value) {
		open.value = false;
	} else {
		openPanel();
	}
}

function onDocClick(e) {
	if (rootRef.value && !rootRef.value.contains(e.target)) {
		open.value = false;
	}
}

function onKeydown(e) {
	if (e.key === 'Escape') {
		open.value = false;
	}
}

onMounted(() => {
	document.addEventListener('click', onDocClick);
	document.addEventListener('keydown', onKeydown);
});

onBeforeUnmount(() => {
	if (enterTimer) clearTimeout(enterTimer);
	if (leaveTimer) clearTimeout(leaveTimer);
	document.removeEventListener('click', onDocClick);
	document.removeEventListener('keydown', onKeydown);
});

const buttonTitle = computed(() => {
	if (!metrics.value) return 'Token 用量';
	return `Token 用量：${metrics.value.percentageText} (${metrics.value.contextText} / ${metrics.value.limitText})`;
});
</script>

<template>
	<div v-if="metrics" ref="rootRef" class="usage-meter-wrap" @mouseenter="onMouseEnter" @mouseleave="onMouseLeave">
		<button
			class="usage-pill"
			type="button"
			:aria-expanded="open ? 'true' : 'false'"
			aria-haspopup="true"
			:title="buttonTitle"
			@click.stop="togglePanel"
			@focus="openPanel"
			@blur="closeWithDelay"
		>
			<svg class="usage-ring" viewBox="0 0 20 20" width="18" height="18" aria-hidden="true">
				<circle class="usage-ring-track" cx="10" cy="10" :r="RADIUS" fill="none" stroke-width="2.2" />
				<circle
					class="usage-ring-fill"
					:class="`tone--${metrics.tone}`"
					cx="10"
					cy="10"
					:r="RADIUS"
					fill="none"
					stroke-width="2.2"
					stroke-linecap="round"
					:stroke-dasharray="circumference"
					:stroke-dashoffset="dashOffset"
				/>
			</svg>
			<span class="usage-pill-text" :class="`tone--${metrics.tone}`">
				{{ metrics.percentageText }}
			</span>
		</button>

		<div
			v-show="open"
			ref="popoverRef"
			class="usage-popover"
			:class="`usage-popover--${placement}`"
			role="tooltip"
			aria-label="Token 用量详情"
			@mouseenter="onPopoverEnter"
			@mouseleave="onPopoverLeave"
		>
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
				</div>
				<div class="progress-bar-track" aria-hidden="true">
					<div
						class="progress-bar-fill"
						:class="`tone--${metrics.tone}`"
						:style="{ width: `${Math.min(100, Math.max(metrics.percentage > 0 ? 3 : 0, metrics.percentage))}%` }"
					></div>
				</div>
				<div class="context-caption">
					{{ metrics.isAbsoluteThreshold ? '未声明窗口 · 按配置绝对阈值计算' : '按模型声明的上下文窗口计算' }}
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
		</div>
	</div>
</template>

<style scoped>
.usage-meter-wrap {
	position: relative;
	display: inline-flex;
	align-items: center;
}

/* ===== 触发按钮：与 composer-model / composer-agent 同一语言的高级微胶囊 ===== */
.usage-pill {
	display: inline-flex;
	align-items: center;
	gap: 5px;
	height: 28px;
	padding: 0 7px 0 6px;
	border: 0;
	border-radius: 8px;
	background: transparent;
	cursor: pointer;
	transition: background 0.16s ease, color 0.16s ease;
	outline: none;
	user-select: none;
}

.usage-pill:hover,
.usage-pill[aria-expanded='true'],
.usage-pill:focus-visible {
	background: var(--panel-muted);
}

.usage-ring {
	display: block;
	flex: none;
	transform: rotate(-90deg);
}

.usage-ring-track {
	stroke: color-mix(in srgb, var(--text) 10%, transparent);
}

.usage-ring-fill {
	transition: stroke-dashoffset 0.45s cubic-bezier(0.16, 1, 0.3, 1), stroke 0.25s ease;
}

/* 色调体系：normal 中性优雅微蓝/微青，warm 琥珀，critical 警示红 */
.usage-ring-fill.tone--normal {
	stroke: var(--accent);
}

.usage-ring-fill.tone--warm {
	stroke: #f59e0b;
}

.usage-ring-fill.tone--critical {
	stroke: #ef4444;
}

.usage-pill-text {
	font-family: var(--font-ui);
	font-size: 11px;
	font-weight: 600;
	font-variant-numeric: tabular-nums;
	color: var(--muted);
	letter-spacing: -0.01em;
	transition: color 0.16s ease;
}

.usage-pill:hover .usage-pill-text,
.usage-pill[aria-expanded='true'] .usage-pill-text {
	color: var(--text);
}

.usage-pill-text.tone--warm {
	color: #d97706;
}

.usage-pill-text.tone--critical {
	color: #ef4444;
}

/* ===== 悬停展开面板：对标 Awwwards 精致磨砂浮层 ===== */
.usage-popover {
	position: absolute;
	right: 0;
	width: 252px;
	padding: 12px 14px;
	border: 1px solid var(--border);
	border-radius: 12px;
	background: var(--panel);
	box-shadow:
		0 2px 4px rgba(var(--shadow-ink), 0.04),
		0 12px 32px rgba(var(--shadow-ink), 0.12);
	z-index: 50;
	pointer-events: auto;
}

.usage-popover--up {
	bottom: calc(100% + 7px);
	transform-origin: bottom right;
	animation: popover-in-up 0.18s cubic-bezier(0.16, 1, 0.3, 1);
}

.usage-popover--down {
	top: calc(100% + 7px);
	transform-origin: top right;
	animation: popover-in-down 0.18s cubic-bezier(0.16, 1, 0.3, 1);
}

@keyframes popover-in-up {
	from {
		opacity: 0;
		transform: translateY(6px) scale(0.97);
	}
	to {
		opacity: 1;
		transform: translateY(0) scale(1);
	}
}

@keyframes popover-in-down {
	from {
		opacity: 0;
		transform: translateY(-6px) scale(0.97);
	}
	to {
		opacity: 1;
		transform: translateY(0) scale(1);
	}
}

@media (prefers-reduced-motion: reduce) {
	.usage-popover--up,
	.usage-popover--down {
		animation: none;
	}
}

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

/* 线性进度条 */
.progress-bar-track {
	width: 100%;
	height: 4px;
	border-radius: 99px;
	background: color-mix(in srgb, var(--text) 8%, transparent);
	overflow: hidden;
	margin-bottom: 6px;
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

.context-caption {
	font-size: 10px;
	color: var(--muted-soft);
	line-height: 1.3;
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
