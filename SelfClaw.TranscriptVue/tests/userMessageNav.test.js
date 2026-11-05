import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount } from '@vue/test-utils';
import { nextTick } from 'vue';
import UserMessageNav from '../src/components/chat/transcript/UserMessageNav.vue';
import TranscriptPanel from '../src/components/chat/TranscriptPanel.vue';

describe('UserMessageNav', () => {
	let mounted = [];

	beforeEach(() => {
		mounted = [];
	});

	afterEach(() => {
		mounted.forEach((wrapper) => wrapper.unmount());
		mounted = [];
		vi.restoreAllMocks();
	});

	it('does not render navigation track when there are no user messages', () => {
		const wrapper = mount(UserMessageNav, {
			props: {
				items: [
					{ id: 'a1', role: 'assistant', segments: [{ kind: 'content', markdown: '你好！' }] },
				],
			},
		});
		mounted.push(wrapper);

		expect(wrapper.find('.user-message-nav').exists()).toBe(false);
	});

	it('renders exactly two indicator lines for two rounds of user messages', () => {
		const items = [
			{
				id: 'u1',
				role: 'user',
				segments: [{ kind: 'content', markdown: '请帮我写一个函数' }],
			},
			{
				id: 'a1',
				role: 'assistant',
				segments: [{ kind: 'content', markdown: '好的，这是代码' }],
			},
			{
				id: 'u2',
				role: 'user',
				segments: [{ kind: 'content', markdown: '再给这个函数添加单元测试' }],
			},
			{
				id: 'a2',
				role: 'assistant',
				segments: [{ kind: 'content', markdown: '这是测试代码' }],
			},
		];

		const wrapper = mount(UserMessageNav, {
			props: { items },
		});
		mounted.push(wrapper);

		expect(wrapper.find('.user-message-nav').exists()).toBe(true);
		const navButtons = wrapper.findAll('.nav-item');
		expect(navButtons).toHaveLength(2);

		const indicators = wrapper.findAll('.nav-indicator');
		expect(indicators).toHaveLength(2);
	});

	it('displays hover popover with fixed width and user text on mouseenter, hides on mouseleave', async () => {
		const items = [
			{
				id: 'u1',
				role: 'user',
				segments: [{ kind: 'content', markdown: '根据这张设计图实现布局' }],
				attachments: [
					{ fileName: 'screenshot.png', mediaType: 'image/png', sourceUrl: 'https://example.com/screenshot.png' },
				],
			},
		];

		const wrapper = mount(UserMessageNav, {
			props: { items },
		});
		mounted.push(wrapper);

		// Initially popover is not visible
		expect(wrapper.find('.nav-popover').exists()).toBe(false);

		// Hover the indicator
		const btn = wrapper.find('.nav-item');
		await btn.trigger('mouseenter');

		const popover = wrapper.find('.nav-popover');
		expect(popover.exists()).toBe(true);
		expect(popover.text()).toBe('根据这张设计图实现布局');
		expect(popover.text()).not.toContain('screenshot');

		// Leave mouse
		await btn.trigger('mouseleave');
		expect(wrapper.find('.nav-popover').exists()).toBe(false);
	});

	it('does not show hover popover when user message has only images and no text', async () => {
		const items = [
			{
				id: 'u1',
				role: 'user',
				segments: [],
				attachments: [
					{ fileName: 'photo.png', mediaType: 'image/png', sourceUrl: 'https://example.com/photo.png' },
				],
			},
		];

		const wrapper = mount(UserMessageNav, {
			props: { items },
		});
		mounted.push(wrapper);

		const btn = wrapper.find('.nav-item');
		await btn.trigger('mouseenter');
		expect(wrapper.find('.nav-popover').exists()).toBe(false);
	});

	it('scrolls into view on click without any highlight selection effect', async () => {
		const mockContainer = document.createElement('div');
		const targetRow = document.createElement('div');
		targetRow.className = 'message-row user';
		targetRow.setAttribute('data-message-id', 'u2');
		targetRow.scrollIntoView = vi.fn();
		mockContainer.appendChild(targetRow);

		const items = [
			{ id: 'u1', role: 'user', segments: [{ kind: 'content', markdown: '问题 1' }] },
			{ id: 'u2', role: 'user', segments: [{ kind: 'content', markdown: '问题 2' }] },
		];

		const wrapper = mount(UserMessageNav, {
			props: {
				items,
				getScrollEl: () => mockContainer,
			},
		});
		mounted.push(wrapper);

		const buttons = wrapper.findAll('.nav-item');
		await buttons[1].trigger('click');

		// Only scroll into view is called
		expect(targetRow.scrollIntoView).toHaveBeenCalledWith({ behavior: 'smooth', block: 'center' });
		// No highlight class added to the target message row
		expect(targetRow.classList.contains('user-nav-highlight')).toBe(false);
		expect(buttons[1].classes()).toContain('active');
	});

	it('integrates seamlessly into TranscriptPanel', () => {
		const items = [
			{ id: 'u1', role: 'user', segments: [{ kind: 'content', markdown: '第一轮' }] },
			{ id: 'a1', role: 'assistant', segments: [{ kind: 'content', markdown: '回复一' }] },
			{ id: 'u2', role: 'user', segments: [{ kind: 'content', markdown: '第二轮' }] },
		];

		const wrapper = mount(TranscriptPanel, {
			props: {
				items,
				collapse: { isThinkingOpen: () => false, isToolOpen: () => false },
			},
		});
		mounted.push(wrapper);

		const nav = wrapper.findComponent(UserMessageNav);
		expect(nav.exists()).toBe(true);
		expect(nav.findAll('.nav-item')).toHaveLength(2);
	});

	it('hides scroll-to-bottom button when scrolled near bottom', () => {
		const mockContainer = document.createElement('div');
		Object.defineProperty(mockContainer, 'scrollHeight', { value: 1000, configurable: true });
		Object.defineProperty(mockContainer, 'clientHeight', { value: 500, configurable: true });
		mockContainer.scrollTop = 450; // distanceToBottom = 50 <= 120

		const items = [{ id: 'u1', role: 'user', segments: [{ kind: 'content', markdown: '测试' }] }];
		const wrapper = mount(UserMessageNav, {
			props: {
				items,
				getScrollEl: () => mockContainer,
			},
		});
		mounted.push(wrapper);

		expect(wrapper.find('.nav-bottom-btn').exists()).toBe(false);
	});

	it('shows scroll-to-bottom button when scrolled up past threshold and handles hover popover', async () => {
		const mockContainer = document.createElement('div');
		Object.defineProperty(mockContainer, 'scrollHeight', { value: 1000, configurable: true });
		Object.defineProperty(mockContainer, 'clientHeight', { value: 500, configurable: true });
		Object.defineProperty(mockContainer, 'scrollTop', { value: 200, writable: true, configurable: true });

		const items = [{ id: 'u1', role: 'user', segments: [{ kind: 'content', markdown: '测试' }] }];
		const wrapper = mount(UserMessageNav, {
			props: {
				items,
				getScrollEl: () => mockContainer,
			},
		});
		mounted.push(wrapper);
		await flushPromises();

		const bottomBtn = wrapper.find('.nav-bottom-btn');
		expect(bottomBtn.exists()).toBe(true);

		// 鼠标悬停显示“定位到底部”提示气泡
		await bottomBtn.trigger('mouseenter');
		const popover = wrapper.find('.nav-popover');
		expect(popover.exists()).toBe(true);
		expect(popover.text()).toBe('定位到底部');

		// 鼠标移开提示气泡消失
		await bottomBtn.trigger('mouseleave');
		expect(wrapper.find('.nav-popover').exists()).toBe(false);
	});

	it('scrolls smoothly to bottom on button click', async () => {
		const mockContainer = document.createElement('div');
		Object.defineProperty(mockContainer, 'scrollHeight', { value: 1200, configurable: true });
		Object.defineProperty(mockContainer, 'clientHeight', { value: 500, configurable: true });
		Object.defineProperty(mockContainer, 'scrollTop', { value: 300, writable: true, configurable: true });
		mockContainer.scrollTo = vi.fn();

		const items = [{ id: 'u1', role: 'user', segments: [{ kind: 'content', markdown: '测试' }] }];
		const wrapper = mount(UserMessageNav, {
			props: {
				items,
				getScrollEl: () => mockContainer,
			},
		});
		mounted.push(wrapper);
		await flushPromises();

		const bottomBtn = wrapper.find('.nav-bottom-btn');
		expect(bottomBtn.exists()).toBe(true);

		await bottomBtn.trigger('click');
		expect(mockContainer.scrollTo).toHaveBeenCalledWith({
			top: 1200,
			behavior: 'smooth',
		});
	});
});
