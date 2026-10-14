<script setup>
import { computed, onMounted, onUnmounted, ref, watch } from 'vue';
import { Bell, BellOff, RefreshCw } from 'lucide-vue-next';
import AutomationCalendar from '../components/automation/AutomationCalendar.vue';
import AutomationRunDrawer from '../components/automation/AutomationRunDrawer.vue';
import AutomationTaskDialog from '../components/automation/AutomationTaskDialog.vue';
import AutomationTaskPanel from '../components/automation/AutomationTaskPanel.vue';
import { browserTimeZone, useAutomation } from '../composables/useAutomation';
import { useToast } from '../composables/useToast';
import { createTaskForm, filterRuns, filterTasks, taskToForm, utcIsoToLocalInput } from '../renderers/automationEvents';

const {
	tasks, calendar, runs, settings, loading, error, record, recordLoading,
	loadCalendar, loadRuns, loadSettings, refresh, previewSchedule,
	saveTask, setTaskEnabled, runTaskNow, openRun, closeRun, updateSettings,
} = useAutomation();
const { showToast } = useToast();

const timeZone = browserTimeZone();
const dialog = ref({ open: false, saving: false, form: createTaskForm({ timeZone }) });
const activeTab = ref('tasks');
const query = ref('');
const runStatus = ref('all');
const pending = ref({});

const taskTitleById = computed(() => Object.fromEntries(tasks.value.map((task) => [task.id, task.title])));
const visibleTasks = computed(() => filterTasks(tasks.value, { query: query.value }));
const visibleRuns = computed(() => filterRuns(runs.value, { query: query.value, status: runStatus.value })
	.map((run) => ({ ...run, taskTitle: taskTitleById.value[run.taskId] })));
const drawerOpen = computed(() => Boolean(record.value) || recordLoading.value);

function localDate(date) {
	const year = date.getFullYear();
	const month = String(date.getMonth() + 1).padStart(2, '0');
	const day = String(date.getDate()).padStart(2, '0');
	return `${year}-${month}-${day}`;
}

function openCreate(dueDate = '') {
	const form = createTaskForm({ timeZone });
	form.dueAtLocal = dueDate;
	dialog.value = { open: true, saving: false, form };
}

function openEdit(task) {
	dialog.value = { open: true, saving: false, form: taskToForm(task, { timeZone }) };
}

async function submitTask(form) {
	dialog.value.saving = true;
	try {
		await saveTask(form);
		dialog.value = { ...dialog.value, open: false, saving: false };
	} catch (cause) {
		showToast(cause?.message || '保存任务失败。');
		dialog.value = { ...dialog.value, saving: false };
	}
}

async function toggleStatus(task) {
	try {
		const form = taskToForm(task, { timeZone });
		form.status = task.status === 'done' ? 'open' : 'done';
		await saveTask(form);
	} catch (cause) {
		showToast(cause?.message || '更新任务失败。');
	}
}

async function toggleEnabled(task) {
	try {
		await setTaskEnabled(task.id, !task.isEnabled);
	} catch (cause) {
		showToast(cause?.message || '更新任务状态失败。');
	}
}

async function runNow(task) {
	pending.value = { ...pending.value, [task.id]: true };
	try {
		await runTaskNow(task.id);
		showToast(`已启动「${task.title}」。`);
	} catch (cause) {
		showToast(cause?.message || '启动任务失败。');
	} finally {
		const next = { ...pending.value };
		delete next[task.id];
		pending.value = next;
	}
}

function onEventClick(event) {
	if (event.kind === 'run' && event.runId) {
		openRun(event.runId);
		return;
	}
	const task = tasks.value.find((item) => item.id === event.taskId);
	if (task) openEdit(task);
}

function onDateClick(info) {
	if (info.allDay) {
		openCreate(localDate(info.date));
		return;
	}

	// 时格点击预填「一次性」触发时间，而不是只丢一个截止日。
	const form = createTaskForm({ timeZone });
	form.kind = 'once';
	form.schedule.runAtLocal = utcIsoToLocalInput(info.date.toISOString(), timeZone);
	dialog.value = { open: true, saving: false, form };
}

async function onEventDrop({ event, start }) {
	const task = tasks.value.find((item) => item.id === event.taskId);
	if (!task || !start) return;
	const form = taskToForm(task, { timeZone });
	if (event.kind === 'todo') form.dueAtLocal = utcIsoToLocalInput(start.toISOString(), timeZone).slice(0, 10);
	else if (event.taskKind === 'once') form.schedule.runAtLocal = utcIsoToLocalInput(start.toISOString(), timeZone);
	else return;
	try {
		await saveTask(form);
	} catch (cause) {
		showToast(cause?.message || '调整触发时间失败。');
	}
}

async function toggleNotify() {
	try {
		await updateSettings(!settings.value.notifyOnSuccess);
	} catch (cause) {
		showToast(cause?.message || '保存自动化设置失败。');
	}
}

function onWindowFocus() {
	refresh();
}

onMounted(async () => {
	window.addEventListener('focus', onWindowFocus);
	await loadSettings();
	await refresh();
	await loadRuns({ status: 'all' });
});

onUnmounted(() => {
	window.removeEventListener('focus', onWindowFocus);
});

watch(activeTab, (tab) => {
	if (tab === 'runs') loadRuns({ status: runStatus.value === 'all' ? 'all' : runStatus.value });
});
watch(runStatus, (status) => {
	if (activeTab.value === 'runs') loadRuns({ status: status === 'all' ? 'all' : status });
});
</script>

<template>
	<main class="automation-view">
		<header class="automation-head">
			<div class="automation-head-text">
				<span class="automation-kicker">AUTOMATION</span>
				<h1>自动化</h1>
				<p>定时 LLM 任务与个人待办。时间一律按本地时区（{{ timeZone }}）计算。</p>
			</div>
			<div class="automation-head-actions">
				<button type="button" class="automation-ghost" :class="{ 'is-on': settings.notifyOnSuccess }"
					:title="settings.notifyOnSuccess ? '关闭成功通知' : '开启成功通知'" @click="toggleNotify">
					<Bell v-if="settings.notifyOnSuccess" aria-hidden="true" />
					<BellOff v-else aria-hidden="true" />
					<span>成功通知</span>
				</button>
				<button type="button" class="automation-ghost" :disabled="loading" title="刷新" @click="refresh">
					<RefreshCw :class="{ 'is-spinning': loading }" aria-hidden="true" />
					<span>刷新</span>
				</button>
			</div>
		</header>

		<p v-if="error" class="automation-banner" role="alert">{{ error }}</p>

		<div class="automation-body">
			<AutomationCalendar :events="calendar.events" :truncated="calendar.truncated"
				@range-change="loadCalendar" @event-click="onEventClick" @date-click="onDateClick"
				@event-drop="onEventDrop" />
			<AutomationTaskPanel :tasks="visibleTasks" :runs="visibleRuns" :active-tab="activeTab"
				:query="query" :run-status="runStatus" :time-zone="timeZone" :pending="pending"
				@update:active-tab="activeTab = $event" @update:query="query = $event"
				@update:run-status="runStatus = $event" @create="openCreate()" @edit="openEdit"
				@run-now="runNow" @toggle-enabled="toggleEnabled" @toggle-status="toggleStatus"
				@open-run="openRun($event.id)" />
		</div>

		<AutomationRunDrawer v-if="drawerOpen" :record="record" :loading="recordLoading" :time-zone="timeZone"
			@close="closeRun" />

		<AutomationTaskDialog :open="dialog.open" :form="dialog.form" :time-zone="timeZone"
			:saving="dialog.saving" :preview="previewSchedule" @close="dialog.open = false"
			@submit="submitTask" />
	</main>
</template>

<style scoped>
.automation-view {
	position: relative;
	display: flex;
	min-width: 0;
	height: 100%;
	flex-direction: column;
	overflow: hidden;
	background: var(--bg);
}

.automation-head {
	display: flex;
	flex: none;
	align-items: flex-end;
	justify-content: space-between;
	gap: 16px;
	padding: 20px 24px 14px;
	border-bottom: 1px solid var(--border);
}

.automation-kicker {
	color: var(--muted);
	font-size: var(--fs-11);
	font-weight: 650;
	letter-spacing: 0.14em;
}

.automation-head h1 {
	margin: 6px 0 0;
	font-family: var(--font-display);
	font-size: var(--fs-24);
	line-height: 1.1;
}

.automation-head p {
	margin: 4px 0 0;
	color: var(--muted);
	font-size: var(--fs-12);
}

.automation-head-actions {
	display: flex;
	flex: none;
	gap: 8px;
}

.automation-ghost {
	display: inline-flex;
	align-items: center;
	gap: 6px;
	padding: 7px 12px;
	border: 1px solid var(--border);
	border-radius: 9px;
	background: var(--panel-soft);
	color: var(--muted);
	font-size: var(--fs-12);
	font-weight: 650;
}

.automation-ghost.is-on {
	border-color: var(--accent);
	color: var(--accent-2);
}

.automation-ghost svg {
	width: 14px;
	height: 14px;
}

.automation-ghost .is-spinning {
	animation: automation-spin 1s linear infinite;
}

.automation-banner {
	margin: 10px 24px 0;
	padding: 8px 12px;
	border: 1px solid color-mix(in srgb, var(--danger) 32%, transparent);
	border-radius: 10px;
	background: color-mix(in srgb, var(--danger) 8%, transparent);
	color: var(--danger);
	font-size: var(--fs-12);
}

.automation-body {
	display: flex;
	min-height: 0;
	flex: 1 1 auto;
	padding-top: 10px;
}

@keyframes automation-spin {
	to {
		transform: rotate(360deg);
	}
}

@media (prefers-reduced-motion: reduce) {
	.automation-ghost .is-spinning {
		animation: none;
	}
}
</style>
