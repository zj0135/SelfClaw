// 自动化页的 Playwright 宿主夹具。必须自包含：addInitScript 会把函数源码注入页面，
// 任何对模块导入的引用都不可见，所以不能复用 activityHost 的安装函数。
export function installAutomationHost() {
	const seedTaskId = 'seed-task';
	const seedRunId = 'seed-run';
	const listeners = new Set();
	const requests = [];
	const iso = (value) => new Date(value).toISOString();
	const tasks = [{
		id: seedTaskId, title: '示例定时任务', notes: '', kind: 'daily',
		schedule: { version: 1, time: '09:00' }, nextRunAtUtc: iso(Date.now() + 3_600_000),
		prompt: 'produce the daily digest', instructions: '', toolPolicy: 'system',
		pluginIds: [], skillIds: [], mcpServerIds: [], modelProfileId: null, workspaceRootId: null,
		toolPermissionMode: 'fullAccess', status: 'open', isEnabled: true, historyBytes: 2048,
		createdAtUtc: iso(Date.now()),
	}];
	const runs = [{
		id: seedRunId, taskId: seedTaskId, trigger: 'scheduled', status: 'completed',
		scheduledForUtc: iso(Date.now() - 7_200_000), startedAtUtc: iso(Date.now() - 7_200_000),
		completedAtUtc: iso(Date.now() - 7_140_000), durationMs: 60_000, attempt: 1, createdAtUtc: iso(Date.now() - 7_200_000),
	}];
	const transcriptItems = [
		{ id: 'seed-u1', kind: 'message', role: 'user', status: 'sealed', isThinking: false, timestamp: '09:30', segments: [{ kind: 'content', markdown: 'Run the nightly report', segmentId: 'seed-u1:text:0' }] },
		{ id: 'seed-a1', kind: 'message', role: 'assistant', status: 'sealed', isThinking: false, timestamp: '09:31', segments: [{ kind: 'content', markdown: 'Seed transcript content', segmentId: 'seed-a1:text:0' }] },
	];
	let taskSequence = 0;

	const send = (payload) => {
		for (const listener of listeners) listener({ data: JSON.parse(JSON.stringify(payload)) });
	};
	const reply = (request, body) => send({ type: request.type, requestId: request.requestId, ...body });
	const taskTitle = (taskId) => tasks.find((task) => task.id === taskId)?.title || '自动化任务';
	const generic = () => ({
		sections: [], models: [], agents: [], views: [], openViews: [], roots: [], commonFolders: [], current: null,
		state: { revision: 1, agents: [], subagents: [], plugins: [], skills: [], mcpServers: [], providers: [], customProtocols: [], defaultModelProfileId: null },
	});

	function listCalendar(request) {
		const from = Date.parse(request.fromUtc);
		const to = Date.parse(request.toUtc);
		const events = [];
		for (const task of tasks) {
			if (task.isEnabled === false || task.kind === 'none') continue;
			const at = Date.now() + 3_600_000;
			if (at < from || at > to) continue;
			events.push({ kind: 'projected', taskId: task.id, atUtc: iso(at), allDay: false, title: task.title, taskKind: task.kind });
		}
		for (const run of runs) {
			const at = Date.parse(run.completedAtUtc || run.startedAtUtc || run.createdAtUtc);
			if (at < from || at > to) continue;
			events.push({ kind: 'run', taskId: run.taskId, runId: run.id, atUtc: iso(at), allDay: false, title: taskTitle(run.taskId), status: run.status, durationMs: run.durationMs });
		}
		reply(request, { ok: true, events, truncated: false });
	}

	function saveTask(request) {
		const incoming = request.task || {};
		const id = incoming.id || `task-${++taskSequence}`;
		let saved = tasks.find((task) => task.id === id);
		if (saved) Object.assign(saved, incoming);
		else {
			saved = { ...incoming, id };
			tasks.push(saved);
		}
		saved.nextRunAtUtc = saved.kind && saved.kind !== 'none' ? iso(Date.now() + 3_600_000) : null;
		saved.createdAtUtc = saved.createdAtUtc || iso(Date.now());
		reply(request, { ok: true, task: saved, nextRunAtUtc: saved.nextRunAtUtc });
		send({ type: 'automation/changed', reason: 'task-saved', taskId: id });
	}

	function getRun(request) {
		const run = runs.find((item) => item.id === request.runId);
		if (!run) {
			reply(request, { ok: false, errorCode: 'UnknownRun', errorMessage: 'missing' });
			return;
		}
		reply(request, { ok: true, run, task: tasks.find((task) => task.id === run.taskId) || null, transcript: { items: transcriptItems }, usage: { totalTokens: 1234 } });
	}

	function handle(request) {
		switch (request.type) {
			case 'automation/list-tasks':
				return reply(request, { ok: true, tasks, historyBytes: 2048 });
			case 'automation/list-calendar':
				return listCalendar(request);
			case 'automation/list-runs':
				return reply(request, { ok: true, runs });
			case 'automation/get-run':
				return getRun(request);
			case 'automation/save-task':
				return saveTask(request);
			case 'automation/preview-schedule': {
				const cron = request.schedule?.cron || '';
				if (cron.startsWith('*/1') || cron.startsWith('*/2')) {
					return reply(request, { ok: false, errorCode: 'IntervalTooShort', errorMessage: 'too short' });
				}
				return reply(request, { ok: true, nextRunAtUtc: iso(Date.now() + 3_600_000) });
			}
			case 'automation/set-task-enabled': {
				const task = tasks.find((item) => item.id === request.taskId);
				if (task) task.isEnabled = request.isEnabled !== false;
				return reply(request, { ok: true, task });
			}
			case 'automation/run-task-now':
				return reply(request, { ok: false, errorCode: 'RunConflict', errorMessage: 'busy' });
			case 'automation/cancel-run':
				return reply(request, { ok: true, run: runs[0] });
			case 'automation/delete-run': {
				const index = runs.findIndex((run) => run.id === request.runId);
				if (index < 0) return reply(request, { ok: false, deleted: 0, errorCode: 'UnknownRun', errorMessage: 'missing' });
				const [removed] = runs.splice(index, 1);
				reply(request, { ok: true, deleted: 1 });
				send({ type: 'automation/changed', reason: 'run-deleted', taskId: removed.taskId, runId: removed.id });
				return undefined;
			}
			case 'automation/clear-task-history': {
				const remaining = runs.filter((run) => run.taskId !== request.taskId);
				const deleted = runs.length - remaining.length;
				runs.length = 0;
				runs.push(...remaining);
				const task = tasks.find((item) => item.id === request.taskId);
				if (task) task.historyBytes = 0;
				reply(request, { ok: true, deleted });
				send({ type: 'automation/changed', reason: 'history-cleared', taskId: request.taskId });
				return undefined;
			}
			case 'automation/clear-history': {
				const deleted = runs.length;
				runs.length = 0;
				for (const task of tasks) task.historyBytes = 0;
				reply(request, { ok: true, deleted });
				send({ type: 'automation/changed', reason: 'history-cleared' });
				return undefined;
			}
			case 'automation/delete-task': {
				const index = tasks.findIndex((task) => task.id === request.taskId);
				if (index < 0) return reply(request, { ok: false, errorCode: 'UnknownTask', errorMessage: 'missing' });
				tasks.splice(index, 1);
				for (let cursor = runs.length - 1; cursor >= 0; cursor -= 1) {
					if (runs[cursor].taskId === request.taskId) runs.splice(cursor, 1);
				}
				reply(request, { ok: true });
				send({ type: 'automation/changed', reason: 'task-deleted', taskId: request.taskId });
				return undefined;
			}
			case 'automation/get-settings':
				return reply(request, { ok: true, settings: { notifyOnSuccess: false } });
			case 'automation/set-settings':
				return reply(request, { ok: true, settings: { notifyOnSuccess: Boolean(request.notifyOnSuccess) } });
			default:
				if (request.requestId) reply(request, generic());
		}
	}

	window.chrome = { ...(window.chrome || {}), webview: {
		addEventListener(type, listener) { if (type === 'message') listeners.add(listener); },
		removeEventListener(type, listener) { if (type === 'message') listeners.delete(listener); },
		postMessage(request) { requests.push(request); queueMicrotask(() => handle(request)); },
	} };
	window.automationFixture = { requests, send, runs, tasks };
}
