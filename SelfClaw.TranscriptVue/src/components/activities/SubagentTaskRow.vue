<script setup>
import { Ban, Check, CircleAlert, LoaderCircle, Square } from 'lucide-vue-next';
import { activityStatusLabel, deliveryStatusLabel, activityTimingLabel } from '../../renderers/activityLabels.js';
defineProps({ task: { type: Object, required: true }, selected: Boolean, cancelling: Boolean, now: Number });
const emit = defineEmits(['select', 'cancel']);
</script>

<template>
	<div class="task-row" :class="[task.status, { selected }]" :data-task-id="task.taskId">
		<button class="task-select" type="button" :aria-pressed="selected" @click="emit('select', task.taskId)">
			<span class="task-state" aria-hidden="true"><LoaderCircle v-if="task.status === 'running'" :size="14" class="spin" /><Check v-else-if="task.status === 'succeeded'" :size="14" /><CircleAlert v-else-if="['failed', 'interrupted'].includes(task.status)" :size="14" /><Ban v-else-if="task.status === 'cancelled'" :size="14" /><span v-else class="queued-dot" /></span>
			<span class="task-main"><strong>{{ task.subagentName || task.subagentId }}<small v-if="task.attempt > 1">#{{ task.attempt }}</small></strong><span class="task-preview">{{ task.taskPreview }}</span><span class="task-meta">{{ activityStatusLabel(task.phase) }}<span v-if="task.pendingApprovalCount"> · {{ task.pendingApprovalCount }} 项审批</span></span><span class="task-meta">{{ activityTimingLabel(task, now) }}</span><span v-if="deliveryStatusLabel(task.deliveryStatus)" class="task-delivery">{{ deliveryStatusLabel(task.deliveryStatus) }}</span></span>
		</button>
		<button v-if="task.canCancel" class="cancel-task" type="button" :disabled="cancelling" title="取消任务" aria-label="取消任务" @click="emit('cancel', task)"><LoaderCircle v-if="cancelling" :size="13" class="spin" /><Square v-else :size="12" /></button>
	</div>
</template>

<style scoped>
.task-row { display: flex; align-items: center; min-width: 0; border-bottom: 1px solid var(--border); }
.task-row.selected { background: color-mix(in srgb, var(--accent) 8%, transparent); box-shadow: inset 2px 0 var(--accent); }
.task-select { display: flex; align-items: flex-start; gap: 8px; min-width: 0; flex: 1; padding: 10px; border: 0; background: transparent; color: var(--text); text-align: left; }
.task-select:hover { background: var(--panel-soft); }
.task-state { flex: 0 0 16px; min-height: 18px; display: grid; place-items: center; color: var(--muted); }
.running .task-state { color: var(--accent); }.succeeded .task-state { color: var(--success); }.failed .task-state, .interrupted .task-state { color: var(--danger); }
.queued-dot { width: 7px; height: 7px; border: 1px solid currentColor; border-radius: 50%; }
.task-main { min-width: 0; display: grid; gap: 3px; flex: 1; }.task-main strong { display: flex; gap: 6px; font-size: var(--fs-12); font-weight: 600; overflow-wrap: anywhere; }.task-main strong small { color: var(--muted); }
.task-preview { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; font-size: var(--fs-11); color: var(--muted); }
.task-meta, .task-delivery { font-size: var(--fs-10); color: var(--muted); overflow-wrap: anywhere; }
.cancel-task { flex: 0 0 26px; height: 26px; display: grid; place-items: center; margin-right: 6px; color: var(--muted); border: 1px solid var(--border); background: transparent; border-radius: 4px; }.cancel-task:hover { color: var(--danger); }
.spin { animation: activity-spin 1s linear infinite; } @keyframes activity-spin { to { transform: rotate(360deg); } }
@media (prefers-reduced-motion: reduce) { .spin { animation: none; } }
</style>
