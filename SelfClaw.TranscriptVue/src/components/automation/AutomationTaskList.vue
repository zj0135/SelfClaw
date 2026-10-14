<script setup>
import { Check, Eraser, Loader, Pencil, Play, Square, Trash2 } from 'lucide-vue-next';
import { formatBytes, isManualAiTask, isUserCheckableTask, runStatusLabel, taskKindLabel, taskNextRunText } from '../../renderers/automationEvents';

const props = defineProps({
	tasks: { type: Array, default: () => [] },
	timeZone: { type: String, default: '' },
	pending: { type: Object, default: () => ({}) },
	deleting: { type: Object, default: () => ({}) },
});

const emit = defineEmits(['edit', 'run-now', 'toggle-enabled', 'toggle-status', 'clear-history', 'delete-task']);

function isPending(id) {
	return Boolean(props.pending?.[id]);
}
</script>

<template>
	<p v-if="!tasks.length" class="automation-empty">还没有任务。点击「新建任务」创建待办或定时任务。</p>
	<article v-for="task in tasks" :key="task.id" class="automation-task" :class="{ 'is-done': task.status === 'done' }">
		<button v-if="isUserCheckableTask(task)" type="button" class="automation-check" :aria-pressed="task.status === 'done'"
			:title="task.status === 'done' ? '标记为未完成' : '标记为完成'" @click="emit('toggle-status', task)">
			<Check v-if="task.status === 'done'" aria-hidden="true" />
			<Square v-else aria-hidden="true" />
		</button>
		<div class="automation-task-main" role="button" tabindex="0" @click="emit('edit', task)"
			@keydown.enter="emit('edit', task)">
			<span class="automation-task-title">{{ task.title }}</span>
			<span class="automation-task-meta">
				<span class="automation-kind">{{ taskKindLabel(task.kind) }}</span>
				<span v-if="task.kind !== 'none'" class="automation-meta-item">下次：{{ taskNextRunText(task, { timeZone }) }}</span>
				<span v-else-if="isManualAiTask(task)" class="automation-meta-item">手动 AI 任务</span>
				<span v-if="task.lastRunStatus" class="automation-meta-item">上次：{{ runStatusLabel(task.lastRunStatus) }}</span>
				<span v-if="task.historyBytes" class="automation-meta-item">历史：{{ formatBytes(task.historyBytes) }}</span>
				<span v-if="task.kind !== 'none' && !task.isEnabled" class="automation-meta-item is-warning">已停用</span>
			</span>
		</div>
		<div class="automation-task-actions">
			<button v-if="task.prompt" type="button" class="automation-icon-btn" title="立即运行"
				:disabled="isPending(task.id)" @click="emit('run-now', task)">
				<Loader v-if="isPending(task.id)" class="is-spinning" aria-hidden="true" />
				<Play v-else aria-hidden="true" />
			</button>
			<button type="button" class="automation-icon-btn" title="编辑" @click="emit('edit', task)">
				<Pencil aria-hidden="true" />
			</button>
			<button v-if="task.kind !== 'none'" type="button" class="automation-switch"
				:class="{ 'is-on': task.isEnabled }" :aria-pressed="task.isEnabled"
				:title="task.isEnabled ? '停用任务' : '启用任务'" @click="emit('toggle-enabled', task)">
				<span class="automation-switch-knob"></span>
			</button>
			<button v-if="task.historyBytes" type="button" class="automation-icon-btn"
				:disabled="Boolean(deleting[task.id])" title="清空该任务历史" @click="emit('clear-history', task)">
				<Loader v-if="deleting[task.id]" class="is-spinning" aria-hidden="true" />
				<Eraser v-else aria-hidden="true" />
			</button>
			<button type="button" class="automation-icon-btn is-danger"
				:disabled="Boolean(deleting[task.id])" title="删除任务" @click="emit('delete-task', task)">
				<Trash2 aria-hidden="true" />
			</button>
		</div>
	</article>
</template>

<style scoped>
.automation-empty {
	margin: 18px 6px;
	color: var(--muted);
	font-size: var(--fs-12);
	line-height: 1.6;
	text-align: center;
}

.automation-task {
	display: flex;
	align-items: center;
	gap: 8px;
	padding: 9px 10px;
	border: 1px solid var(--border);
	border-radius: 11px;
	background: var(--panel-soft);
	transition: border-color 0.16s, background 0.16s;
}

.automation-task:hover {
	border-color: var(--border-strong);
	background: var(--panel);
}

.automation-task.is-done .automation-task-title {
	color: var(--muted);
	text-decoration: line-through;
}

.automation-check,
.automation-icon-btn {
	display: inline-flex;
	align-items: center;
	justify-content: center;
	width: 26px;
	height: 26px;
	flex: none;
	border: 1px solid var(--border);
	border-radius: 8px;
	background: var(--panel);
	color: var(--muted);
}

.automation-check svg,
.automation-icon-btn svg {
	width: 14px;
	height: 14px;
}

.automation-icon-btn:disabled {
	opacity: 0.55;
}

.automation-icon-btn.is-danger:hover:not(:disabled) {
	border-color: color-mix(in srgb, var(--danger) 32%, transparent);
	color: var(--danger);
}

.automation-icon-btn .is-spinning {
	animation: ac-spin 1s linear infinite;
}

.automation-task-main {
	display: flex;
	min-width: 0;
	flex: 1 1 auto;
	flex-direction: column;
	gap: 3px;
	cursor: pointer;
}

.automation-task-title {
	overflow: hidden;
	color: var(--text);
	font-size: var(--fs-13);
	font-weight: 650;
	text-overflow: ellipsis;
	white-space: nowrap;
}

.automation-task-meta {
	display: flex;
	flex-wrap: wrap;
	gap: 4px 8px;
	color: var(--muted);
	font-size: var(--fs-11);
}

.automation-kind {
	padding: 0 6px;
	border-radius: 999px;
	background: color-mix(in srgb, var(--accent) 14%, transparent);
	color: var(--accent-2);
	font-weight: 650;
}

.automation-meta-item.is-warning {
	color: var(--danger);
}

.automation-task-actions {
	display: flex;
	flex: none;
	gap: 4px;
}

.automation-switch {
	position: relative;
	width: 32px;
	height: 18px;
	flex: none;
	padding: 0;
	border: 1px solid var(--border);
	border-radius: 999px;
	background: var(--panel-muted);
	transition: background 0.16s;
}

.automation-switch-knob {
	position: absolute;
	top: 2px;
	left: 2px;
	width: 12px;
	height: 12px;
	border-radius: 50%;
	background: var(--muted);
	transition: transform 0.16s, background 0.16s;
}

.automation-switch.is-on {
	background: color-mix(in srgb, var(--accent) 30%, transparent);
}

.automation-switch.is-on .automation-switch-knob {
	background: var(--accent);
	transform: translateX(14px);
}

@keyframes ac-spin {
	to {
		transform: rotate(360deg);
	}
}

@media (prefers-reduced-motion: reduce) {
	.automation-icon-btn .is-spinning {
		animation: none;
	}
}
</style>
