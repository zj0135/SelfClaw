import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { mount } from '@vue/test-utils';
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
});
