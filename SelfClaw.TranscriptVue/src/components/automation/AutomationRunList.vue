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
	<p v-if="!runs.length" class="automation-empty">没有符合条件的运行记录。</p>
	<div v-for="run in runs" :key="run.id" class="automation-run" :class="`is-${run.status}`">
		<button type="button" class="automation-run-open" @click="emit('open-run', run)">
			<span class="automation-run-dot" aria-hidden="true"></span>
			<span class="automation-run-main">
				<span class="automation-run-title">{{ run.taskTitle || '自动化任务' }}</span>
				<span class="automation-run-meta">
					<span>{{ runStatusLabel(run.status) }}</span>
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
	margin: 18px 6px;
	color: var(--muted);
	font-size: var(--fs-12);
	line-height: 1.6;
	text-align: center;
}

.automation-run {
	display: flex;
	align-items: flex-start;
	width: 100%;
	gap: 4px;
	padding: 6px;
	border: 1px solid var(--border);
	border-radius: 11px;
	background: var(--panel-soft);
}

.automation-run:hover {
	border-color: var(--border-strong);
	background: var(--panel);
}

.automation-run-open {
	display: flex;
	align-items: flex-start;
	min-width: 0;
	flex: 1 1 auto;
	gap: 8px;
	padding: 4px 5px;
	border: 0;
	border-radius: 8px;
	background: transparent;
	text-align: left;
}

.automation-run-dot {
	width: 8px;
	height: 8px;
	flex: none;
	margin-top: 5px;
	border-radius: 50%;
	background: var(--muted);
}

.automation-run.is-completed .automation-run-dot {
	background: var(--success);
}

.automation-run.is-failed .automation-run-dot,
.automation-run.is-interrupted .automation-run-dot,
.automation-run.is-cancelled .automation-run-dot {
	background: var(--danger);
}

.automation-run.is-running .automation-run-dot {
	background: var(--accent);
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
	font-weight: 650;
}

.automation-run-meta {
	display: flex;
	flex-wrap: wrap;
	gap: 4px 8px;
	color: var(--muted);
	font-size: var(--fs-11);
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
	border-radius: 8px;
	background: transparent;
	color: var(--muted);
}

.automation-run-delete:hover:not(:disabled) {
	border-color: color-mix(in srgb, var(--danger) 32%, transparent);
	color: var(--danger);
}

.automation-run-delete:disabled {
	opacity: 0.55;
}

.automation-run-delete svg {
	width: 14px;
	height: 14px;
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
