import { afterEach, expect, it } from 'vitest';
import { mount } from '@vue/test-utils';
import ComposerPanel from '../src/components/chat/composer/ComposerPanel.vue';
import ConversationQueue from '../src/components/chat/ConversationQueue.vue';

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

	const disabled = attach(ConversationQueue, { state: queueState({ canSteer: false, steerReason: 'skill-requires-new-turn', items: [followUp()] }) });
	expect(disabled.find('[aria-label="调整当前任务"]').attributes('disabled')).toBeDefined();
	expect(disabled.text()).toContain('新回合');
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

it('never mounts the card for a paused queue that has nothing waiting', () => {
	// 回合失败后宿主会把队列落库为 paused，而本轮输入已消费，快照里只剩 pauseReason。
	const idle = attach(ConversationQueue, {
		state: queueState({ paused: true, pauseReason: 'turn-failed', items: [] }),
	});
	expect(idle.find('.conversation-queue').exists()).toBe(false);
	expect(idle.text()).not.toContain('上一项执行失败');

	// 只剩本轮 claimed 项同样不算等待：它由转录呈现，卡片不该凭空出现。
	const claimedOnly = attach(ConversationQueue, {
		state: queueState({ paused: true, pauseReason: 'turn-failed', items: [followUp({ status: 'claimed' })] }),
	});
	expect(claimedOnly.find('.conversation-queue').exists()).toBe(false);

	// 暂停 + 等待项仍然要说明原因并给出恢复入口。
	const waiting = attach(ConversationQueue, {
		state: queueState({ paused: true, pauseReason: 'turn-failed', items: [followUp()] }),
	});
	expect(waiting.find('.conversation-queue').exists()).toBe(true);
	expect(waiting.text()).toContain('上一项执行失败');
	expect(waiting.find('[aria-label="恢复队列"]').exists()).toBe(true);

	// 宿主侧操作错误仍需就地显示，即使没有任何等待项。
	const failed = attach(ConversationQueue, {
		state: queueState({ paused: true, pauseReason: 'turn-failed', items: [], error: '队列操作未生效。' }),
	});
	expect(failed.find('.conversation-queue').exists()).toBe(true);
	expect(failed.text()).toContain('队列操作未生效。');
	expect(failed.find('.queue-list').exists()).toBe(false);
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

it('keeps the edit confirmation and the pause/resume control independent', async () => {
	const wrapper = attach(ConversationQueue, { state: queueState({ items: [followUp()] }), notice: '已更新待发送内容' });
	expect(wrapper.find('[role="status"]').text()).toContain('已更新待发送内容');
	await wrapper.find('[aria-label="暂停队列"]').trigger('click');
	expect(wrapper.emitted('pause')).toHaveLength(1);

	const paused = attach(ConversationQueue, { state: queueState({ paused: true, pauseReason: 'queue-paused', items: [followUp()] }) });
	await paused.find('[aria-label="恢复队列"]').trigger('click');
	expect(paused.emitted('resume')).toHaveLength(1);
});

it('keeps the remaining rows when one of three waiting inputs is consumed', async () => {
	const items = [
		followUp({ inputId: 'i-1', sequence: 1, preview: 'first' }),
		followUp({ inputId: 'i-2', sequence: 2, preview: 'promote me', kind: 'steer' }),
		followUp({ inputId: 'i-3', sequence: 3, preview: 'third' }),
	];
	const wrapper = attach(ConversationQueue, {
		state: queueState({ canSteer: true, steerTargetTurnId: 'turn-1', items }),
	});
	expect(wrapper.findAll('.queue-item')).toHaveLength(3);
	expect(wrapper.find('.queue-item[data-kind="steer"]').text()).toContain('等待生效');

	// 消费的是中间那条：卡片不卸载，其余两条保持原顺序，也不留任何提示。
	await wrapper.setProps({
		state: queueState({ canSteer: true, steerTargetTurnId: 'turn-1', items: [items[0], items[2]] }),
	});
	expect(wrapper.findAll('.queue-item')).toHaveLength(2);
	expect(wrapper.findAll('.queue-preview').map(node => node.text())).toEqual(['first', 'third']);
	expect(wrapper.find('.queue-title').text()).toContain('· 2');
	expect(wrapper.find('.queue-notice').exists()).toBe(false);
});

it('unmounts the card at once when the last waiting input is consumed', () => {
	// 消费后的快照里既没有条目也没有错误：卡片必须整卡消失，不能靠任何提示文案继续挂载。
	const empty = attach(ConversationQueue, {
		state: queueState({ items: [], paused: false, error: '' }),
		notice: '已更新待发送内容',
	});
	expect(empty.find('.conversation-queue').exists()).toBe(false);
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
