<script setup>
import { computed } from 'vue';
import { X } from 'lucide-vue-next';
import TranscriptPanel from '../Chat/TranscriptPanel.vue';
import { useTranscriptCollapse } from '../../composables/useTranscriptCollapse';
import { errorMessage, formatDuration, formatLocalDateTime, runStatusLabel, runTriggerLabel } from '../../renderers/automationEvents';

const props = defineProps({
	record: { type: Object, default: null },
	loading: { type: Boolean, default: false },
	timeZone: { type: String, default: '' },
});

const emit = defineEmits(['close', 'preview-image']);

const collapse = useTranscriptCollapse();

const run = computed(() => props.record?.run || null);
const task = computed(() => props.record?.task || null);
const items = computed(() => props.record?.transcript?.items || []);
const usage = computed(() => props.record?.usage || null);
const statusLabel = computed(() => (run.value ? runStatusLabel(run.value.status) : ''));
const failureText = computed(() => {
	if (!run.value) return '';
	return run.value.errorMessage || (run.value.errorCode ? errorMessage(run.value.errorCode) : '');
});
</script>

<template>
	<div class="automation-drawer-backdrop" @click.self="emit('close')">
		<aside class="automation-drawer" role="dialog" aria-label="运行详情">
			<header class="automation-drawer-head">
				<div class="automation-drawer-title">
					<span class="automation-drawer-kicker">运行详情</span>
					<h2>{{ task?.title || '自动化任务' }}</h2>
				</div>
				<button type="button" class="automation-drawer-close" title="关闭" @click="emit('close')">
					<X aria-hidden="true" />
				</button>
			</header>

			<div v-if="loading" class="automation-drawer-loading">正在加载运行记录…</div>
			<template v-else-if="run">
				<div class="automation-drawer-meta">
					<span class="automation-drawer-status" :class="`is-${run.status}`">{{ statusLabel }}</span>
					<span v-if="run.trigger">{{ runTriggerLabel(run.trigger) }}</span>
					<span>开始：{{ formatLocalDateTime(run.startedAtUtc || run.scheduledForUtc || run.createdAtUtc, timeZone) }}</span>
					<span v-if="run.completedAtUtc">结束：{{ formatLocalDateTime(run.completedAtUtc, timeZone) }}</span>
					<span v-if="run.durationMs">耗时：{{ formatDuration(run.durationMs) }}</span>
					<span v-if="run.attempt > 1">第 {{ run.attempt }} 次</span>
					<span v-if="usage?.totalTokens">Tokens：{{ usage.totalTokens }}</span>
				</div>
				<p v-if="failureText" class="automation-drawer-error">{{ failureText }}</p>
				<div class="automation-drawer-transcript">
					<TranscriptPanel v-if="items.length" :items="items" :collapse="collapse"
						@preview-image="emit('preview-image', $event)" />
					<p v-else class="automation-drawer-empty">该运行未生成转录</p>
				</div>
			</template>
		</aside>
	</div>
</template>

<style scoped>
.automation-drawer-backdrop {
	position: absolute;
	inset: 0;
	z-index: 300;
	display: flex;
	justify-content: flex-end;
	background: var(--overlay, rgba(0, 0, 0, 0.45));
	backdrop-filter: blur(8px);
	-webkit-backdrop-filter: blur(8px);
	animation: ac-drawer-fade 0.18s ease-out;
}

.automation-drawer {
	display: flex;
	width: min(640px, 62%);
	min-width: 0;
	flex-direction: column;
	overflow: hidden;
	border-left: 1px solid var(--line-1);
	background: var(--panel, #ffffff);
	color: var(--text);
	box-shadow: -20px 0 60px rgba(var(--shadow-ink), 0.25);
	animation: ac-drawer-enter 0.24s cubic-bezier(0.16, 1, 0.3, 1);
}

.automation-drawer-head {
	display: flex;
	align-items: flex-start;
	justify-content: space-between;
	gap: 12px;
	padding: 16px 18px 14px;
	border-bottom: 1px solid var(--line-1);
}

.automation-drawer-kicker {
	color: var(--muted);
	font-family: var(--font-code, monospace);
	font-size: var(--fs-10);
	font-weight: 650;
	letter-spacing: 0.18em;
	text-transform: uppercase;
}

.automation-drawer-title h2 {
	margin: 4px 0 0;
	color: var(--text);
	font-family: var(--font-display);
	font-size: var(--fs-18);
	font-weight: 650;
	letter-spacing: -0.015em;
	line-height: 1.25;
}

.automation-drawer-close {
	display: inline-flex;
	align-items: center;
	justify-content: center;
	width: 28px;
	height: 28px;
	flex: none;
	border: 1px solid var(--line-1);
	border-radius: 8px;
	background: var(--panel-soft);
	color: var(--muted);
	cursor: pointer;
	transition: background 0.15s, border-color 0.15s, color 0.15s, transform 0.15s var(--ease-spring);
}

.automation-drawer-close:hover {
	background: var(--panel-hover);
	color: var(--text);
	border-color: var(--line-2);
}

.automation-drawer-close:active {
	transform: scale(0.92);
}

.automation-drawer-close svg {
	width: 14px;
	height: 14px;
}

.automation-drawer-meta {
	display: flex;
	flex-wrap: wrap;
	gap: 6px 10px;
	padding: 10px 18px;
	color: var(--muted);
	font-family: var(--font-code, monospace);
	font-size: 11px;
	font-variant-numeric: tabular-nums;
	border-bottom: 1px solid var(--line-1);
	background: var(--panel-soft);
}

.automation-drawer-status {
	padding: 1px 7px;
	border-radius: 4px;
	background: var(--panel-muted);
	font-weight: 600;
}

.automation-drawer-status.is-completed {
	background: color-mix(in srgb, var(--success) 16%, transparent);
	color: var(--success);
}

.automation-drawer-status.is-failed,
.automation-drawer-status.is-interrupted,
.automation-drawer-status.is-cancelled {
	background: color-mix(in srgb, var(--danger) 16%, transparent);
	color: var(--danger);
}

.automation-drawer-status.is-running {
	background: color-mix(in srgb, var(--accent) 18%, transparent);
	color: var(--accent-2);
}

.automation-drawer-error {
	margin: 10px 18px 4px;
	padding: 8px 10px;
	border: 1px solid color-mix(in srgb, var(--danger) 30%, transparent);
	border-radius: 8px;
	background: color-mix(in srgb, var(--danger) 8%, transparent);
	color: var(--danger);
	font-size: var(--fs-12);
	line-height: 1.5;
}

.automation-drawer-transcript {
	display: flex;
	min-height: 0;
	flex: 1 1 auto;
	flex-direction: column;
	overflow: hidden;
}

.automation-drawer-transcript :deep(.transcript-scroll) {
	padding: 8px 18px 24px;
	-webkit-mask-image: none;
	mask-image: none;
}

.automation-drawer-loading,
.automation-drawer-empty {
	padding: 24px 18px;
	color: var(--faint);
	font-size: var(--fs-12);
	text-align: center;
}

@keyframes ac-drawer-fade {
	from { opacity: 0; }
	to { opacity: 1; }
}

@keyframes ac-drawer-enter {
	from {
		transform: translateX(100%);
	}
	to {
		transform: translateX(0);
	}
}
</style>
