<script setup>
import { Loader, Trash2 } from 'lucide-vue-next';
import { formatDuration, formatLocalDateTime, runStatusLabel, runTriggerLabel } from '../../renderers/automationEvents';

defineProps({
	runs: { type: Array, default: () => [] },
	timeZone: { type: String, default: '' },
	deleting: { type: Object, default: () => ({}) },
});

const emit = defineEmits(['open-run', 'delete-run']);

function runStartedAt(run) {
	return run.startedAtUtc || run.scheduledForUtc || run.createdAtUtc;
}

function isTerminal(run) {
	return run.status !== 'queued' && run.status !== 'running';
}
</script>

<template>
	<p v-if="!runs.length" class="automation-empty">暂无运行记录</p>
	<div v-for="run in runs" :key="run.id" class="automation-run" :class="`is-${run.status}`">
		<button type="button" class="automation-run-open" @click="emit('open-run', run)">
			<span class="automation-run-dot" aria-hidden="true"></span>
			<span class="automation-run-main">
				<span class="automation-run-title">{{ run.taskTitle || '自动化任务' }}</span>
				<span class="automation-run-meta">
					<span class="automation-run-badge">{{ runStatusLabel(run.status) }}</span>
					<span v-if="run.trigger">{{ runTriggerLabel(run.trigger) }}</span>
					<span>{{ formatLocalDateTime(runStartedAt(run), timeZone) }}</span>
					<span v-if="run.durationMs">{{ formatDuration(run.durationMs) }}</span>
				</span>
				<span v-if="run.errorMessage" class="automation-run-error">{{ run.errorMessage }}</span>
			</span>
		</button>
		<button v-if="isTerminal(run)" type="button" class="automation-run-delete"
			:disabled="Boolean(deleting[run.id])" :title="deleting[run.id] ? '删除中…' : '删除该运行记录'"
			@click="emit('delete-run', run)">
			<Loader v-if="deleting[run.id]" class="is-spinning" aria-hidden="true" />
			<Trash2 v-else aria-hidden="true" />
		</button>
	</div>
</template>

<style scoped>
.automation-empty {
	margin: 28px 6px;
	color: var(--faint);
	font-size: var(--fs-12);
	text-align: center;
}

.automation-run {
	display: flex;
	align-items: flex-start;
	width: 100%;
	gap: 4px;
	padding: 5px;
	border: 1px solid var(--line-1);
	border-radius: 9px;
	background: var(--panel-soft);
	box-shadow: 0 1px 3px rgba(var(--shadow-ink), 0.02);
	transition: border-color 0.16s, background 0.16s, box-shadow 0.16s;
}

.automation-run:hover {
	border-color: var(--line-2);
	background: var(--panel);
	box-shadow: 0 2px 8px rgba(var(--shadow-ink), 0.05);
}

.automation-run-open {
	display: flex;
	align-items: flex-start;
	min-width: 0;
	flex: 1 1 auto;
	gap: 8px;
	padding: 4px 6px;
	border: 0;
	border-radius: 6px;
	background: transparent;
	text-align: left;
	cursor: pointer;
}

.automation-run-dot {
	width: 7px;
	height: 7px;
	flex: none;
	margin-top: 6px;
	border-radius: 50%;
	background: var(--muted);
}

.automation-run.is-completed .automation-run-dot {
	background: var(--success);
	box-shadow: 0 0 6px color-mix(in srgb, var(--success) 45%, transparent);
}

.automation-run.is-failed .automation-run-dot,
.automation-run.is-interrupted .automation-run-dot,
.automation-run.is-cancelled .automation-run-dot {
	background: var(--danger);
	box-shadow: 0 0 6px color-mix(in srgb, var(--danger) 45%, transparent);
}

.automation-run.is-running .automation-run-dot {
	background: var(--accent);
	box-shadow: 0 0 8px var(--accent);
	animation: ac-pulse 1.6s ease-out infinite;
}

.automation-run-main {
	display: flex;
	min-width: 0;
	flex: 1 1 auto;
	flex-direction: column;
	gap: 3px;
}

.automation-run-title {
	color: var(--text);
	font-size: var(--fs-13);
	font-weight: 600;
	letter-spacing: -0.01em;
}

.automation-run-meta {
	display: flex;
	flex-wrap: wrap;
	gap: 4px 8px;
	color: var(--muted);
	font-family: var(--font-code, monospace);
	font-size: 10.5px;
	font-variant-numeric: tabular-nums;
	align-items: center;
}

.automation-run-badge {
	font-family: inherit;
	font-weight: 600;
}

.automation-run-error {
	color: var(--danger);
	font-size: var(--fs-11);
	line-height: 1.5;
}

.automation-run-delete {
	display: inline-flex;
	align-items: center;
	justify-content: center;
	width: 26px;
	height: 26px;
	flex: none;
	border: 1px solid transparent;
	border-radius: 7px;
	background: transparent;
	color: var(--muted);
	cursor: pointer;
	transition: background 0.15s, border-color 0.15s, color 0.15s, transform 0.15s var(--ease-spring);
}

.automation-run-delete:hover:not(:disabled) {
	border-color: color-mix(in srgb, var(--danger) 32%, transparent);
	color: var(--danger);
	background: color-mix(in srgb, var(--danger) 8%, transparent);
}

.automation-run-delete:active:not(:disabled) {
	transform: scale(0.92);
}

.automation-run-delete:disabled {
	opacity: 0.45;
	cursor: not-allowed;
}

.automation-run-delete svg {
	width: 13px;
	height: 13px;
}

@keyframes ac-pulse {
	0% {
		box-shadow: 0 0 0 0 color-mix(in srgb, var(--accent) 34%, transparent);
	}

	70% {
		box-shadow: 0 0 0 6px transparent;
	}

	100% {
		box-shadow: 0 0 0 0 transparent;
	}
}

@media (prefers-reduced-motion: reduce) {
	.automation-run.is-running .automation-run-dot {
		animation: none;
	}
}
</style>
