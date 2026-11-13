import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount } from '@vue/test-utils';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import UserMessageNav from '../src/components/chat/transcript/UserMessageNav.vue';
import TranscriptPanel from '../src/components/chat/TranscriptPanel.vue';

const componentSource = readFileSync(
	resolve(process.cwd(), 'src/components/chat/transcript/UserMessageNav.vue'),
	'utf8',
);
const userItems = (count) => Array.from({ length: count }, (_, index) => ({
	id: `u${index + 1}`,
	role: 'user',
	segments: [{ kind: 'content', markdown: `第 ${index + 1} 轮提问` }],
}));

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

	it('renders exactly two hollow circular indicators for two rounds of user messages', () => {
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

	it('uses a circular, anchor-aligned scroll-to-bottom button and keeps the ArrowDown icon', () => {
		const buttonBlock = componentSource.match(/\.nav-bottom-btn \{[\s\S]*?\}/)?.[0] ?? '';
		const noTrackBlock = componentSource.match(/\.user-message-nav:not\(:has\(\.nav-track\)\) \.nav-bottom-btn \{[\s\S]*?\}/)?.[0] ?? '';

		expect(buttonBlock).toContain('left: 50%');
		expect(buttonBlock).toContain('transform: translateX(-50%)');
		expect(buttonBlock).toContain('border-radius: 50%');
		expect(buttonBlock).toContain('background: var(--surface-base)');
		expect(noTrackBlock).toContain('transform: translate(-50%, -50%)');
		expect(componentSource).toContain('<ArrowDown');
	});

	it('uses compact hollow dots, fills only the active dot and keeps the track height bounded', () => {
		const trackBlock = componentSource.match(/\.nav-track \{[\s\S]*?\}/)?.[0] ?? '';
		const indicatorBlock = componentSource.match(/\.nav-indicator \{[\s\S]*?\}/)?.[0] ?? '';
		const activeBlock = componentSource.match(/\.nav-item\.active \.nav-indicator \{[\s\S]*?\}/)?.[0] ?? '';
		const navBlock = componentSource.match(/\.user-message-nav \{[\s\S]*?\}/)?.[0] ?? '';

		expect(trackBlock).toMatch(/gap:\s*2px/);
		expect(trackBlock).toContain('height: 100%');
		expect(navBlock).toContain('height: min(146px, max(0px, 100% - 160px))');
		expect(indicatorBlock).toContain('border-radius: 50%');
		expect(indicatorBlock).toContain('background: transparent');
		expect(activeBlock).toContain('background: var(--accent-base)');
		expect(componentSource).not.toContain('height: 2.5px');
		expect(componentSource).not.toContain('width: 22px');
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

		expect(wrapper.find('.nav-popover').exists()).toBe(false);

		const btn = wrapper.find('.nav-item');
		await btn.trigger('mouseenter');

		const popover = wrapper.find('.nav-popover');
		expect(popover.exists()).toBe(true);
		expect(popover.text()).toBe('根据这张设计图实现布局');
		expect(popover.text()).not.toContain('screenshot');

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

	it('marks only the selected anchor as active with accessibility state', async () => {
		const mockContainer = document.createElement('div');
		const firstRow = document.createElement('div');
		firstRow.className = 'message-row user';
		firstRow.setAttribute('data-message-id', 'u1');
		const secondRow = document.createElement('div');
		secondRow.className = 'message-row user';
		secondRow.setAttribute('data-message-id', 'u2');
		secondRow.scrollIntoView = vi.fn();
		mockContainer.append(firstRow, secondRow);

		const wrapper = mount(UserMessageNav, {
			props: { items: userItems(2), getScrollEl: () => mockContainer },
		});
		mounted.push(wrapper);

		const buttons = wrapper.findAll('.nav-item');
		await buttons[1].trigger('click');
		await flushPromises();

		expect(buttons[0].classes()).not.toContain('active');
		expect(buttons[0].attributes('aria-current')).toBeUndefined();
		expect(buttons[1].classes()).toContain('active');
		expect(buttons[1].attributes('aria-current')).toBe('true');
		expect(wrapper.findAll('.nav-item.active')).toHaveLength(1);
	});

	it('shows a bounded window around the active round with navigation arrows and wheel paging', async () => {
		const items = userItems(40);
		const container = document.createElement('div');
		for (const item of items) {
			const row = document.createElement('div');
			row.className = 'message-row user';
			row.setAttribute('data-message-id', item.id);
			row.scrollIntoView = vi.fn();
			container.appendChild(row);
		}

		const wrapper = mount(UserMessageNav, {
			props: { items, getScrollEl: () => container },
		});
		mounted.push(wrapper);
		await flushPromises();
		Object.defineProperty(wrapper.find('.nav-track').element, 'getBoundingClientRect', {
			value: () => ({ top: 0, bottom: 146, left: 0, right: 20, width: 20, height: 146 }),
			configurable: true,
		});
		await wrapper.find('.nav-item').trigger('focus');
		await wrapper.find('.nav-item').trigger('blur');
		await flushPromises();
		await wrapper.findComponent(UserMessageNav).vm.scrollTo('u1');
		await flushPromises();

		expect(wrapper.find('.nav-track').attributes('aria-label')).toContain('共 40 轮');
		const dots = wrapper.findAll('.nav-item');
		expect(dots).toHaveLength(8);
		expect(wrapper.find('.nav-step[aria-label="查看更早的用户消息"]').exists()).toBe(false);
		expect(wrapper.find('.nav-step[aria-label="查看更晚的用户消息"]').exists()).toBe(true);
		expect(dots[0].attributes('aria-label')).toBe('跳转到第 1 条用户消息');

		const forward = wrapper.find('.nav-step[aria-label="查看更晚的用户消息"]');
		await forward.trigger('click');
		await flushPromises();

		expect(wrapper.findAll('.nav-item')[0].attributes('aria-label')).toBe('跳转到第 5 条用户消息');
		expect(wrapper.find('.nav-step[aria-label="查看更早的用户消息"]').exists()).toBe(true);
		expect(wrapper.find('.nav-step[aria-label="查看更晚的用户消息"]').exists()).toBe(true);

		const track = wrapper.find('.nav-track');
		const wheelEvent = new WheelEvent('wheel', { deltaY: 120, cancelable: true });
		track.element.dispatchEvent(wheelEvent);
		await flushPromises();
		expect(wheelEvent.defaultPrevented).toBe(true);
		expect(wrapper.findAll('.nav-item')[0].attributes('aria-label')).toBe('跳转到第 13 条用户消息');
	});

	it('scrolls into view on click without any highlight selection effect', async () => {
		const mockContainer = document.createElement('div');
		const targetRow = document.createElement('div');
		targetRow.className = 'message-row user';
		targetRow.setAttribute('data-message-id', 'u2');
		targetRow.scrollIntoView = vi.fn();
		mockContainer.appendChild(targetRow);

		const wrapper = mount(UserMessageNav, {
			props: {
				items: userItems(2),
				getScrollEl: () => mockContainer,
			},
		});
		mounted.push(wrapper);

		const buttons = wrapper.findAll('.nav-item');
		await buttons[1].trigger('click');

		expect(targetRow.scrollIntoView).toHaveBeenCalledWith({ behavior: 'smooth', block: 'center' });
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
		mockContainer.scrollTop = 450;

		const wrapper = mount(UserMessageNav, {
			props: {
				items: userItems(1),
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

		const wrapper = mount(UserMessageNav, {
			props: {
				items: userItems(1),
				getScrollEl: () => mockContainer,
			},
		});
		mounted.push(wrapper);
		await flushPromises();

		const bottomBtn = wrapper.find('.nav-bottom-btn');
		expect(bottomBtn.exists()).toBe(true);

		await bottomBtn.trigger('mouseenter');
		const popover = wrapper.find('.nav-popover');
		expect(popover.exists()).toBe(true);
		expect(popover.text()).toBe('定位到底部');

		await bottomBtn.trigger('mouseleave');
		expect(wrapper.find('.nav-popover').exists()).toBe(false);
	});

	it('scrolls smoothly to bottom on button click', async () => {
		const mockContainer = document.createElement('div');
		Object.defineProperty(mockContainer, 'scrollHeight', { value: 1200, configurable: true });
		Object.defineProperty(mockContainer, 'clientHeight', { value: 500, configurable: true });
		Object.defineProperty(mockContainer, 'scrollTop', { value: 300, writable: true, configurable: true });
		mockContainer.scrollTo = vi.fn();

		const wrapper = mount(UserMessageNav, {
			props: {
				items: userItems(1),
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
