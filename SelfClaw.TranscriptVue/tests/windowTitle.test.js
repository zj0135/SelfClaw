import { afterEach, describe, expect, it, vi } from 'vitest';
import { mount } from '@vue/test-utils';
import { defineComponent, ref } from 'vue';

// 顶栏标题的来源就是宿主推下来的导航列表：这里用同一个 bridge 桩把「推送 → 标题」串起来测，
// 不另建一套假数据通道，否则测的就不是真实取数路径了。
const { bridge, transcriptListeners } = vi.hoisted(() => ({
	bridge: {
		request: vi.fn(async () => ({})),
		requestLatest: vi.fn(async () => ({})),
		on: vi.fn(() => () => {}),
		post: vi.fn(),
		hasHost: () => true,
	},
	transcriptListeners: [],
}));

vi.mock('../src/composables/hostBridge.js', () => ({
	hostBridge: bridge,
	useHostBridge: () => bridge,
	isSuperseded: () => false,
}));

vi.mock('../src/composables/transcriptBridge.js', () => ({
	useTranscriptBridge: () => ({
		on: (handler) => {
			transcriptListeners.push(handler);
			return () => {};
		},
	}),
}));

import { useConversationNavigation } from '../src/composables/useConversationNavigation.js';

afterEach(() => {
	transcriptListeners.length = 0;
	bridge.request.mockClear();
});

function mountNavigation(viewId = 'chat') {
	const harness = defineComponent({
		setup() {
			const currentViewId = ref(viewId);
			const chatViewRef = ref(null);
			return { currentViewId, navigation: useConversationNavigation(currentViewId, chatViewRef, () => {}) };
		},
		template: '<div />',
	});
	return mount(harness);
}

function push(state) {
	transcriptListeners[transcriptListeners.length - 1]({
		conversations: [
			{ id: 'conv-a', title: '排查压缩阈值异常' },
			{ id: 'conv-b', title: '生图模型能力参数' },
		],
		...state,
	});
}

describe('window title conversation name', () => {
	it('follows the selected conversation title from the host navigation list', () => {
		const wrapper = mountNavigation();
		push({ selectedConversationId: 'conv-a' });
		expect(wrapper.vm.navigation.activeConversationTitle.value).toBe('排查压缩阈值异常');

		push({ selectedConversationId: 'conv-b' });
		expect(wrapper.vm.navigation.activeConversationTitle.value).toBe('生图模型能力参数');
		wrapper.unmount();
	});

	// 自动总结与重命名都只改列表里的 title（同一个 id），标题必须跟着走。
	it('tracks a renamed conversation without the selection changing', () => {
		const wrapper = mountNavigation();
		push({ selectedConversationId: 'conv-a' });
		transcriptListeners[transcriptListeners.length - 1]({
			conversations: [{ id: 'conv-a', title: 'Summarized title' }],
			selectedConversationId: 'conv-a',
		});
		expect(wrapper.vm.navigation.activeConversationTitle.value).toBe('Summarized title');
		wrapper.unmount();
	});

	// 新对话没有会话 ID；独立视图不承载会话。两种情况都必须留空，不能残留上一条标题。
	it('is empty for a new chat and for standalone views', () => {
		const wrapper = mountNavigation();
		push({ selectedConversationId: 'conv-a' });
		push({ selectedConversationId: null });
		expect(wrapper.vm.navigation.activeConversationTitle.value).toBe('');

		push({ selectedConversationId: 'conv-a' });
		wrapper.vm.currentViewId = 'settings';
		expect(wrapper.vm.navigation.activeConversationTitle.value).toBe('');
		wrapper.unmount();
	});

	// 选中会话还没进入列表（切换途中的过渡帧）时不能拼出一个 id 当标题。
	it('is empty when the selected conversation is not in the list yet', () => {
		const wrapper = mountNavigation();
		push({ selectedConversationId: 'conv-missing' });
		expect(wrapper.vm.navigation.activeConversationTitle.value).toBe('');
		wrapper.unmount();
	});
});
