<script setup>
import { computed } from 'vue';
import { CircleCheck, CircleDashed, CirclePlay, SkipForward, CircleAlert, TriangleAlert } from 'lucide-vue-next';
import { formatDuration, runStatusLabel } from '../../renderers/automationEvents';

const props = defineProps({
	event: { type: Object, required: true },
	timeText: { type: String, default: '' },
});

const emit = defineEmits(['hover', 'leave']);

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
	<span
		class="ac-chip"
		:class="[`ac-chip--${event.kind}`, `ac-status-${event.status || 'none'}`, { 'is-overdue': event.isOverdue }]"
		@mouseenter="emit('hover', $event)"
		@mouseleave="emit('leave', $event)"
	>
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
	padding: 2px 6px;
	border-radius: 5px;
	overflow: hidden;
	color: var(--text);
	font-size: var(--fs-11);
	line-height: 1.4;
	white-space: nowrap;
	transition: background 0.14s ease, color 0.14s ease;
}

.ac-chip-icon,
.ac-chip-overdue {
	flex: none;
	width: 12px;
	height: 12px;
	opacity: 0.85;
}

.ac-chip--projected .ac-chip-icon {
	color: var(--accent);
}

.ac-chip--todo .ac-chip-icon {
	color: var(--muted);
}

.ac-chip--todo.ac-status-done {
	opacity: 0.72;
}

.ac-chip--todo.ac-status-done .ac-chip-title {
	text-decoration: line-through;
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
	animation: ac-chip-spin 1.2s cubic-bezier(0.4, 0, 0.2, 1) infinite;
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
	font-family: var(--font-code, monospace);
	font-size: 10.5px;
	font-variant-numeric: tabular-nums;
	letter-spacing: -0.02em;
}

.ac-chip-title {
	min-width: 0;
	overflow: hidden;
	text-overflow: ellipsis;
	font-weight: 580;
	letter-spacing: -0.01em;
}

.ac-chip-badge,
.ac-chip-duration {
	flex: none;
	padding: 0 4px;
	height: 16px;
	line-height: 16px;
	border-radius: 4px;
	background: color-mix(in srgb, var(--panel-muted) 80%, transparent);
	color: var(--muted);
	font-family: var(--font-code, monospace);
	font-size: 10px;
	font-weight: 600;
	font-variant-numeric: tabular-nums;
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
