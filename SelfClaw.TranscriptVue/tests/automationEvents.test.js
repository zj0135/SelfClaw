import { describe, expect, it } from 'vitest';
import {
	createTaskForm,
	describeNextRun,
	filterRuns,
	filterTasks,
	formToWireTask,
	formatBytes,
	formatScheduleRule,
	isTaskInDayScope,
	localDateKey,
	localInputToUtcIso,
	parseCalendarTitle,
	runStatusLabel,
	taskNextRunText,
	taskToForm,
	toCalendarEvents,
	todoDueState,
	utcIsoToLocalInput,
	validateTaskForm,
	wireSchedule,
} from '../src/renderers/automationEvents.js';

const TZ = 'America/New_York';

const projected = (overrides = {}) => ({
	kind: 'projected',
	taskId: 't-daily',
	atUtc: '2026-10-13T01:30:00.000Z',
	allDay: false,
	title: '每日代码审查',
	taskKind: 'daily',
	...overrides,
});

describe('automation calendar projection', () => {
	it('maps the three event kinds and marks only todos plus once-projections draggable', () => {
		const { events } = toCalendarEvents([
			projected(),
			projected({ taskId: 't-once', taskKind: 'once', atUtc: '2026-10-14T01:30:00.000Z', title: '一次性报告' }),
			{ kind: 'todo', taskId: 't-todo', atUtc: '2026-10-15T13:00:00.000Z', allDay: true, title: '交周报', status: 'open', taskKind: 'none' },
			{ kind: 'run', taskId: 't-daily', runId: 'r-1', atUtc: '2026-10-13T02:00:00.000Z', allDay: false, title: '每日代码审查', status: 'completed', durationMs: 42000 },
		]);

		expect(events).toHaveLength(4);
		expect(events[0].extendedProps.kind).toBe('projected');
		expect(events[0].classNames).toContain('ac-event--projected');
		expect(events[0].editable).toBe(false);
		expect(events[1].editable).toBe(true);
		expect(events[2].allDay).toBe(true);
		expect(events[2].start).toBe('2026-10-15');
		expect(events[2].extendedProps.isOverdue).toBe(false);
		expect(events[3].id).toBe('run:r-1');
		expect(events[3].classNames).toContain('ac-status-completed');
	});

	it('styles overdue todos and surfaces the host truncation flag plus a local cap', () => {
		const overdue = toCalendarEvents([
			{ kind: 'todo', taskId: 't-late', atUtc: '2020-01-01T14:00:00.000Z', allDay: true, title: '逾期待办', status: 'open', isOverdue: true, taskKind: 'none' },
		]);
		expect(overdue.events[0].classNames).toContain('ac-event--overdue');
		expect(overdue.truncated).toBe(false);

		const capped = toCalendarEvents([projected(), projected({ atUtc: '2026-10-13T02:30:00.000Z' })], { limit: 1 });
		expect(capped.events).toHaveLength(1);
		expect(capped.truncated).toBe(true);

		const flagged = toCalendarEvents([projected()], { truncated: true });
		expect(flagged.truncated).toBe(true);
	});

	it('deduplicates projected occurrence when an executed run already exists for the same schedule', () => {
		const targetUtc = '2026-10-15T12:01:00.000Z';
		const { events } = toCalendarEvents([
			{ kind: 'projected', taskId: 't-frog', taskKind: 'once', atUtc: targetUtc, title: '完全的青蛙' },
			{ kind: 'run', taskId: 't-frog', runId: 'r-fail', atUtc: targetUtc, title: '完全的青蛙', status: 'failed' },
		]);

		expect(events).toHaveLength(1);
		expect(events[0].extendedProps.kind).toBe('run');
		expect(events[0].extendedProps.status).toBe('failed');
	});
});

describe('automation task form validation', () => {
	it('requires a title', () => {
		expect(validateTaskForm({ title: '   ', kind: 'none' })).toMatchObject({ ok: false, errorCode: 'InvalidTitle' });
		expect(validateTaskForm({ title: 'x'.repeat(121), kind: 'none' })).toMatchObject({ ok: false, errorCode: 'InvalidTitle' });
		expect(validateTaskForm({ title: '有效标题', kind: 'none' }).ok).toBe(true);
	});

	it('rejects a trigger without a prompt', () => {
		const result = validateTaskForm({
			title: '每日任务',
			kind: 'daily',
			schedule: { time: '09:00' },
			prompt: '',
			fullAccessConfirmed: true,
		});
		expect(result).toMatchObject({ ok: false, errorCode: 'TriggerRequiresPrompt' });
	});

	it('rejects an interval shorter than the host reported minimum', () => {
		const form = createTaskForm({ timeZone: TZ, overrides: { title: '高频任务', kind: 'cron', prompt: '跑一下', fullAccessConfirmed: true } });
		expect(validateTaskForm(form, { previewErrorCode: 'IntervalTooShort' })).toMatchObject({ ok: false, errorCode: 'IntervalTooShort' });
		expect(validateTaskForm(form, { previewErrorCode: null }).ok).toBe(true);
	});

	it('requires the unattended full-access confirmation whenever a prompt is present', () => {
		const form = createTaskForm({ timeZone: TZ, overrides: { title: '手动任务', kind: 'none', prompt: '写点东西' } });
		expect(validateTaskForm(form)).toMatchObject({ ok: false, errorCode: 'FullAccessRequired' });
		expect(validateTaskForm({ ...form, fullAccessConfirmed: true }).ok).toBe(true);
	});
});

describe('automation schedule preview honours DST', () => {
	it('drops a local time that does not exist across the spring-forward gap', () => {
		expect(localInputToUtcIso('2026-03-08T02:30', TZ)).toBeNull();
		expect(localInputToUtcIso('2026-03-08T03:30', TZ)).toBe('2026-03-08T07:30:00.000Z');
	});

	it('resolves the ambiguous fall-back time to the earlier instant, matching the host', () => {
		expect(localInputToUtcIso('2026-11-01T01:30', TZ)).toBe('2026-11-01T05:30:00.000Z');
		expect(utcIsoToLocalInput('2026-11-01T05:30:00.000Z', TZ)).toBe('2026-11-01T01:30');
	});

	it('renders the host-computed next run in local time without a redundant zone suffix', () => {
		expect(describeNextRun('2026-07-01T13:00:00.000Z', { timeZone: TZ }).text).toBe('2026-07-01 09:00');
		expect(describeNextRun(null, { timeZone: TZ })).toMatchObject({ text: '不自动触发', scheduled: false });
	});

	it('round-trips a once schedule and a todo due date through the wire shape', () => {
		const form = taskToForm(
			{ id: 't1', title: '一次性', kind: 'once', schedule: { version: 1, runAtUtc: '2026-11-01T05:30:00.000Z' } },
			{ timeZone: TZ }
		);
		expect(form.schedule.runAtLocal).toBe('2026-11-01T01:30');
		expect(wireSchedule(form, TZ)).toEqual({ version: 1, runAtUtc: '2026-11-01T05:30:00.000Z' });

		const todo = createTaskForm({ timeZone: TZ, overrides: { title: '交周报', kind: 'none', dueAtLocal: '2026-07-02' } });
		expect(formToWireTask(todo, TZ).dueAtUtc).toBe('2026-07-02T13:00:00.000Z');
		expect(taskNextRunText({ kind: 'once', isEnabled: true, nextRunAtUtc: '2026-11-01T05:30:00.000Z' }, { timeZone: TZ }))
			.toBe('2026-11-01 01:30');
	});
});

describe('automation list filtering', () => {
	const tasks = [
		{ id: 'a', title: '写周报', notes: '', prompt: null, kind: 'none', status: 'open' },
		{ id: 'b', title: '代码审查', notes: '每天', prompt: 'review', kind: 'daily', status: 'open' },
		{ id: 'c', title: '旧事项', notes: '', prompt: null, kind: 'none', status: 'done' },
	];

	it('filters the task list by keyword and status', () => {
		expect(filterTasks(tasks, { query: '周报' }).map((task) => task.id)).toEqual(['a']);
		expect(filterTasks(tasks, { query: 'REVIEW' }).map((task) => task.id)).toEqual(['b']);
		expect(filterTasks(tasks, { status: 'done' }).map((task) => task.id)).toEqual(['c']);
		expect(filterTasks(tasks)).toHaveLength(3);
	});

	it('filters the run history by keyword and status', () => {
		const runs = [
			{ id: 'r1', status: 'completed', errorCode: null, errorMessage: null },
			{ id: 'r2', status: 'failed', errorCode: 'ProviderFailed', errorMessage: 'boom' },
		];
		expect(filterRuns(runs, { status: 'failed' }).map((run) => run.id)).toEqual(['r2']);
		expect(filterRuns(runs, { query: 'provider' }).map((run) => run.id)).toEqual(['r2']);
		expect(filterRuns(runs, { status: 'all', query: '' })).toHaveLength(2);
		expect(runStatusLabel('skipped')).toBe('已跳过');
	});
});

describe('automation day scope', () => {
	// New York 的 2026-10-15 覆盖 UTC 的 10-15T04:00 到 10-16T04:00。
	const TODAY = '2026-10-15';
	const at = (overrides) => isTaskInDayScope({ id: 'x', kind: 'daily', ...overrides }, { timeZone: TZ, dateKey: TODAY });

	it('keys local days by time zone instead of UTC', () => {
		expect(localDateKey('2026-10-16T02:00:00.000Z', TZ)).toBe('2026-10-15');
		expect(localDateKey('2026-10-16T02:00:00.000Z', 'UTC')).toBe('2026-10-16');
		expect(localDateKey('not-a-date', TZ)).toBe('');
	});

	it('keeps scheduled tasks while their next or last trigger is today', () => {
		expect(at({ nextRunAtUtc: '2026-10-15T13:00:00.000Z' })).toBe(true);
		expect(at({ nextRunAtUtc: '2026-10-16T02:00:00.000Z' })).toBe(true);
		expect(at({ nextRunAtUtc: '2026-10-16T13:00:00.000Z' })).toBe(false);
		// 今天已经跑过的每日任务：下次是明天，但仍属于今天的清单。
		expect(at({ nextRunAtUtc: '2026-10-16T13:00:00.000Z', lastRunAtUtc: '2026-10-15T13:00:00.000Z' })).toBe(true);
	});

	it('keeps todos due today and overdue open todos, but not tomorrow’s work created today', () => {
		expect(at({ kind: 'none', status: 'open', dueAtUtc: '2026-10-15T13:00:00.000Z' })).toBe(true);
		expect(at({ kind: 'none', status: 'open', dueAtUtc: '2026-10-12T13:00:00.000Z' })).toBe(true);
		expect(at({ kind: 'none', status: 'done', dueAtUtc: '2026-10-12T13:00:00.000Z', completedAtUtc: '2026-10-13T13:00:00.000Z' })).toBe(false);
		expect(at({ kind: 'none', status: 'open', dueAtUtc: '2026-10-20T13:00:00.000Z' })).toBe(false);
		// 今天新建、明天到期的待办属于明天。
		expect(at({ kind: 'none', status: 'open', dueAtUtc: '2026-10-16T13:00:00.000Z', createdAtUtc: '2026-10-15T13:00:00.000Z' })).toBe(false);
		// 没有日期也没有调度的待办留在清单里，否则新建后立刻消失。
		expect(at({ kind: 'none', status: 'open', createdAtUtc: '2026-10-15T13:00:00.000Z' })).toBe(true);
		expect(at({ kind: 'none', status: 'done', createdAtUtc: '2026-10-15T13:00:00.000Z' })).toBe(false);
		expect(at({ kind: 'none', status: 'open', prompt: 'write it', createdAtUtc: '2026-09-01T13:00:00.000Z' })).toBe(true);
	});

	it('narrows the task list to the current day unless the scope is all', () => {
		const scoped = [
			{ id: 'today', title: '今天到期', kind: 'none', status: 'open', dueAtUtc: '2026-10-15T13:00:00.000Z' },
			{ id: 'later', title: '下周', kind: 'none', status: 'open', dueAtUtc: '2026-10-20T13:00:00.000Z', createdAtUtc: '2026-10-15T13:00:00.000Z' },
			{ id: 'scheduled', title: '每天', kind: 'daily', status: 'open', nextRunAtUtc: '2026-10-15T13:00:00.000Z' },
			{ id: 'idle', title: '没日期', kind: 'none', status: 'open' },
		];
		expect(filterTasks(scoped, { scope: 'today', timeZone: TZ, dateKey: TODAY }).map((task) => task.id)).toEqual(['today', 'scheduled', 'idle']);
		expect(filterTasks(scoped, { scope: 'all', timeZone: TZ, dateKey: TODAY })).toHaveLength(4);
		expect(filterTasks(scoped, { scope: 'today', query: '没日期', timeZone: TZ, dateKey: TODAY })).toHaveLength(1);
	});

	it('describes todo due dates with overdue emphasis', () => {
		const due = (iso, overrides = {}) => todoDueState(
			{ id: 'x', kind: 'none', status: 'open', dueAtUtc: iso, ...overrides },
			{ timeZone: TZ, dateKey: TODAY }
		);
		expect(due('2026-10-15T13:00:00.000Z')).toEqual({ text: '截止：今天', overdue: false });
		expect(due('2026-10-14T13:00:00.000Z')).toEqual({ text: '逾期 1 天', overdue: true });
		expect(due('2026-10-12T13:00:00.000Z')).toEqual({ text: '逾期 3 天', overdue: true });
		expect(due('2026-10-12T13:00:00.000Z', { status: 'done' })).toEqual({ text: '截止：10-12', overdue: false });
		expect(due('2026-10-16T13:00:00.000Z')).toEqual({ text: '截止：明天', overdue: false });
		expect(due('2026-10-20T13:00:00.000Z')).toEqual({ text: '截止：10-20', overdue: false });
		expect(todoDueState({ id: 'x', kind: 'none', status: 'open' }, { timeZone: TZ, dateKey: TODAY })).toBeNull();
	});
});

describe('automation history volume formatting', () => {
	it('renders bytes, kilobytes, megabytes and gigabytes', () => {
		expect(formatBytes(512)).toBe('512 B');
		expect(formatBytes(2048)).toBe('2.0 KB');
		expect(formatBytes(1536 * 1024)).toBe('1.5 MB');
		expect(formatBytes(3 * 1024 * 1024 * 1024)).toBe('3.0 GB');
		expect(formatBytes(0)).toBe('0 B');
	});
});

describe('automation schedule rule formatting', () => {
	it('formats daily, weekly, monthly, cron, and once tasks into readable rule strings', () => {
		expect(formatScheduleRule({ kind: 'daily', schedule: { time: '09:00' } })).toBe('每天 09:00');
		expect(formatScheduleRule({ kind: 'weekly', schedule: { time: '14:30', weekdays: [1, 3] } })).toBe('每周一、三 14:30');
		expect(formatScheduleRule({ kind: 'monthly', schedule: { time: '10:00', dayOfMonth: 15 } })).toBe('每月 15 日 10:00');
		expect(formatScheduleRule({ kind: 'cron', schedule: { cron: '0 * * * *' } })).toBe('Cron: 0 * * * *');
		expect(formatScheduleRule({ kind: 'once' })).toBe('一次性触发');
		expect(formatScheduleRule({ kind: 'none' })).toBe('');
		expect(formatScheduleRule(null)).toBe('');
	});
});

describe('automation calendar title parsing', () => {
	it('parses and condenses raw FullCalendar titles for month, week, day, and listWeek views', () => {
		// Month view
		expect(parseCalendarTitle('2026年10月')).toEqual({ kicker: '2026', title: '10月' });

		// Day view
		expect(parseCalendarTitle('2026年10月15日')).toEqual({ kicker: '2026', title: '10月15日' });

		// Week view (Chinese)
		expect(parseCalendarTitle('2026年10月12日 – 18日')).toEqual({ kicker: '2026', title: '10月12日 – 18日' });
		expect(parseCalendarTitle('2026年10月26日 – 11月1日')).toEqual({ kicker: '2026', title: '10月26日 – 11月1日' });
		expect(parseCalendarTitle('2026年10月12日 – 2026年10月18日')).toEqual({ kicker: '2026', title: '10月12日 – 10月18日' });

		// ListWeek view (slashes in zh-cn locale)
		expect(parseCalendarTitle('2026/10/12 – 2026/10/18')).toEqual({ kicker: '2026', title: '10月12日 – 18日' });
		expect(parseCalendarTitle('2026/10/26 – 2026/11/01')).toEqual({ kicker: '2026', title: '10月26日 – 11月1日' });
		expect(parseCalendarTitle('2026/12/28 – 2027/01/03')).toEqual({ kicker: '2026 – 2027', title: '2026年12月28日 – 2027年1月3日' });
		expect(parseCalendarTitle('2026/10/15')).toEqual({ kicker: '2026', title: '10月15日' });

		// Empty / non-matching fallback
		expect(parseCalendarTitle('')).toEqual({ kicker: 'CALENDAR', title: '日历' });
		expect(parseCalendarTitle('Calendar')).toEqual({ kicker: 'CALENDAR', title: 'Calendar' });
	});
});


