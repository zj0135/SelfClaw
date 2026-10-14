// 自动化日历与表单的纯函数层：host 事件模型 → FullCalendar 事件、表单校验、时区换算、
// 清单/历史筛选。这里不依赖 Vue 与宿主，便于单测；cron 语义仍只由 host 计算。

const MINIMUM_INTERVAL_MINUTES = 5;
const MAXIMUM_TITLE_LENGTH = 120;
const DEFAULT_PROJECTION_LIMIT = 300;

const KIND_LABELS = {
	none: '待办',
	once: '一次性',
	daily: '每天',
	weekly: '每周',
	monthly: '每月',
	cron: '自定义',
};

const RUN_STATUS_LABELS = {
	queued: '排队中',
	running: '运行中',
	completed: '已完成',
	failed: '失败',
	cancelled: '已取消',
	interrupted: '已中断',
	skipped: '已跳过',
};

const TRIGGER_LABELS = {
	scheduled: '定时',
	manual: '手动',
	catchUp: '补跑',
};

const ERROR_MESSAGES = {
	InvalidTitle: `标题必填，且不超过 ${MAXIMUM_TITLE_LENGTH} 个字符。`,
	TriggerRequiresPrompt: '带触发器的任务必须填写提示词。',
	FullAccessRequired: '请确认该任务可在无人值守时读写文件并执行命令。',
	InvalidSchedule: '触发器配置无效。',
	IntervalTooShort: `触发间隔不能小于 ${MINIMUM_INTERVAL_MINUTES} 分钟。`,
	RunConflict: '正在进行的运行必须先停止。',
	DeleteTimeout: '在限定时间内未能停止运行，删除已中止。',
	UnknownRun: '该运行记录已不存在。',
	UnknownTask: '该任务已不存在。',
};

export function taskKindLabel(kind) {
	return KIND_LABELS[kind] || kind || '';
}

export function runStatusLabel(status) {
	return RUN_STATUS_LABELS[status] || status || '';
}

export function runTriggerLabel(trigger) {
	return TRIGGER_LABELS[trigger] || trigger || '';
}

export function errorMessage(errorCode) {
	return ERROR_MESSAGES[errorCode] || errorCode || '操作失败。';
}

// 只有真实待办与一次性任务的投影可拖：周期投影拖动一次无法界定改的是哪一次，历史 run 已终态。
export function isCalendarEventDraggable(event) {
	if (!event) return false;
	if (event.kind === 'todo') return true;
	return event.kind === 'projected' && event.taskKind === 'once';
}

function localDateString(date) {
	const year = date.getFullYear();
	const month = String(date.getMonth() + 1).padStart(2, '0');
	const day = String(date.getDate()).padStart(2, '0');
	return `${year}-${month}-${day}`;
}

function calendarEventId(event) {
	if (event.kind === 'run') return `run:${event.runId}`;
	if (event.kind === 'todo') return `todo:${event.taskId}`;
	return `projected:${event.taskId}:${event.atUtc}`;
}

function toCalendarEvent(event) {
	const draggable = isCalendarEventDraggable(event);
	const overdue = event.kind === 'todo' && event.isOverdue === true;
	const running = event.kind === 'run' && event.status === 'running';
	const classNames = ['ac-event', `ac-event--${event.kind}`];
	if (event.status) classNames.push(`ac-status-${event.status}`);
	if (overdue) classNames.push('ac-event--overdue');
	if (running) classNames.push('ac-event--running');
	return {
		id: calendarEventId(event),
		title: event.title || '',
		start: event.allDay ? localDateString(new Date(event.atUtc)) : event.atUtc,
		allDay: Boolean(event.allDay),
		editable: draggable,
		startEditable: draggable,
		durationEditable: false,
		classNames,
		extendedProps: { ...event, isOverdue: overdue },
	};
}

// events 是 host 的 AutomationCalendarEvent[]；limit 兜底防止 host 侧截断失效。
export function toCalendarEvents(events = [], { truncated = false, limit = DEFAULT_PROJECTION_LIMIT } = {}) {
	const list = Array.isArray(events) ? events : [];
	return {
		events: list.slice(0, limit).map(toCalendarEvent),
		truncated: truncated || list.length > limit,
	};
}

function hasSchedule(kind, schedule) {
	if (!schedule) return false;
	switch (kind) {
		case 'once':
			return Boolean(schedule.runAtLocal || schedule.runAtUtc);
		case 'daily':
			return /^\d{2}:\d{2}$/.test(schedule.time || '');
		case 'weekly':
			return Array.isArray(schedule.weekdays) && schedule.weekdays.length > 0;
		case 'monthly':
			return Number.isInteger(schedule.dayOfMonth) && schedule.dayOfMonth >= 1 && schedule.dayOfMonth <= 31;
		case 'cron':
			return Boolean((schedule.cron || '').trim());
		default:
			return true;
	}
}

// previewErrorCode 来自 host 的 automation/preview-schedule；<5 分钟等 cron 语义不在前端重算。
export function validateTaskForm(form = {}, { previewErrorCode = null } = {}) {
	const title = String(form.title ?? '').trim();
	if (!title) return { ok: false, errorCode: 'InvalidTitle', message: ERROR_MESSAGES.InvalidTitle };
	if (title.length > MAXIMUM_TITLE_LENGTH) {
		return { ok: false, errorCode: 'InvalidTitle', message: ERROR_MESSAGES.InvalidTitle };
	}
	const kind = form.kind || 'none';
	const prompt = String(form.prompt ?? '').trim();
	if (kind !== 'none' && !prompt) {
		return { ok: false, errorCode: 'TriggerRequiresPrompt', message: ERROR_MESSAGES.TriggerRequiresPrompt };
	}
	if (prompt && form.fullAccessConfirmed === false) {
		return { ok: false, errorCode: 'FullAccessRequired', message: ERROR_MESSAGES.FullAccessRequired };
	}
	if (kind !== 'none' && !hasSchedule(kind, form.schedule)) {
		return { ok: false, errorCode: 'InvalidSchedule', message: ERROR_MESSAGES.InvalidSchedule };
	}
	if (previewErrorCode) {
		return { ok: false, errorCode: previewErrorCode, message: errorMessage(previewErrorCode) };
	}
	return { ok: true, errorCode: null, message: '' };
}

function timeZoneOffsetMilliseconds(date, timeZone) {
	const formatter = new Intl.DateTimeFormat('en-US', {
		timeZone,
		hourCycle: 'h23',
		year: 'numeric',
		month: '2-digit',
		day: '2-digit',
		hour: '2-digit',
		minute: '2-digit',
		second: '2-digit',
	});
	const parts = {};
	for (const part of formatter.formatToParts(date)) parts[part.type] = part.value;
	const asUtc = Date.UTC(
		Number(parts.year),
		Number(parts.month) - 1,
		Number(parts.day),
		Number(parts.hour),
		Number(parts.minute),
		Number(parts.second)
	);
	return asUtc - date.getTime();
}

function wallClock(value, timeZone) {
	const formatter = new Intl.DateTimeFormat('en-CA', {
		timeZone,
		hourCycle: 'h23',
		year: 'numeric',
		month: '2-digit',
		day: '2-digit',
		hour: '2-digit',
		minute: '2-digit',
	});
	const parts = {};
	for (const part of formatter.formatToParts(value)) parts[part.type] = part.value;
	return `${parts.year}-${parts.month}-${parts.day}T${parts.hour}:${parts.minute}`;
}

// datetime-local 值 → UTC ISO。与 host 的 DST 规则一致：不存在的本地时刻返回 null，
// 回拨时取较早的那个瞬时（offset 较大者）。
export function localInputToUtcIso(value, timeZone) {
	const match = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2})$/.exec(String(value ?? ''));
	if (!match) return null;
	const naive = Date.UTC(
		Number(match[1]),
		Number(match[2]) - 1,
		Number(match[3]),
		Number(match[4]),
		Number(match[5])
	);
	const firstOffset = timeZoneOffsetMilliseconds(new Date(naive), timeZone);
	const firstUtc = naive - firstOffset;
	const secondOffset = timeZoneOffsetMilliseconds(new Date(firstUtc), timeZone);
	const candidate = new Date(naive - secondOffset);
	if (wallClock(candidate, timeZone) !== String(value)) return null;
	return candidate.toISOString();
}

// UTC ISO → datetime-local 显示值（本地墙上时间）。
export function utcIsoToLocalInput(iso, timeZone) {
	if (!iso) return '';
	const date = new Date(iso);
	if (Number.isNaN(date.getTime())) return '';
	return wallClock(date, timeZone);
}

export function formatLocalDateTime(value, timeZone) {
	const date = value instanceof Date ? value : new Date(value);
	if (Number.isNaN(date.getTime())) return '';
	return wallClock(date, timeZone).replace('T', ' ');
}

export function describeNextRun(nextRunAtUtc, { timeZone } = {}) {
	if (!nextRunAtUtc) return { text: '不自动触发', scheduled: false };
	return { text: `${formatLocalDateTime(nextRunAtUtc, timeZone)}（本地时间）`, scheduled: true };
}

export function taskNextRunText(task, { timeZone } = {}) {
	if (!task || !task.isEnabled || task.kind === 'none') return '不自动触发';
	if (!task.nextRunAtUtc) return '待调度';
	return describeNextRun(task.nextRunAtUtc, { timeZone }).text;
}

// 纯 todo：形状上是 kind=none（有无提示词都算用户可勾选的任务）。
export function isUserCheckableTask(task) {
	return Boolean(task) && (task.kind || 'none') === 'none';
}

export function isManualAiTask(task) {
	return Boolean(task) && (task.kind || 'none') === 'none' && Boolean(String(task.prompt ?? '').trim());
}

export function filterTasks(tasks = [], { query = '', status = 'all' } = {}) {
	const keyword = query.trim().toLowerCase();
	return tasks.filter((task) => {
		if (status !== 'all' && task.status !== status) return false;
		if (!keyword) return true;
		return [task.title, task.notes, task.prompt]
			.some((value) => String(value ?? '').toLowerCase().includes(keyword));
	});
}

export function filterRuns(runs = [], { query = '', status = 'all' } = {}) {
	const keyword = query.trim().toLowerCase();
	return runs.filter((run) => {
		if (status !== 'all' && run.status !== status) return false;
		if (!keyword) return true;
		return [run.id, run.errorCode, run.errorMessage]
			.some((value) => String(value ?? '').toLowerCase().includes(keyword));
	});
}

export function formatDuration(durationMs) {
	if (durationMs == null) return '';
	const seconds = Math.round(durationMs / 1000);
	if (seconds < 60) return `${seconds} 秒`;
	const minutes = Math.floor(seconds / 60);
	return `${minutes} 分 ${seconds % 60} 秒`;
}

// host 用 SQL LENGTH() 粗算的转录体积，这里只做人类可读换算。
export function formatBytes(bytes) {
	const value = Number(bytes) || 0;
	if (value < 1024) return `${value} B`;
	const units = ['KB', 'MB', 'GB', 'TB'];
	let size = value / 1024;
	let index = 0;
	while (size >= 1024 && index < units.length - 1) {
		size /= 1024;
		index += 1;
	}
	return `${size >= 100 ? Math.round(size) : size.toFixed(1)} ${units[index]}`;
}

// ===== 任务表单 ↔ 宿主 wire 形状 =====

const DEFAULT_CRON = '0 * * * *';

function defaultSchedule(timeZone) {
	return {
		time: '09:00',
		weekdays: [1],
		dayOfMonth: 1,
		runAtLocal: utcIsoToLocalInput(new Date(Date.now() + 3600_000).toISOString(), timeZone),
		cron: DEFAULT_CRON,
	};
}

export function createTaskForm({ timeZone, overrides = {} } = {}) {
	return {
		id: null,
		title: '',
		notes: '',
		kind: 'none',
		schedule: defaultSchedule(timeZone),
		prompt: '',
		instructions: '',
		toolPolicy: 'system',
		pluginIds: [],
		skillIds: [],
		mcpServerIds: [],
		modelProfileId: null,
		workspaceRootId: null,
		dueAtLocal: '',
		isEnabled: true,
		status: 'open',
		fullAccessConfirmed: false,
		...overrides,
	};
}

export function taskToForm(task, { timeZone, overrides = {} } = {}) {
	const schedule = task.schedule || {};
	return createTaskForm({
		timeZone,
		overrides: {
			id: task.id,
			title: task.title || '',
			notes: task.notes || '',
			kind: task.kind || 'none',
			schedule: {
				time: schedule.time || '09:00',
				weekdays: Array.isArray(schedule.weekdays) && schedule.weekdays.length ? [...schedule.weekdays] : [1],
				dayOfMonth: schedule.dayOfMonth || 1,
				runAtLocal: utcIsoToLocalInput(schedule.runAtUtc, timeZone),
				cron: schedule.cron || DEFAULT_CRON,
			},
			prompt: task.prompt || '',
			instructions: task.instructions || '',
			toolPolicy: task.toolPolicy || 'system',
			pluginIds: [...(task.pluginIds || [])],
			skillIds: [...(task.skillIds || [])],
			mcpServerIds: [...(task.mcpServerIds || [])],
			modelProfileId: task.modelProfileId || null,
			workspaceRootId: task.workspaceRootId || null,
			dueAtLocal: task.dueAtUtc ? utcIsoToLocalInput(task.dueAtUtc, timeZone).slice(0, 10) : '',
			isEnabled: task.isEnabled !== false,
			status: task.status || 'open',
			fullAccessConfirmed: Boolean(task.prompt),
			...overrides,
		},
	});
}

export function wireSchedule(form, timeZone) {
	const schedule = form.schedule || {};
	switch (form.kind) {
		case 'once':
			return { version: 1, runAtUtc: localInputToUtcIso(schedule.runAtLocal, timeZone) };
		case 'daily':
			return { version: 1, time: schedule.time };
		case 'weekly':
			return { version: 1, time: schedule.time, weekdays: [...(schedule.weekdays || [])] };
		case 'monthly':
			return { version: 1, time: schedule.time, dayOfMonth: Number(schedule.dayOfMonth) };
		case 'cron':
			return { version: 1, cron: String(schedule.cron || '').trim() };
		default:
			return null;
	}
}

export function formToWireTask(form, timeZone) {
	const prompt = String(form.prompt ?? '').trim();
	const todo = (form.kind || 'none') === 'none' && !prompt;
	return {
		id: form.id,
		title: String(form.title ?? '').trim(),
		notes: form.notes || '',
		kind: form.kind || 'none',
		schedule: form.kind === 'none' ? null : wireSchedule(form, timeZone),
		prompt: prompt || null,
		instructions: form.instructions || '',
		toolPolicy: form.toolPolicy || 'system',
		pluginIds: [...(form.pluginIds || [])],
		skillIds: [...(form.skillIds || [])],
		mcpServerIds: [...(form.mcpServerIds || [])],
		modelProfileId: form.modelProfileId || null,
		workspaceRootId: form.workspaceRootId || null,
		dueAtUtc: todo && form.dueAtLocal ? localInputToUtcIso(`${form.dueAtLocal}T09:00`, timeZone) : null,
		isEnabled: form.isEnabled !== false,
		status: form.status || 'open',
	};
}
