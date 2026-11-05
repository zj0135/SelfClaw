import { afterEach, expect, it, vi } from 'vitest';
import { mount } from '@vue/test-utils';
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

import { useConversationNavigation } from '../src/composables/useConversationNavigation.js';

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
	bridge.request.mockClear();
});

// 侧栏顶栏的「新建对话」要沿用当前工作区，避免在同一工作区里连开会话时反复重选。
it('keeps the current workspace when the sidebar top button starts a new chat', async () => {
	const wrapper = mountNavigation();

	await wrapper.vm.navigation.onSidebarAction('new-chat');

	expect(bridge.request).toHaveBeenCalledWith('new-chat', { keepWorkspace: true });
	wrapper.unmount();
});

// 「对话」分组标题上的加号保持旧行为：清空工作区选择、回退到默认工作目录。
it('clears the workspace when the conversation group plus starts a new chat', async () => {
	const wrapper = mountNavigation();

	await wrapper.vm.navigation.onSidebarAction('add-conversations');

	expect(bridge.request).toHaveBeenCalledWith('new-chat', {});
	wrapper.unmount();
});

// 归档分组排在「对话」之后：它是时间推导出来的分组，既没有加号也没有自己的 action。
it('exposes the archive group after conversations with the folder layout and no create action', () => {
	const wrapper = mountNavigation();

	const groups = wrapper.vm.navigation.navItems.value.filter((item) => item.type === 'group');

	expect(groups.map((group) => group.id)).toEqual(['projects', 'conversations', 'archived']);
	expect(groups[2]).toMatchObject({
		label: '归档',
		layout: 'folders',
		canCreate: false,
		emptyText: '暂无归档会话',
		children: [],
	});
	wrapper.unmount();
});
