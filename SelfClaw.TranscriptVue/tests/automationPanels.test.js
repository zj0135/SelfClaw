import { describe, expect, it } from 'vitest';
import { mount } from '@vue/test-utils';
import AutomationRunDrawer from '../src/components/automation/AutomationRunDrawer.vue';
import AutomationTaskPanel from '../src/components/automation/AutomationTaskPanel.vue';

const task = (overrides = {}) => ({
	id: 't-daily',
	title: '每日代码审查',
	notes: '',
	kind: 'daily',
	prompt: 'review the diff',
	schedule: { version: 1, time: '09:00' },
	nextRunAtUtc: '2026-07-01T13:00:00.000Z',
	status: 'open',
	isEnabled: true,
	lastRunStatus: 'completed',
	...overrides,
});

const run = (overrides = {}) => ({
	id: 'r-1',
	taskId: 't-daily',
	trigger: 'scheduled',
	status: 'completed',
	startedAtUtc: '2026-07-01T13:00:00.000Z',
	completedAtUtc: '2026-07-01T13:00:42.000Z',
	durationMs: 42000,
	...overrides,
});

describe('AutomationTaskPanel', () => {
	it('renders the task list tab with next-run context and switches to the run history tab', async () => {
		const tasks = [
			task(),
			task({ id: 't-todo', title: '交周报', kind: 'none', prompt: null, status: 'done' }),
			task({ id: 't-manual', title: '手动 AI', kind: 'none', prompt: 'write the summary' }),
		];
		const runs = [run({ taskTitle: '每日代码审查' })];
		const wrapper = mount(AutomationTaskPanel, {
			props: { tasks, runs, activeTab: 'tasks', query: '', runStatus: 'all', timeZone: 'America/New_York' },
		});

		expect(wrapper.findAll('.automation-task')).toHaveLength(3);
		expect(wrapper.text()).toContain('每日代码审查');
		expect(wrapper.text()).toContain('下次：');
		expect(wrapper.text()).toContain('2026-07-01 09:00');
		// §6.2：手动 AI 任务与纯待办都由用户勾选完成；周期任务不显示勾选框。
		expect(wrapper.findAll('.automation-check')).toHaveLength(2);

		await wrapper.setProps({ activeTab: 'runs' });
		expect(wrapper.findAll('.automation-run')).toHaveLength(1);
		expect(wrapper.find('.automation-run').text()).toContain('已完成');
		expect(wrapper.find('.automation-run').text()).toContain('42 秒');
	});

	it('emits filter and action intents instead of mutating its inputs', async () => {
		const wrapper = mount(AutomationTaskPanel, {
			props: { tasks: [task()], runs: [], activeTab: 'tasks', query: '', runStatus: 'all', timeZone: 'UTC' },
		});
		await wrapper.find('.automation-tab').trigger('click');
		expect(wrapper.emitted('update:activeTab')?.[0]).toEqual(['tasks']);
		await wrapper.find('input[type="search"]').setValue('周报');
		expect(wrapper.emitted('update:query')?.at(-1)).toEqual(['周报']);
		await wrapper.find('.automation-primary').trigger('click');
		expect(wrapper.emitted('create')).toHaveLength(1);
		await wrapper.find('.automation-switch').trigger('click');
		expect(wrapper.emitted('toggle-enabled')?.at(-1)?.[0]).toMatchObject({ id: 't-daily' });
	});

	it('offers the per-task reclaim action without printing the history size on the card', async () => {
		const tasks = [
			task({ historyBytes: 2048 }),
			task({ id: 't-todo', title: '交周报', kind: 'none', prompt: null, historyBytes: 0 }),
		];
		const wrapper = mount(AutomationTaskPanel, {
			props: { tasks, runs: [run({ taskTitle: '每日代码审查' })], activeTab: 'tasks', query: '', runStatus: 'all', timeZone: 'UTC', historyBytes: 4096 },
		});

		// 卡片只留「下次 / 上次」，体积只在运行历史页签显示。
		expect(wrapper.text()).not.toContain('历史：');
		const clearTaskHistory = wrapper.findAll('.automation-icon-btn').find((button) => button.attributes('title') === '清空该任务历史');
		await clearTaskHistory.trigger('click');
		expect(wrapper.emitted('clear-history')?.[0]?.[0]).toMatchObject({ id: 't-daily' });

		const deleteTask = wrapper.findAll('.automation-icon-btn').find((button) => button.attributes('title') === '删除任务');
		await deleteTask.trigger('click');
		expect(wrapper.emitted('delete-task')?.[0]?.[0]).toMatchObject({ id: 't-daily' });

		await wrapper.setProps({ activeTab: 'runs' });
		expect(wrapper.text()).toContain('历史体积 4.0 KB');
		await wrapper.find('.automation-run-delete').trigger('click');
		expect(wrapper.emitted('delete-run')?.[0]?.[0]).toMatchObject({ id: 'r-1' });
		await wrapper.find('.automation-history-clear').trigger('click');
		expect(wrapper.emitted('clear-history')?.at(-1)).toEqual([]);
	});

	it('switches the checklist scope and names the scope in the empty state', async () => {
		const wrapper = mount(AutomationTaskPanel, {
			props: {
				tasks: [], runs: [], activeTab: 'tasks', query: '', runStatus: 'all',
				taskScope: 'today', totalTasks: 7, timeZone: 'UTC', dateKey: '2026-10-15',
			},
		});

		expect(wrapper.text()).toContain('今天没有待办或任务');
		const scopeButtons = wrapper.findAll('.automation-scope-btn');
		expect(scopeButtons.map((button) => button.text())).toEqual(['今天', '全部']);
		expect(scopeButtons[0].attributes('aria-pressed')).toBe('true');
		await scopeButtons[1].trigger('click');
		expect(wrapper.emitted('update:taskScope')?.[0]).toEqual(['all']);
	});

	it('renders the todo due date so the day list explains why a task is listed', () => {
		const wrapper = mount(AutomationTaskPanel, {
			props: {
				tasks: [task({ kind: 'none', prompt: null, status: 'open', dueAtUtc: '2026-10-12T13:00:00.000Z' })],
				runs: [], activeTab: 'tasks', query: '', runStatus: 'all',
				taskScope: 'today', totalTasks: 1, timeZone: 'UTC', dateKey: '2026-10-15',
			},
		});

		const due = wrapper.find('.automation-meta-item.is-warning');
		expect(due.text()).toBe('逾期 3 天');
	});
});

describe('AutomationRunDrawer', () => {
	it('hands the host transcript items straight to TranscriptPanel', () => {
		const items = [
			{
				id: 'u1', kind: 'message', role: 'user', status: 'sealed', isThinking: false, timestamp: '09:30',
				segments: [{ kind: 'content', markdown: 'Run the nightly report', segmentId: 'u1:text:0', segmentOrdinal: 0 }],
			},
			{
				id: 'a1', kind: 'message', role: 'assistant', status: 'sealed', isThinking: false, timestamp: '09:31',
				segments: [{ kind: 'content', markdown: '**Report ready** for verification', segmentId: 'a1:text:0', segmentOrdinal: 0 }],
			},
		];
		const wrapper = mount(AutomationRunDrawer, {
			props: {
				record: { run: run(), task: task(), transcript: { items }, usage: { totalTokens: 8123 } },
				loading: false,
				timeZone: 'America/New_York',
			},
		});

		expect(wrapper.findAll('.message-row')).toHaveLength(2);
		expect(wrapper.find('.transcript-panel').exists()).toBe(true);
		expect(wrapper.text()).toContain('Report ready');
		expect(wrapper.text()).toContain('Tokens：8123');
		expect(wrapper.text()).toContain('已完成');
	});

	it('closes through an emit and shows the run failure when there is no transcript', async () => {
		const wrapper = mount(AutomationRunDrawer, {
			props: {
				record: { run: run({ status: 'failed', errorCode: 'ProviderFailed', errorMessage: 'provider exploded' }), task: task(), transcript: { items: [] } },
				loading: false,
				timeZone: 'UTC',
			},
		});

		expect(wrapper.text()).toContain('provider exploded');
		expect(wrapper.find('.automation-drawer-empty').exists()).toBe(true);
		await wrapper.find('.automation-drawer-close').trigger('click');
		expect(wrapper.emitted('close')).toHaveLength(1);
	});
});
