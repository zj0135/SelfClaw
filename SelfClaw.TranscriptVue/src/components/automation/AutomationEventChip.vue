<script setup>
import { computed } from 'vue';
import { CircleCheck, CircleDashed, CirclePlay, SkipForward, CircleAlert, TriangleAlert } from 'lucide-vue-next';
import { formatDuration, runStatusLabel } from '../../renderers/automationEvents';

const props = defineProps({
	event: { type: Object, required: true },
	timeText: { type: String, default: '' },
});

const icon = computed(() => {
	const event = props.event;
	if (event.kind === 'todo') return event.status === 'done' ? CircleCheck : CircleDashed;
	if (event.kind === 'run') {
		if (event.status === 'running') return CirclePlay;
		if (event.status === 'skipped') return SkipForward;
		return event.status === 'completed' ? CircleCheck : CircleAlert;
	}
	return CircleDashed;
});

const badge = computed(() => {
	const event = props.event;
	if (event.kind === 'todo') return event.status === 'done' ? '已完成' : '待办';
	if (event.kind === 'run') return runStatusLabel(event.status);
	return '';
});

const durationText = computed(() => (props.event.kind === 'run' ? formatDuration(props.event.durationMs) : ''));
</script>

<template>
	<span class="ac-chip" :class="[`ac-chip--${event.kind}`, `ac-status-${event.status || 'none'}`, { 'is-overdue': event.isOverdue }]">
		<component :is="icon" class="ac-chip-icon" aria-hidden="true" />
		<span v-if="timeText" class="ac-chip-time">{{ timeText }}</span>
		<span class="ac-chip-title">{{ event.title }}</span>
		<span v-if="badge" class="ac-chip-badge">{{ badge }}</span>
		<span v-if="durationText" class="ac-chip-duration">{{ durationText }}</span>
		<TriangleAlert v-if="event.isOverdue" class="ac-chip-overdue" aria-label="逾期" />
	</span>
</template>

<style scoped>
.ac-chip {
	display: flex;
	align-items: center;
	min-width: 0;
	gap: 5px;
	padding: 1px 2px;
	overflow: hidden;
	color: inherit;
	font-size: var(--fs-12);
	line-height: 1.5;
	white-space: nowrap;
}

.ac-chip-icon,
.ac-chip-overdue {
	flex: none;
	width: 13px;
	height: 13px;
}

.ac-chip--projected .ac-chip-icon {
	color: var(--accent);
}

.ac-chip--todo .ac-chip-icon {
	color: var(--muted);
}

.ac-chip--todo.ac-status-done .ac-chip-icon,
.ac-chip--run.ac-status-completed .ac-chip-icon {
	color: var(--success);
}

.ac-chip--run.ac-status-failed .ac-chip-icon,
.ac-chip--run.ac-status-interrupted .ac-chip-icon,
.ac-chip--run.ac-status-cancelled .ac-chip-icon {
	color: var(--danger);
}

.ac-chip--run.ac-status-running .ac-chip-icon {
	color: var(--accent);
	animation: ac-chip-spin 1.1s linear infinite;
}

.ac-chip--run.ac-status-skipped .ac-chip-icon {
	color: var(--muted-soft);
}

.ac-chip.is-overdue .ac-chip-icon,
.ac-chip-overdue {
	color: var(--danger);
}

.ac-chip-time {
	flex: none;
	color: var(--muted);
	font-variant-numeric: tabular-nums;
}

.ac-chip-title {
	min-width: 0;
	overflow: hidden;
	text-overflow: ellipsis;
	font-weight: 600;
}

.ac-chip-badge,
.ac-chip-duration {
	flex: none;
	padding: 0 5px;
	border-radius: 999px;
	background: var(--panel-muted);
	color: var(--muted);
	font-size: var(--fs-11);
	font-weight: 600;
}

@keyframes ac-chip-spin {
	to {
		transform: rotate(360deg);
	}
}

@media (prefers-reduced-motion: reduce) {
	.ac-chip--run.ac-status-running .ac-chip-icon {
		animation: none;
	}
}
</style>
