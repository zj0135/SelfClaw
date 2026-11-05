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

const ids = () => window.activityFixture.ids;

async function mountPane(props = {}) {
	const { default: Pane } = await import('../src/components/dock/SubagentDockPane.vue');
	return mount(Pane, { props: { parentConversationId: window.activityFixture.parent, visible: true, ...props } });
}

async function mountVisiblePane(props = {}) {
	wrapper = await mountPane(props);
	await flushPromises();
	return wrapper;
}

// dock 收起/切走 ⇔ visible=false：详情清掉但阅读位置留在偏好里，再可见时恢复。
async function setVisible(visible) {
	await wrapper.setProps({ visible });
	await flushPromises();
}

const select = () => wrapper.find('select.task-select');
// 本页任务的选项；选中任务不在本页时会多出一个 data-missing 占位项，不计入。
const options = () => select().findAll('option[data-status]:not([data-missing])');
const detailTaskId = () => wrapper.find('.task-detail').attributes('data-task-id');

// 下拉框选任务：setValue 触发 change → selectTask。
async function choose(index) {
	await select().setValue(ids()[index]);
	await flushPromises();
}

it('lists task names in the dropdown, auto-selects the first one, shows only the run time outside, and reports presence', async () => {
	wrapper = await mountVisiblePane();
	expect(options()).toHaveLength(3);
	expect(options()[0].text()).toBe('Reviewer 1');
	expect(select().element.value).toBe(ids()[0]);
	expect(detailTaskId()).toBe(ids()[0]);
	expect(wrapper.find('.task-status').exists()).toBe(false);
	expect(options()[0].attributes('data-phase')).toBe('thinking');
	expect(wrapper.find('.task-timing').text()).toMatch(/^运行 \d+s$/);
	const { useSideDock } = await import('../src/composables/useSideDock.js');
	expect(useSideDock().state.subagents).toEqual({ total: 3, running: 3 });
	window.activityFixture.finish();
	await flushPromises();
	expect(options()[0].attributes('data-phase')).toBe('succeeded');
	expect(useSideDock().state.subagents).toEqual({ total: 3, running: 2 });
});

it('shows the onboarding empty state without a conversation', async () => {
	wrapper = await mountPane({ parentConversationId: null });
	await flushPromises();
	expect(wrapper.find('.pane-empty').text()).toContain('开始一个会话');
	expect(wrapper.find('.task-detail').exists()).toBe(false);
	expect(select().attributes('disabled')).toBeDefined();
});

it('does not auto-select while hidden and restores the remembered task when shown again', async () => {
	wrapper = await mountPane({ visible: false });
	await flushPromises();
	expect(options()).toHaveLength(3);
	expect(wrapper.find('.task-detail').exists()).toBe(false);
	await setVisible(true);
	expect(detailTaskId()).toBe(ids()[0]);
	await choose(1);
	expect(detailTaskId()).toBe(ids()[1]);
	await setVisible(false);
	expect(wrapper.find('.task-detail').exists()).toBe(false);
	await setVisible(true);
	expect(detailTaskId()).toBe(ids()[1]);
});

it('applies and ACKs the correlated first snapshot, streams shared blocks, and cancels the selected child', async () => {
	wrapper = await mountVisiblePane();
	expect(window.activityFixture.requests.some((request) => request.type === 'activity-panel/rendered')).toBe(true);
	expect(wrapper.find('.thinking-summary').exists()).toBe(true);
	expect(wrapper.find('.thinking-content').text()).toContain('Reasoning for');
	await wrapper.find('.tool-summary').trigger('click');
	window.activityFixture.text(' grows');
	await flushPromises();
	await vi.waitFor(() => expect(wrapper.find('.body-segment').text()).toContain('grows'));
	window.activityFixture.finish();
	await flushPromises();
	expect(wrapper.find('.tool-block').classes()).toContain('completed');
	expect(wrapper.find('.tool-details').text()).toContain('Recorded tool result');
	expect(wrapper.find('.task-cancel').exists()).toBe(false);
	await choose(1);
	// 停止键固定在工具条右侧、刷新键左边。
	expect(wrapper.find('.pane-toolbar').findAll('.task-cancel, .pane-refresh').map((button) => button.classes()[0]))
		.toEqual(['task-cancel', 'pane-refresh']);
	await wrapper.find('.task-cancel').trigger('click');
	await flushPromises();
	expect(select().findAll('option[data-status="cancelled"]')).toHaveLength(1);
	expect(wrapper.find('.task-cancel').exists()).toBe(false);
	// 切换父会话：新订阅自己拿到任务并自动选中第一项；旧订阅的快照既不被应用也不会被 ACK。
	const old = window.activityFixture.snapshot();
	const acks = () => window.activityFixture.requests.filter((request) => request.type === 'activity-panel/rendered').length;
	await wrapper.setProps({ parentConversationId: 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb' });
	await flushPromises();
	expect(options()).toHaveLength(3);
	expect(detailTaskId()).toBe(ids()[0]);
	const before = acks();
	window.activityFixture.send(old);
	await flushPromises();
	expect(acks()).toBe(before);
	expect(detailTaskId()).toBe(ids()[0]);
});

it('preserves thinking expansion after switching tasks and does not nest buttons', async () => {
	wrapper = await mountVisiblePane();
	expect(wrapper.find('.thinking-content').exists()).toBe(true);
	await wrapper.find('.thinking-summary').trigger('click');
	expect(wrapper.find('.thinking-content').exists()).toBe(false);
	await choose(1);
	expect(wrapper.find('.thinking-content').exists()).toBe(true);
	await choose(0);
	expect(wrapper.find('.thinking-content').exists()).toBe(false);
	expect(wrapper.findAll('button button')).toHaveLength(0);
});

it('retains invalidated historical content, resumes live, and restores selection after hiding and remount', async () => {
	wrapper = await mountVisiblePane();
	const invalidated = window.activityFixture.snapshot();
	delete invalidated.sections[0].detail;
	invalidated.sections[0].detailError = 'content-changed';
	invalidated.sections[0].selectedTask = { ...window.activityFixture.tasks[0], status: 'succeeded', phase: 'succeeded', deliveryStatus: 'pending', outputTokens: 123 };
	window.activityFixture.send(invalidated);
	await flushPromises();
	expect(options()).toHaveLength(3);
	await vi.waitFor(() => expect(wrapper.find('.body-segment').text()).toContain('Partial answer'));
	expect(wrapper.find('.detail-meta').text()).toContain('已完成');
	expect(wrapper.find('.detail-meta').text()).toContain('输出 123');
	expect(wrapper.find('.detail-meta').text()).toContain('等待父代理处理');
	await wrapper.find('.new-content').trigger('click');
	await flushPromises();
	expect(wrapper.find('.new-content').exists()).toBe(false);
	await setVisible(false);
	window.activityFixture.text(' while hidden');
	await flushPromises();
	expect(wrapper.find('.task-detail').exists()).toBe(false);
	await setVisible(true);
	await vi.waitFor(() => expect(wrapper.find('.body-segment').text()).toContain('while hidden'));
	wrapper.unmount();
	wrapper = await mountVisiblePane();
	await vi.waitFor(() => expect(wrapper.find('.body-segment').text()).toContain('while hidden'));
});

it('restores a long historical window after task switching, hiding, and remount', async () => {
	window.activityFixture.longContent();
	wrapper = await mountVisiblePane();
	expect(wrapper.findAll('.body-segment')[0].text()).toContain('History block 86');
	await wrapper.find('.window-link').trigger('click');
	await flushPromises();
	expect(wrapper.findAll('.body-segment')[0].text()).toContain('History block 22');
	await choose(1);
	await choose(0);
	expect(wrapper.findAll('.body-segment')[0].text()).toContain('History block 22');
	await setVisible(false);
	window.activityFixture.appendBlock('Output while hidden');
	await setVisible(true);
	expect(wrapper.findAll('.body-segment')[0].text()).toContain('History block 22');
	expect(wrapper.find('.new-content').exists()).toBe(true);
	wrapper.unmount();
	wrapper = await mountVisiblePane();
	expect(wrapper.findAll('.body-segment')[0].text()).toContain('History block 22');
	const request = window.activityFixture.requests.filter((item) => item.type === 'activity-panel/select-detail').at(-1);
	expect(request).toMatchObject({ blockOffset: 22, contentVersion: '2' });
});

it('returns from an invalidated historical window to the tail and follows subsequent output', async () => {
	window.activityFixture.longContent();
	wrapper = await mountVisiblePane();
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
	wrapper = await mountVisiblePane();
	window.activityFixture.push();
	await flushPromises();
	expect(options()).toHaveLength(3);
	window.activityFixture.release();
	await flushPromises();
	await choose(1);
	window.activityFixture.release(1);
	await flushPromises();
	window.activityFixture.release(0, 'activity-subscription-changed');
	await flushPromises();
	expect(detailTaskId()).toBe(ids()[1]);
	expect(wrapper.findAll('[role="alert"]')).toHaveLength(0);
	expect(wrapper.find('.detail-state').exists()).toBe(false);
});

it('keeps the confirmed page on failure and resets an invalidated cursor without losing the selection', async () => {
	window.activityFixture.setTaskCount(53);
	wrapper = await mountVisiblePane();
	expect(detailTaskId()).toBe(ids()[0]);
	window.activityFixture.holdResponses(true);
	await wrapper.find('[aria-label="下一页任务"]').trigger('click');
	await flushPromises();
	expect(wrapper.find('.task-picker').attributes('aria-busy')).toBe('true');
	expect(options()).toHaveLength(50);
	window.activityFixture.release(0, 'temporary-read-failure');
	await flushPromises();
	expect(wrapper.find('.task-pager span').text()).toBe('1');
	expect(wrapper.find('[aria-label="上一页任务"]').attributes('disabled')).toBeDefined();
	expect(wrapper.find('.task-picker').attributes('aria-busy')).toBe('false');
	window.activityFixture.holdResponses(false);
	await wrapper.find('[aria-label="下一页任务"]').trigger('click');
	await flushPromises();
	expect(options()).toHaveLength(3);
	expect(wrapper.find('option[data-missing]').exists()).toBe(true);
	expect(select().element.value).toBe(ids()[0]);
	expect(wrapper.find('.task-pager span').text()).toBe('2');
	window.activityFixture.resetTaskPage();
	await flushPromises();
	expect(options()).toHaveLength(50);
	expect(wrapper.find('option[data-missing]').exists()).toBe(false);
	expect(wrapper.find('.task-pager span').text()).toBe('1');
	expect(detailTaskId()).toBe(ids()[0]);
});

it('ignores an unknown section and keeps the current selection', async () => {
	wrapper = await mountVisiblePane();
	const snapshot = window.activityFixture.snapshot();
	snapshot.sections.unshift({ id: 'future', kind: 'unavailable', title: 'Unknown', badge: 10 });
	window.activityFixture.send(snapshot);
	await flushPromises();
	expect(options()).toHaveLength(3);
	expect(detailTaskId()).toBe(ids()[0]);
});
