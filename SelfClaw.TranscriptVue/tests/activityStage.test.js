import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { mount, flushPromises } from '@vue/test-utils';
import { installActivityHost } from './fixtures/activityHost.js';

let wrapper;
beforeEach(() => {
	vi.resetModules();
	installActivityHost();
	globalThis.ResizeObserver = class { observe() {} disconnect() {} };
});
afterEach(() => { wrapper?.unmount(); wrapper = null; });

async function mountStage(props = { parentConversationId: window.activityFixture.parent }) {
	const { default: Stage } = await import('../src/components/Activities/ActivityStage.vue');
	return mount(Stage, { props });
}

async function expandActivityPanel() {
	await wrapper.find('.activity-toggle').trigger('click');
	await flushPromises();
}

async function mountExpandedStage(props = { parentConversationId: window.activityFixture.parent }) {
	wrapper = await mountStage(props);
	await flushPromises();
	await expandActivityPanel();
	return wrapper;
}

it('starts collapsed as a content-width chip in the stage corner until the user opens it', async () => {
	wrapper = await mountStage();
	await flushPromises();
	expect(wrapper.find('.activity-dock').classes()).toContain('collapsed');
	expect(wrapper.find('.activity-panel').classes()).toContain('collapsed');
	expect(wrapper.find('.activity-body').exists()).toBe(false);
	expect(wrapper.find('.task-row').exists()).toBe(false);
	await expandActivityPanel();
	expect(wrapper.findAll('.task-row')).toHaveLength(3);
});

it('applies and ACKs the correlated first snapshot, streams shared blocks, and cancels an independent child', async () => {
	wrapper = await mountExpandedStage();
	expect(wrapper.findAll('.task-row')).toHaveLength(3);
	expect(window.activityFixture.requests.some((request) => request.type === 'activity-panel/rendered')).toBe(true);
	await wrapper.find('.task-select').trigger('click');
	await flushPromises();
	expect(wrapper.find('.thinking-summary').exists()).toBe(true);
	await wrapper.find('.thinking-summary').trigger('click');
	expect(wrapper.find('.thinking-content').text()).toContain('Reasoning for');
	await wrapper.find('.tool-summary').trigger('click');
	window.activityFixture.text(' grows');
	await flushPromises();
	await vi.waitFor(() => expect(wrapper.find('.body-segment').text()).toContain('grows'));
	window.activityFixture.finish();
	await flushPromises();
	expect(wrapper.find('.tool-block').classes()).toContain('completed');
	expect(wrapper.find('.tool-details').text()).toContain('Recorded tool result');
	await wrapper.findAll('.cancel-task')[0].trigger('click');
	await flushPromises();
	expect(wrapper.findAll('.task-row.cancelled')).toHaveLength(1);
	const old = window.activityFixture.snapshot();
	await wrapper.setProps({ parentConversationId: 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb' });
	await flushPromises();
	window.activityFixture.send(old);
	await flushPromises();
	expect(wrapper.find('.task-detail').exists()).toBe(false);
});

it('preserves thinking expansion after switching tasks and does not nest buttons', async () => {
	wrapper = await mountExpandedStage();
	await wrapper.findAll('.task-select')[0].trigger('click');
	await flushPromises();
	await wrapper.find('.thinking-summary').trigger('click');
	await wrapper.findAll('.task-select')[1].trigger('click');
	await flushPromises();
	expect(wrapper.find('.thinking-content').exists()).toBe(false);
	await wrapper.findAll('.task-select')[0].trigger('click');
	await flushPromises();
	expect(wrapper.find('.thinking-content').exists()).toBe(true);
	expect(wrapper.findAll('button button')).toHaveLength(0);
});

it('retains invalidated historical content, resumes live, and restores selection after collapse and remount', async () => {
	const props = { parentConversationId: window.activityFixture.parent };
	wrapper = await mountExpandedStage(props);
	await wrapper.find('.task-select').trigger('click');
	await flushPromises();
	const invalidated = window.activityFixture.snapshot();
	delete invalidated.sections[0].detail;
	invalidated.sections[0].detailError = 'content-changed';
	invalidated.sections[0].selectedTask = { ...window.activityFixture.tasks[0], status: 'succeeded', phase: 'succeeded', deliveryStatus: 'pending', outputTokens: 123 };
	window.activityFixture.send(invalidated);
	await flushPromises();
	expect(wrapper.findAll('.task-row')).toHaveLength(3);
	await vi.waitFor(() => expect(wrapper.find('.body-segment').text()).toContain('Partial answer'));
	expect(wrapper.find('.detail-meta').text()).toContain('已完成');
	expect(wrapper.find('.detail-meta').text()).toContain('输出 123');
	expect(wrapper.find('.detail-meta').text()).toContain('等待父代理处理');
	await wrapper.find('.new-content').trigger('click');
	await flushPromises();
	expect(wrapper.find('.new-content').exists()).toBe(false);
	await wrapper.find('.activity-toggle').trigger('click');
	await flushPromises();
	window.activityFixture.text(' while collapsed');
	await flushPromises();
	expect(wrapper.find('.task-detail').exists()).toBe(false);
	await wrapper.find('.activity-toggle').trigger('click');
	await flushPromises();
	await vi.waitFor(() => expect(wrapper.find('.body-segment').text()).toContain('while collapsed'));
	wrapper.unmount();
	wrapper = await mountStage(props);
	await flushPromises();
	await vi.waitFor(() => expect(wrapper.find('.body-segment').text()).toContain('while collapsed'));
});

it('switches an independent todo section without replacing subagent preferences', async () => {
	const { default: Panel } = await import('../src/components/Activities/ActivityFloatingPanel.vue');
	const preferences = { open: true, section: 'subagents', taskId: 'selected-child' };
	const subagents = { id: 'subagents', kind: 'subagents', title: 'Subagents', counts: { total: 3 } };
	const todos = { id: 'todos', kind: 'todos', title: 'Todo', badge: 2, entries: [{ id: 'todo-1', title: 'Review' }] };
	wrapper = mount(Panel, { props: { sections: [subagents, todos], preferences }, slots: { default: '<p>{{ params.section.kind }}</p>' } });
	await wrapper.findAll('[role="tab"]')[1].trigger('click');
	expect(preferences.section).toBe('todos');
	await wrapper.setProps({ sections: [subagents] });
	expect(preferences.section).toBe('subagents');
	expect(preferences.taskId).toBe('selected-child');
	expect(wrapper.find('.badge').text()).toBe('3');
});

it('restores a long historical window after task switching, collapse, and remount', async () => {
	const props = { parentConversationId: window.activityFixture.parent };
	window.activityFixture.longContent();
	wrapper = await mountExpandedStage(props);
	await wrapper.find('.task-select').trigger('click');
	await flushPromises();
	expect(wrapper.findAll('.body-segment')[0].text()).toContain('History block 86');
	await wrapper.find('.window-link').trigger('click');
	await flushPromises();
	expect(wrapper.findAll('.body-segment')[0].text()).toContain('History block 22');
	await wrapper.findAll('.task-select')[1].trigger('click');
	await flushPromises();
	await wrapper.find('.task-select').trigger('click');
	await flushPromises();
	expect(wrapper.findAll('.body-segment')[0].text()).toContain('History block 22');
	await wrapper.find('.activity-toggle').trigger('click');
	await flushPromises();
	window.activityFixture.appendBlock('Output while hidden');
	await wrapper.find('.activity-toggle').trigger('click');
	await flushPromises();
	expect(wrapper.findAll('.body-segment')[0].text()).toContain('History block 22');
	expect(wrapper.find('.new-content').exists()).toBe(true);
	wrapper.unmount();
	wrapper = await mountStage(props);
	await flushPromises();
	expect(wrapper.findAll('.body-segment')[0].text()).toContain('History block 22');
	const request = window.activityFixture.requests.filter((item) => item.type === 'activity-panel/select-detail').at(-1);
	expect(request).toMatchObject({ blockOffset: 22, contentVersion: '2' });
});

it('returns from an invalidated historical window to the tail and follows subsequent output', async () => {
	window.activityFixture.longContent();
	wrapper = await mountExpandedStage();
	await wrapper.find('.task-select').trigger('click');
	await flushPromises();
	await wrapper.find('.window-link').trigger('click');
	await flushPromises();
	window.activityFixture.appendBlock('New tail after history');
	await flushPromises();
	expect(wrapper.findAll('.body-segment')[0].text()).toContain('History block 22');
	await wrapper.find('.new-content').trigger('click');
	await flushPromises();
	expect(wrapper.findAll('.body-segment').at(-1).text()).toContain('New tail after history');
	expect(wrapper.find('.new-content').exists()).toBe(false);
	window.activityFixture.appendBlock('Continues following');
	await flushPromises();
	expect(wrapper.findAll('.body-segment').at(-1).text()).toContain('Continues following');
	expect(wrapper.find('.new-content').exists()).toBe(false);
});

it('accepts push before initial response and ignores an older failed detail request', async () => {
	window.activityFixture.holdResponses(true);
	wrapper = await mountStage();
	await flushPromises();
	window.activityFixture.push();
	await flushPromises();
	await expandActivityPanel();
	expect(wrapper.findAll('.task-row')).toHaveLength(3);
	window.activityFixture.release();
	await flushPromises();
	await wrapper.findAll('.task-select')[0].trigger('click');
	await flushPromises();
	await wrapper.findAll('.task-select')[1].trigger('click');
	await flushPromises();
	window.activityFixture.release(1);
	await flushPromises();
	window.activityFixture.release(0, 'activity-subscription-changed');
	await flushPromises();
	expect(wrapper.find('.task-detail header').text()).toContain('Reviewer 2');
	expect(wrapper.findAll('[role="alert"]')).toHaveLength(0);
	expect(wrapper.find('.detail-state').exists()).toBe(false);
});

it('keeps the confirmed page on failure and resets an invalidated cursor without losing detail selection', async () => {
	window.activityFixture.setTaskCount(53);
	wrapper = await mountExpandedStage();
	await wrapper.find('.task-select').trigger('click');
	await flushPromises();
	window.activityFixture.holdResponses(true);
	await wrapper.find('[aria-label="下一页任务"]').trigger('click');
	await flushPromises();
	expect(wrapper.find('.task-list').attributes('aria-busy')).toBe('true');
	expect(wrapper.findAll('.task-row')).toHaveLength(50);
	window.activityFixture.release(0, 'temporary-read-failure');
	await flushPromises();
	expect(wrapper.find('.task-list footer span').text()).toBe('1');
	expect(wrapper.find('[aria-label="上一页任务"]').attributes('disabled')).toBeDefined();
	expect(wrapper.find('.task-list').attributes('aria-busy')).toBe('false');
	window.activityFixture.holdResponses(false);
	await wrapper.find('[aria-label="下一页任务"]').trigger('click');
	await flushPromises();
	expect(wrapper.findAll('.task-row')).toHaveLength(3);
	expect(wrapper.find('.task-list footer span').text()).toBe('2');
	expect(wrapper.find('.task-counts').text()).toContain('53 个任务');
	window.activityFixture.resetTaskPage();
	await flushPromises();
	expect(wrapper.findAll('.task-row')).toHaveLength(50);
	expect(wrapper.find('.task-list footer span').text()).toBe('1');
	expect(wrapper.find('.task-detail header').text()).toContain('Reviewer 1');
});

it('ignores an unknown section and discards content that arrives after selecting another task', async () => {
	wrapper = await mountExpandedStage();
	const snapshot = window.activityFixture.snapshot();
	snapshot.sections.unshift({ id: 'future', kind: 'unavailable', title: 'Unknown', badge: 10 });
	window.activityFixture.send(snapshot);
	await flushPromises();
	expect(wrapper.findAll('[role="tab"]')).toHaveLength(0);
	expect(wrapper.findAll('.task-row')).toHaveLength(3);
	await wrapper.find('.task-select').trigger('click');
	await flushPromises();
	window.activityFixture.holdResponses(true);
	await wrapper.find('.content-links button').trigger('click');
	await flushPromises();
	await wrapper.findAll('.task-select')[1].trigger('click');
	await flushPromises();
	window.activityFixture.release(1);
	await flushPromises();
	window.activityFixture.release(0);
	await flushPromises();
	expect(wrapper.find('.task-detail header').text()).toContain('Reviewer 2');
	expect(wrapper.find('.content-window').exists()).toBe(false);
});
