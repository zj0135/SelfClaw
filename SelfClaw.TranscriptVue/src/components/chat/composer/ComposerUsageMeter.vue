<script setup>
import { computed, nextTick, onBeforeUnmount, onMounted, ref } from 'vue';
import { calculateUsageProgress, ringTick } from '../../../renderers/usageMeter.js';
import { useCompressionThreshold } from '../../../composables/useCompressionThreshold.js';
import ComposerUsagePopover from './ComposerUsagePopover.vue';

const props = defineProps({
	usage: {
		type: Object,
		default: null,
	},
});

const { absoluteThreshold, enabled: compactionEnabled } = useCompressionThreshold();

const metrics = computed(() =>
	calculateUsageProgress(props.usage, absoluteThreshold.value, compactionEnabled.value),
);

const RADIUS = 7.5;
const CIRCUMFERENCE = 2 * Math.PI * RADIUS;
// 刻度两端各探出环身一点，在 18px 的环上才看得出是一道刻度而不是断口。
const TICK = { center: 10, inner: 5.3, outer: 9.7 };

const circumference = CIRCUMFERENCE;
const dashOffset = computed(() => {
	if (!metrics.value) return CIRCUMFERENCE;
	return CIRCUMFERENCE * (1 - metrics.value.ratio);
});
const triggerTick = computed(() => ringTick(metrics.value?.triggerRatio, TICK));

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
	const reading = `Token 用量：${metrics.value.percentageText} (${metrics.value.contextText} / ${metrics.value.limitText})`;
	return metrics.value.triggerText ? `${reading} · 自动压缩 ${metrics.value.triggerText}` : reading;
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
				<line v-if="triggerTick" class="usage-ring-trigger" v-bind="triggerTick" stroke-width="1.4" stroke-linecap="round" />
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
			<ComposerUsagePopover :metrics="metrics" />
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

/* 压缩触发点：进度弧长到这道刻度就会自动压缩。 */
.usage-ring-trigger {
	stroke: var(--text-strong);
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
</style>
