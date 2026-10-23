import { describe, expect, it, vi } from 'vitest';
import { mount } from '@vue/test-utils';
import { defineComponent, ref } from 'vue';
import AppSidebar from '../src/components/sidebar/AppSidebar.vue';

const { bridge, transcriptListeners } = vi.hoisted(() => {
	const listeners = [];
	return {
		transcriptListeners: listeners,
		bridge: {
			request: vi.fn(async () => ({})),
			on: vi.fn(() => () => {}),
			post: vi.fn(),
			hasHost: () => true,
		},
	};
});

vi.mock('../src/composables/hostBridge.js', () => ({
	hostBridge: bridge,
	useHostBridge: () => bridge,
	isSuperseded: () => false,
}));

vi.mock('../src/composables/transcriptBridge.js', () => ({
	useTranscriptBridge: () => ({
		on: (fn) => {
			transcriptListeners.push(fn);
			return () => {};
		},
	}),
}));

import { useConversationNavigation } from '../src/composables/useConversationNavigation.js';

describe('sidebar running conversation state', () => {
	it('maps conversation.isRunning and isBusy into conversation node isRunning', () => {
		const harness = defineComponent({
			setup() {
				const currentViewId = ref('chat');
				const chatViewRef = ref(null);
				return { navigation: useConversationNavigation(currentViewId, chatViewRef, () => {}) };
			},
			template: '<div />',
		});
		const wrapper = mount(harness);

		// Broadcast transcript with running conversation and background idle conversation
		const listener = transcriptListeners[transcriptListeners.length - 1];
		listener({
			conversations: [
				{ id: 'conv-1', title: 'Running in background', isRunning: true },
				{ id: 'conv-2', title: 'Selected running turn', isRunning: false },
				{ id: 'conv-3', title: 'Idle conversation', isRunning: false },
			],
			selectedConversationId: 'conv-2',
			isBusy: true,
		});

		const items = wrapper.vm.navigation.navItems.value;
		const convGroup = items.find((i) => i.id === 'conversations');
		expect(convGroup).toBeDefined();

		const nodes = convGroup.children;
		expect(nodes).toHaveLength(3);

		// conv-1: isRunning = true (from backend payload)
		expect(nodes.find((n) => n.id === 'conv-1').isRunning).toBe(true);
		// conv-2: isRunning = true (selected and isBusy)
		expect(nodes.find((n) => n.id === 'conv-2').isRunning).toBe(true);
		// conv-3: isRunning = false
		expect(nodes.find((n) => n.id === 'conv-3').isRunning).toBe(false);

		wrapper.unmount();
	});

	it('renders loading spinner for running conversations in AppSidebar', () => {
		const navItems = [
			{
				id: 'conversations',
				label: '对话',
				type: 'group',
				children: [
					{ id: 'conv-running', label: '运行中的任务', type: 'conversation', isRunning: true },
					{ id: 'conv-idle', label: '空闲会话', type: 'conversation', isRunning: false },
				],
			},
		];

		const wrapper = mount(AppSidebar, {
			props: {
				items: navItems,
				activeId: 'conv-idle',
				collapsed: false,
			},
		});

		// Find the button for running conversation
		const buttons = wrapper.findAll('.node.kind-chat');
		expect(buttons).toHaveLength(2);

		const runningBtn = buttons[0];
		expect(runningBtn.classes()).toContain('is-running');
		expect(runningBtn.find('.node-spin').exists()).toBe(true);
		expect(runningBtn.find('.node-spin svg').exists()).toBe(true);
		expect(runningBtn.find('.dot').exists()).toBe(false);

		const idleBtn = buttons[1];
		expect(idleBtn.classes()).not.toContain('is-running');
		expect(idleBtn.find('.node-spin').exists()).toBe(false);
		expect(idleBtn.find('.dot').exists()).toBe(true);

		wrapper.unmount();
	});
});
