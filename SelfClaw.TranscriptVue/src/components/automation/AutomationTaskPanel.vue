<script setup>
import { computed } from 'vue';
import { History, ListTodo, Plus, Search, Trash2 } from 'lucide-vue-next';
import AutomationRunList from './AutomationRunList.vue';
import AutomationTaskList from './AutomationTaskList.vue';
import { formatBytes, runStatusLabel } from '../../renderers/automationEvents';

const props = defineProps({
	tasks: { type: Array, default: () => [] },
	runs: { type: Array, default: () => [] },
	activeTab: { type: String, default: 'tasks' },
	query: { type: String, default: '' },
	runStatus: { type: String, default: 'all' },
	timeZone: { type: String, default: '' },
	pending: { type: Object, default: () => ({}) },
	deleting: { type: Object, default: () => ({}) },
	historyBytes: { type: Number, default: 0 },
});

const emit = defineEmits([
	'update:activeTab',
	'update:query',
	'update:runStatus',
	'create',
	'edit',
	'run-now',
	'toggle-enabled',
	'toggle-status',
	'open-run',
	'delete-run',
	'clear-history',
	'delete-task',
]);

const runStatuses = ['all', 'queued', 'running', 'completed', 'failed', 'cancelled', 'interrupted', 'skipped'];

const tabs = computed(() => [
	{ id: 'tasks', label: '清单', count: props.tasks.length, icon: ListTodo },
	{ id: 'runs', label: '运行历史', count: props.runs.length, icon: History },
]);
</script>

<template>
	<aside class="automation-panel">
		<header class="automation-panel-head">
			<div class="automation-tabs" role="tablist">
				<button v-for="tab in tabs" :key="tab.id" type="button" class="automation-tab"
					:class="{ 'is-active': activeTab === tab.id }" role="tab" :aria-selected="activeTab === tab.id"
					@click="emit('update:activeTab', tab.id)">
					<component :is="tab.icon" class="automation-tab-icon" aria-hidden="true" />
					<span>{{ tab.label }}</span>
					<span class="automation-tab-count">{{ tab.count }}</span>
				</button>
			</div>
			<button type="button" class="automation-primary" @click="emit('create')">
				<Plus class="automation-primary-icon" aria-hidden="true" />
				<span>新建任务</span>
			</button>
			<div class="automation-filters">
				<label class="automation-search">
					<Search class="automation-search-icon" aria-hidden="true" />
					<input :value="query" type="search" placeholder="筛选任务…" aria-label="筛选任务"
						@input="emit('update:query', $event.target.value)" />
				</label>
				<select v-if="activeTab === 'runs'" class="automation-select" :value="runStatus"
					aria-label="按状态筛选运行" @change="emit('update:runStatus', $event.target.value)">
					<option v-for="status in runStatuses" :key="status" :value="status">
						{{ status === 'all' ? '全部状态' : runStatusLabel(status) }}
					</option>
				</select>
			</div>
		</header>

		<div class="automation-panel-body" role="tabpanel">
			<div v-if="activeTab === 'runs'" class="automation-history-bar">
				<span class="automation-history-size">历史体积 {{ formatBytes(historyBytes) }}</span>
				<button type="button" class="automation-history-clear" :disabled="Boolean(deleting.all)"
					@click="emit('clear-history')">
					<Trash2 aria-hidden="true" />
					<span>清空全部历史</span>
				</button>
			</div>
			<AutomationTaskList v-if="activeTab === 'tasks'" :tasks="tasks" :time-zone="timeZone" :pending="pending"
				:deleting="deleting" @edit="emit('edit', $event)" @run-now="emit('run-now', $event)"
				@toggle-enabled="emit('toggle-enabled', $event)" @toggle-status="emit('toggle-status', $event)"
				@clear-history="emit('clear-history', $event)" @delete-task="emit('delete-task', $event)" />
			<AutomationRunList v-else :runs="runs" :time-zone="timeZone" :deleting="deleting"
				@open-run="emit('open-run', $event)" @delete-run="emit('delete-run', $event)" />
		</div>
	</aside>
</template>

<style scoped>
.automation-panel {
	display: flex;
	width: 360px;
	min-width: 0;
	flex: none;
	flex-direction: column;
	overflow: hidden;
	border-left: 1px solid var(--border);
	background: var(--panel);
}

.automation-panel-head {
	display: flex;
	flex: none;
	flex-direction: column;
	gap: 10px;
	padding: 14px 14px 12px;
	border-bottom: 1px solid var(--border);
}

.automation-tabs {
	display: flex;
	gap: 4px;
	padding: 3px;
	border-radius: 10px;
	background: var(--panel-muted);
}

.automation-tab {
	display: flex;
	align-items: center;
	min-width: 0;
	flex: 1 1 auto;
	gap: 6px;
	padding: 6px 10px;
	border: 0;
	border-radius: 8px;
	background: transparent;
	color: var(--muted);
	font-size: var(--fs-12);
	font-weight: 650;
	justify-content: center;
	transition: background 0.16s, color 0.16s;
}

.automation-tab.is-active {
	background: var(--panel);
	color: var(--text);
	box-shadow: var(--card-shadow);
}

.automation-tab-icon {
	width: 14px;
	height: 14px;
}

.automation-tab-count {
	padding: 0 5px;
	border-radius: 999px;
	background: var(--panel-soft);
	font-size: var(--fs-11);
	font-variant-numeric: tabular-nums;
}

.automation-primary {
	display: flex;
	align-items: center;
	justify-content: center;
	gap: 6px;
	padding: 8px 12px;
	border: 0;
	border-radius: 10px;
	background: var(--accent);
	color: #fff;
	font-size: var(--fs-13);
	font-weight: 650;
}

.automation-primary-icon {
	width: 15px;
	height: 15px;
}

.automation-filters {
	display: flex;
	gap: 8px;
}

.automation-search {
	display: flex;
	align-items: center;
	min-width: 0;
	flex: 1 1 auto;
	gap: 6px;
	padding: 6px 9px;
	border: 1px solid var(--border);
	border-radius: 9px;
	background: var(--panel-soft);
}

.automation-search-icon {
	width: 14px;
	height: 14px;
	color: var(--muted);
}

.automation-search input {
	min-width: 0;
	flex: 1 1 auto;
	border: 0;
	background: transparent;
	color: var(--text);
	font-size: var(--fs-12);
	outline: none;
}

.automation-select {
	padding: 6px 8px;
	border: 1px solid var(--border);
	border-radius: 9px;
	background: var(--panel-soft);
	color: var(--text);
	font-size: var(--fs-12);
}

.automation-panel-body {
	display: flex;
	min-height: 0;
	flex: 1 1 auto;
	flex-direction: column;
	gap: 6px;
	padding: 10px;
	overflow-y: auto;
}

.automation-history-bar {
	display: flex;
	align-items: center;
	justify-content: space-between;
	gap: 8px;
	padding: 6px 8px;
	border: 1px solid var(--border);
	border-radius: 9px;
	background: var(--panel-soft);
}

.automation-history-size {
	color: var(--muted);
	font-size: var(--fs-11);
	font-variant-numeric: tabular-nums;
}

.automation-history-clear {
	display: inline-flex;
	align-items: center;
	gap: 5px;
	padding: 4px 8px;
	border: 1px solid var(--border);
	border-radius: 8px;
	background: var(--panel);
	color: var(--muted);
	font-size: var(--fs-11);
	font-weight: 650;
}

.automation-history-clear svg {
	width: 13px;
	height: 13px;
}

.automation-history-clear:hover:not(:disabled) {
	border-color: color-mix(in srgb, var(--danger) 32%, transparent);
	color: var(--danger);
}

.automation-history-clear:disabled {
	opacity: 0.55;
}
</style>
