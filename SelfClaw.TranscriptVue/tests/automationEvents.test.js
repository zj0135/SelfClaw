import { describe, expect, it } from 'vitest';
import {
	createTaskForm,
	describeNextRun,
	filterRuns,
	filterTasks,
	formToWireTask,
	localInputToUtcIso,
	runStatusLabel,
	taskNextRunText,
	taskToForm,
	toCalendarEvents,
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

	it('renders the host-computed next run in local time', () => {
		expect(describeNextRun('2026-07-01T13:00:00.000Z', { timeZone: TZ }).text).toBe('2026-07-01 09:00（本地时间）');
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
			.toBe('2026-11-01 01:30（本地时间）');
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
