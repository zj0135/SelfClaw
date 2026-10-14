import { ref } from 'vue';
import { isSuperseded, useHostBridge } from './hostBridge';
import { formToWireTask } from '../renderers/automationEvents';

// 触发时间一律按本机时区呈现；host 也只按本地时区计算。
const TIME_ZONE = Intl.DateTimeFormat().resolvedOptions().timeZone;

export function browserTimeZone() {
	return TIME_ZONE;
}

function toError(response, fallback) {
	const failure = new Error(response?.errorMessage || fallback);
	failure.errorCode = response?.errorCode || null;
	return failure;
}

function unwrap(response, fallback) {
	if (response && response.ok === false) throw toError(response, fallback);
	return response;
}

export function useAutomation() {
	const { request, requestLatest, on } = useHostBridge();
	const tasks = ref([]);
	const calendar = ref({ events: [], truncated: false });
	const runs = ref([]);
	const historyBytes = ref(0);
	const settings = ref({ notifyOnSuccess: false });
	const loading = ref(false);
	const error = ref('');
	const record = ref(null);
	const recordLoading = ref(false);
	const lastRange = ref(null);

	function report(cause) {
		if (!isSuperseded(cause)) error.value = cause?.message || '自动化请求失败。';
	}

	async function loadTasks() {
		const response = await request('automation/list-tasks');
		tasks.value = response.tasks || [];
		historyBytes.value = Number(response.historyBytes) || 0;
		return tasks.value;
	}

	async function loadCalendar(range) {
		if (!range?.fromUtc || !range?.toUtc) return calendar.value;
		lastRange.value = { fromUtc: range.fromUtc, toUtc: range.toUtc };
		try {
			const response = await requestLatest('automation/calendar', 'automation/list-calendar', lastRange.value);
			calendar.value = { events: response.events || [], truncated: Boolean(response.truncated) };
		} catch (cause) {
			report(cause);
		}
		return calendar.value;
	}

	async function loadRuns({ taskId = null, status = 'all', limit = 100 } = {}) {
		const payload = { limit };
		if (taskId) payload.taskId = taskId;
		if (status && status !== 'all') payload.status = status;
		const response = await request('automation/list-runs', payload);
		runs.value = response.runs || [];
		return runs.value;
	}

	async function loadSettings() {
		const response = await request('automation/get-settings');
		settings.value = response.settings || { notifyOnSuccess: false };
	}

	async function refresh() {
		loading.value = true;
		try {
			await Promise.all([
				loadTasks(),
				loadRuns(),
				lastRange.value ? loadCalendar(lastRange.value) : Promise.resolve(),
			]);
			error.value = '';
		} catch (cause) {
			report(cause);
		} finally {
			loading.value = false;
		}
	}

	async function previewSchedule(kind, schedule) {
		return request('automation/preview-schedule', { kind, schedule });
	}

	async function saveTask(form) {
		const response = unwrap(
			await request('automation/save-task', { task: formToWireTask(form, TIME_ZONE) }),
			'保存任务失败。'
		);
		await loadTasks();
		if (lastRange.value) await loadCalendar(lastRange.value);
		return response.task;
	}

	async function setTaskEnabled(taskId, isEnabled) {
		const response = unwrap(
			await request('automation/set-task-enabled', { taskId, isEnabled }),
			'更新任务状态失败。'
		);
		await refresh();
		return response.task;
	}

	async function runTaskNow(taskId) {
		const response = unwrap(await request('automation/run-task-now', { taskId }), '启动任务失败。');
		await loadRuns();
		return response.runId;
	}

	async function cancelRun(runId) {
		unwrap(await request('automation/cancel-run', { runId }), '停止运行失败。');
		await refresh();
	}

	async function deleteRun(runId) {
		unwrap(await request('automation/delete-run', { runId }), '删除运行记录失败。');
		await refresh();
	}

	async function clearTaskHistory(taskId) {
		unwrap(await request('automation/clear-task-history', { taskId }), '清空任务历史失败。');
		await refresh();
	}

	async function clearHistory() {
		unwrap(await request('automation/clear-history'), '清空历史失败。');
		await refresh();
	}

	async function deleteTask(taskId) {
		unwrap(await request('automation/delete-task', { taskId }), '删除任务失败。');
		await refresh();
	}

	async function updateSettings(notifyOnSuccess) {
		const response = unwrap(
			await request('automation/set-settings', { notifyOnSuccess }),
			'保存自动化设置失败。'
		);
		settings.value = response.settings || settings.value;
	}

	async function openRun(runId) {
		if (!runId) return;
		recordLoading.value = true;
		try {
			record.value = unwrap(await request('automation/get-run', { runId }), '加载运行记录失败。');
		} catch (cause) {
			report(cause);
		} finally {
			recordLoading.value = false;
		}
	}

	function closeRun() {
		record.value = null;
	}

	on('automation/changed', (payload) => {
		refresh();
		// run 详情是静态快照，但终态事件到达时要把它拉成新快照。
		if (payload.runId && record.value?.run?.id === payload.runId) openRun(payload.runId);
	});
	on('automation/open-run', (payload) => openRun(payload.runId), { replayLast: true });

	return {
		tasks,
		calendar,
		runs,
		historyBytes,
		settings,
		loading,
		error,
		record,
		recordLoading,
		loadTasks,
		loadCalendar,
		loadRuns,
		loadSettings,
		refresh,
		previewSchedule,
		saveTask,
		setTaskEnabled,
		runTaskNow,
		cancelRun,
		deleteRun,
		clearTaskHistory,
		clearHistory,
		deleteTask,
		updateSettings,
		openRun,
		closeRun,
	};
}
