import { afterEach, expect, it, vi } from 'vitest';
import { flushPromises, mount } from '@vue/test-utils';
import { defineComponent, ref } from 'vue';

const { bridge } = vi.hoisted(() => ({
	bridge: {
		request: vi.fn(async () => ({})),
		requestLatest: vi.fn(async () => ({})),
		on: vi.fn(() => () => {}),
		post: vi.fn(),
		hasHost: () => true,
	},
}));
vi.mock('../src/composables/hostBridge.js', () => ({
	hostBridge: bridge,
	useHostBridge: () => bridge,
	isSuperseded: () => false,
}));

import GitWorkspaceControl from '../src/components/Chat/GitWorkspaceControl.vue';
import { useConversationNavigation } from '../src/composables/useConversationNavigation.js';
import { useConfirm } from '../src/composables/useConfirm.js';

const { confirmState, resolveConfirm } = useConfirm();

function mountNavigation() {
	const harness = defineComponent({
		setup() {
			const currentViewId = ref('chat');
			const chatViewRef = ref(null);
			return { navigation: useConversationNavigation(currentViewId, chatViewRef, () => {}) };
		},
		template: '<div />',
	});
	return mount(harness);
}

afterEach(() => {
	resolveConfirm(false);
	bridge.request.mockClear();
	document.body.innerHTML = '';
});

it('deletes a managed-worktree conversation only after both global confirmations', async () => {
	const wrapper = mountNavigation();
	const conversationId = '11111111-1111-1111-1111-111111111111';
	const deleting = wrapper.vm.navigation.onSidebarAction({
		id: 'delete-conversation',
		conversationId,
		isManagedWorktree: true,
	});
	await flushPromises();

	// 第一个弹框：删会话，尚未发出请求。
	expect(confirmState.open).toBe(true);
	expect(confirmState.title).toBe('删除会话');
	bridge.request.mockClear();
	expect(bridge.request).not.toHaveBeenCalled();

	resolveConfirm(true);
	await flushPromises();

	// 第二个弹框：是否同时移除工作树。
	expect(confirmState.open).toBe(true);
	expect(confirmState.title).toBe('移除工作树');
	expect(bridge.request).not.toHaveBeenCalled();

	resolveConfirm(true);
	await deleting;
	await flushPromises();

	expect(bridge.request).toHaveBeenCalledWith('delete-conversation', { conversationId, removeManagedWorktree: true });
	expect(confirmState.open).toBe(false);
	wrapper.unmount();
});

it('drops the deletion when the first confirmation is cancelled', async () => {
	const wrapper = mountNavigation();
	const deleting = wrapper.vm.navigation.onSidebarAction({
		id: 'delete-conversation',
		conversationId: '22222222-2222-2222-2222-222222222222',
		isManagedWorktree: true,
	});
	await flushPromises();

	resolveConfirm(false);
	await deleting;

	expect(bridge.request).not.toHaveBeenCalled();
	expect(confirmState.open).toBe(false);
	wrapper.unmount();
});

it('keeps a non-worktree conversation on the single-prompt path', async () => {
	const wrapper = mountNavigation();
	const conversationId = '33333333-3333-3333-3333-333333333333';
	const deleting = wrapper.vm.navigation.onSidebarAction({
		id: 'delete-conversation',
		conversationId,
		isManagedWorktree: false,
	});
	await flushPromises();

	expect(confirmState.open).toBe(false);
	await deleting;
	expect(bridge.request).toHaveBeenCalledWith('delete-conversation', { conversationId, removeManagedWorktree: false });
	wrapper.unmount();
});

it('deletes a git branch only after the global dialog is confirmed', async () => {
	const wrapper = mount(GitWorkspaceControl, {
		props: {
			state: {
				repositoryName: 'repo',
				branchName: 'main',
				branches: [{ name: 'feature', fullName: 'refs/heads/feature', isCurrent: false, isRemote: false }],
				worktrees: [],
			},
		},
	});

	await wrapper.get('.git-trigger').trigger('click');
	await wrapper.get('.branch-delete').trigger('click');

	expect(confirmState.open).toBe(true);
	expect(confirmState.title).toBe('删除分支');
	expect(wrapper.emitted('action')).toEqual([[{ type: 'refresh' }]]);

	resolveConfirm(true);
	await flushPromises();

	expect(wrapper.emitted('action')).toEqual([
		[{ type: 'refresh' }],
		[{ type: 'delete-branch', branchName: 'feature' }],
	]);
	wrapper.unmount();
});
