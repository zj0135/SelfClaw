import { afterEach, expect, it } from 'vitest';
import { mount } from '@vue/test-utils';
import ComposerPanel from '../src/components/Chat/ComposerPanel.vue';
import ConversationQueue from '../src/components/Chat/ConversationQueue.vue';

const mounted = [];
afterEach(() => mounted.splice(0).forEach((wrapper) => wrapper.unmount()));

function attach(component, props) {
	const wrapper = mount(component, {
		props,
		global: {
			stubs: {
				ComposerStatusBar: true, ComposerAddMenu: true, AgentSelector: true,
				ModelSelector: true, PermissionSelector: true,
			},
		},
	});
	mounted.push(wrapper);
	return wrapper;
}

const queueState = (overrides = {}) => ({
	conversationId: 'A', subscriptionId: 's1', queueRevision: 2, paused: false, pauseReason: null,
	canSteer: false, steerReason: null, steerTargetTurnId: null, items: [], loaded: true, truncated: false, error: '',
	...overrides,
});

const followUp = (overrides = {}) => ({
	inputId: 'i-1', sequence: 1, kind: 'follow-up', status: 'pending', revision: 1, preview: 'queued body',
	reasonCode: null, errorMessage: null, ...overrides,
});

it('offers edit, promote and cancel for a pending follow-up only when the host allows steering', () => {
	const enabled = attach(ConversationQueue, { state: queueState({ canSteer: true, steerTargetTurnId: 'turn-1', items: [followUp()] }) });
	expect(enabled.find('[aria-label="编辑"]').exists()).toBe(true);
	expect(enabled.find('[aria-label="重试"]').exists()).toBe(false);
	const promote = enabled.find('[aria-label="调整当前任务"]');
	expect(promote.exists()).toBe(true);
	expect(promote.attributes('disabled')).toBeUndefined();
	expect(enabled.find('[aria-label="取消"]').exists()).toBe(true);
	expect(enabled.text()).toContain('待发送');

	const disabled = attach(ConversationQueue, { state: queueState({ canSteer: false, steerReason: 'hook-requires-new-turn', items: [followUp()] }) });
	expect(disabled.find('[aria-label="调整当前任务"]').attributes('disabled')).toBeDefined();
	expect(disabled.text()).toContain('完整审查');
});

it('shows Held items as blocking with their host reason and a retry path', () => {
	const wrapper = attach(ConversationQueue, {
		state: queueState({ paused: true, pauseReason: 'turn-failed', items: [
			followUp({ inputId: 'held-1', status: 'held', reasonCode: 'model-disabled' })] }),
	});
	const row = wrapper.find('.queue-item[data-status="held"]');
	expect(row.exists()).toBe(true);
	expect(row.text()).toContain('所选模型不可用');
	expect(row.find('[aria-label="重试"]').exists()).toBe(true);
	expect(wrapper.text()).toContain('上一项执行失败');
});

it('labels a pending steer as waiting and never offers queue conversion actions', () => {
	const wrapper = attach(ConversationQueue, {
		state: queueState({ canSteer: true, steerTargetTurnId: 'turn-1', items: [
			followUp({ inputId: 's-1', kind: 'steer', preview: 'adjust now' })] }),
	});
	const row = wrapper.find('.queue-item[data-kind="steer"]');
	expect(row.text()).toContain('调整');
	expect(row.text()).toContain('等待生效');
	expect(row.find('[aria-label="调整当前任务"]').exists()).toBe(false);
	expect(row.find('[aria-label="编辑"]').exists()).toBe(false);
	expect(row.find('[aria-label="取消"]').exists()).toBe(true);
});

it('hides a claimed input from the card while keeping it out of the rendered list', () => {
	const wrapper = attach(ConversationQueue, {
		state: queueState({ items: [
			followUp({ inputId: 'run-1', status: 'claimed', preview: 'executing now' }),
			followUp({ inputId: 'q-1', preview: 'still waiting' }),
		] }),
	});
	expect(wrapper.find('.queue-item[data-status="claimed"]').exists()).toBe(false);
	expect(wrapper.findAll('.queue-item')).toHaveLength(1);
	expect(wrapper.text()).not.toContain('执行中');
	expect(wrapper.text()).toContain('still waiting');
	// The count reflects only the waiting items.
	expect(wrapper.find('.queue-title').text()).toContain('· 1');
});

it('renders the edit draft from the composable and saves without clearing it', async () => {
	const wrapper = attach(ConversationQueue, {
		state: queueState({ items: [followUp()] }),
		editing: { inputId: 'i-1', revision: 4, prompt: 'draft kept', saving: false },
	});
	const editor = wrapper.find('textarea[aria-label="编辑待发送内容"]');
	expect(editor.element.value).toBe('draft kept');
	await editor.setValue('draft kept and changed');
	expect(wrapper.emitted('update:prompt').at(-1)).toEqual(['draft kept and changed']);
	await wrapper.find('[aria-label="保存编辑"]').trigger('click');
	await wrapper.find('[aria-label="取消编辑"]').trigger('click');
	expect(wrapper.emitted('save-edit')).toHaveLength(1);
	expect(wrapper.emitted('cancel-edit')).toHaveLength(1);
});

it('shows the consumed steer notice and keeps the pause/resume control independent', async () => {
	const wrapper = attach(ConversationQueue, { state: queueState({ items: [followUp()] }), notice: '已加入上下文' });
	expect(wrapper.find('[role="status"]').text()).toContain('已加入上下文');
	await wrapper.find('[aria-label="暂停队列"]').trigger('click');
	expect(wrapper.emitted('pause')).toHaveLength(1);

	const paused = attach(ConversationQueue, { state: queueState({ paused: true, pauseReason: 'queue-paused', items: [followUp()] }) });
	await paused.find('[aria-label="恢复队列"]').trigger('click');
	expect(paused.emitted('resume')).toHaveLength(1);
});

it('toggles the single button between stop and send while a Direct turn is busy', async () => {
	const wrapper = attach(ComposerPanel, {
		busy: true, queueable: true, stoppable: true,
		pendingApproval: { toolExecutionId: 'tool-1', displayName: 'Write file' },
	});
	expect(wrapper.find('.tool-approval-bar').exists()).toBe(true);

	// Empty composer: only the stop button is present.
	expect(wrapper.find('[aria-label="停止生成"]').exists()).toBe(true);
	expect(wrapper.find('[aria-label="发送"]').exists()).toBe(false);

	// Typing turns the same slot into the send (queue) button.
	await wrapper.find('textarea').setValue('adjust the current task');
	expect(wrapper.find('[aria-label="停止生成"]').exists()).toBe(false);
	expect(wrapper.find('[aria-label="发送"]').exists()).toBe(true);

	await wrapper.find('[aria-label="发送"]').trigger('click');
	expect(wrapper.emitted('submit')).toHaveLength(1);
	expect(wrapper.emitted('submit')[0][0].prompt).toBe('adjust the current task');

	// Clearing the draft brings the stop button back.
	await wrapper.find('textarea').setValue('   ');
	expect(wrapper.find('[aria-label="停止生成"]').exists()).toBe(true);
	expect(wrapper.find('[aria-label="发送"]').exists()).toBe(false);
});

it('never turns the CLI stop button into a submit and keeps steering out of the composer', async () => {
	const wrapper = attach(ComposerPanel, {
		busy: true, queueable: false, stoppable: true,
	});
	await wrapper.find('textarea').setValue('cli busy text');
	expect(wrapper.find('[aria-label="发送"]').exists()).toBe(false);
	expect(wrapper.find('[aria-label="调整当前任务"]').exists()).toBe(false);
	expect(wrapper.find('[aria-label="停止生成"]').exists()).toBe(true);
});

it('shows only progress for a busy background continuation', () => {
	const wrapper = attach(ComposerPanel, { busy: true, queueable: true, stoppable: false });
	expect(wrapper.find('[aria-label="发送"]').exists()).toBe(false);
	expect(wrapper.find('[aria-label="停止生成"]').exists()).toBe(false);
	expect(wrapper.find('[aria-label="正在处理子代理结果"]').exists()).toBe(true);
});

it('keeps the normal send action for an idle composer', async () => {
	const wrapper = attach(ComposerPanel, { busy: false, queueable: true });
	expect(wrapper.find('[aria-label="发送"]').exists()).toBe(true);
	await wrapper.find('textarea').setValue('ordinary');
	await wrapper.find('[aria-label="发送"]').trigger('click');
	expect(wrapper.emitted('submit')[0][0].prompt).toBe('ordinary');
});
