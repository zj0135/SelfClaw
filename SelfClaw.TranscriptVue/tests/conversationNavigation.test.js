import { afterEach, expect, it, vi } from 'vitest';
import { mount } from '@vue/test-utils';
import { defineComponent, ref } from 'vue';

const { bridge, pushTranscript } = vi.hoisted(() => {
	const handlers = new Map();
	const subscribe = (type, handler) => {
		if (!handlers.has(type)) handlers.set(type, new Set());
		handlers.get(type).add(handler);
		return () => handlers.get(type)?.delete(handler);
	};
	return {
		bridge: {
			request: vi.fn(async () => ({})),
			requestLatest: vi.fn(async () => ({})),
			on: vi.fn(subscribe),
			post: vi.fn(),
			hasHost: () => true,
		},
		pushTranscript: (payload) => {
			for (const handler of [...(handlers.get(payload.type) ?? [])]) handler(payload);
		},
	};
});
vi.mock('../src/composables/hostBridge.js', () => ({
	hostBridge: bridge,
	useHostBridge: () => bridge,
	isSuperseded: () => false,
}));

import { useConversationNavigation } from '../src/composables/useConversationNavigation.js';
import { useChatTranscript } from '../src/composables/useChatTranscript.js';

// 侧栏导航与转录面板共处一个组件树：两者必须看到同一份 pending / 选中态。
function mountNavigation() {
	const harness = defineComponent({
		setup() {
			const currentViewId = ref('chat');
			const chatViewRef = ref(null);
			const navigation = useConversationNavigation(currentViewId, chatViewRef, () => {});
			const { isLoadingHistory } = useChatTranscript({
				captureBeforeUpdate: () => null,
				settleAfterUpdate: () => {},
			});
			return { navigation, isLoadingHistory };
		},
		template: '<div />',
	});
	const wrapper = mount(harness);
	return { wrapper, isLoading: () => wrapper.vm.isLoadingHistory };
}

// 转录桥按单调 revision 收敛推送：跨用例必须一直往上走，否则会被当成过期快照丢掉。
let revision = 0;

function transcriptPush(selectedConversationId) {
	revision += 1;
	pushTranscript({
		type: 'replaceState',
		revision,
		selectedConversationId,
		items: [{ id: `message-${revision}`, role: 'user', kind: 'message', text: 'hi' }],
		conversations: [
			{ id: 'conv-1', title: '第一个会话' },
			{ id: 'conv-2', title: '第二个会话' },
		],
	});
}

afterEach(() => {
	bridge.request.mockReset();
	bridge.request.mockResolvedValue({});
});

// 侧栏顶栏的「新建对话」要沿用当前工作区，避免在同一工作区里连开会话时反复重选。
it('keeps the current workspace when the sidebar top button starts a new chat', async () => {
	const { wrapper } = mountNavigation();

	await wrapper.vm.navigation.onSidebarAction('new-chat');

	expect(bridge.request).toHaveBeenCalledWith('new-chat', { keepWorkspace: true });
	wrapper.unmount();
});

// 「对话」分组标题上的加号保持旧行为：清空工作区选择、回退到默认工作目录。
it('clears the workspace when the conversation group plus starts a new chat', async () => {
	const { wrapper } = mountNavigation();

	await wrapper.vm.navigation.onSidebarAction('add-conversations');

	expect(bridge.request).toHaveBeenCalledWith('new-chat', {});
	wrapper.unmount();
});

// 归档分组排在「对话」之后：它是时间推导出来的分组，既没有加号也没有自己的 action。
it('exposes the archive group after conversations with the folder layout and no create action', () => {
	const { wrapper } = mountNavigation();

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

// 重复点击已选中的会话：宿主短路、不推快照，前端不能因此卡在「正在载入历史对话」。
it('does not request a switch or leave a pending selection when the selected conversation is clicked again', async () => {
	const { wrapper, isLoading } = mountNavigation();
	transcriptPush('conv-1');

	await wrapper.vm.navigation.onSidebarSelect('conv-1');

	expect(bridge.request).not.toHaveBeenCalled();
	expect(isLoading()).toBe(false);
	wrapper.unmount();
});

// 宿主没推快照（短路、或推送被丢掉）时，请求一结束就必须撤掉 pending，不能无尽等待。
it('releases the pending selection when the host answers without a transcript push', async () => {
	const { wrapper, isLoading } = mountNavigation();
	transcriptPush('conv-1');

	await wrapper.vm.navigation.onSidebarSelect('conv-2');

	expect(bridge.request).toHaveBeenCalledWith('select-conversation', { conversationId: 'conv-2' });
	expect(isLoading()).toBe(false);
	wrapper.unmount();
});

// 正常切换期间仍然要显示骨架：pending 只在请求在途时有效。
it('keeps the skeleton while a real switch is still in flight', async () => {
	const { wrapper, isLoading } = mountNavigation();
	transcriptPush('conv-1');
	let release;
	bridge.request.mockImplementation(() => new Promise((resolve) => {
		release = resolve;
	}));

	const switching = wrapper.vm.navigation.onSidebarSelect('conv-2');
	expect(isLoading()).toBe(true);

	release({});
	await switching;
	expect(isLoading()).toBe(false);
	wrapper.unmount();
});

// 双击同一条会话不能变成两次 select-conversation：第二次点击落在同一次在途切换上。
it('collapses a double click on the same conversation into one select request', async () => {
	const { wrapper } = mountNavigation();
	transcriptPush('conv-1');
	let release;
	bridge.request.mockImplementation(() => new Promise((resolve) => {
		release = resolve;
	}));

	const first = wrapper.vm.navigation.onSidebarSelect('conv-2');
	const second = wrapper.vm.navigation.onSidebarSelect('conv-2');
	release({});
	await Promise.all([first, second]);

	expect(bridge.request).toHaveBeenCalledTimes(1);
	wrapper.unmount();
});
