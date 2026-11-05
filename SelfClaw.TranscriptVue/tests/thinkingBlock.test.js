import { describe, expect, it } from 'vitest';
import { mount } from '@vue/test-utils';
import ThinkingBlock from '../src/components/chat/transcript/ThinkingBlock.vue';
import { useTranscriptCollapse } from '../src/composables/useTranscriptCollapse.js';

describe('useTranscriptCollapse', () => {
	it('defaults thinking blocks to open, while tool and todo blocks remain collapsed', () => {
		const collapse = useTranscriptCollapse();

		// 思考卡片默认展开
		expect(collapse.isThinkingOpen('msg-1:thinking:0')).toBe(true);

		// 工具卡片与任务卡片保持默认折叠
		expect(collapse.isToolOpen('msg-1:tool:0')).toBe(false);
		expect(collapse.isToolGroupOpen('msg-1:tool-group:0:1')).toBe(false);
		expect(collapse.isTodoOpen('msg-1:todo:0')).toBe(false);
	});

	it('toggles thinking blocks to closed and back to open without affecting tool state', () => {
		const collapse = useTranscriptCollapse();
		const thinkingId = 'msg-1:thinking:0';

		// 初始展开
		expect(collapse.isThinkingOpen(thinkingId)).toBe(true);

		// 手动收起
		collapse.toggleThinking(thinkingId);
		expect(collapse.isThinkingOpen(thinkingId)).toBe(false);

		// 再次点击展开
		collapse.toggleThinking(thinkingId);
		expect(collapse.isThinkingOpen(thinkingId)).toBe(true);

		// 工具状态不受影响
		expect(collapse.isToolOpen('msg-1:tool:0')).toBe(false);
	});
});

describe('ThinkingBlock', () => {
	const defaultItem = { id: 'msg-1', isThinking: false };
	const defaultSegment = { kind: 'thinking', markdown: '思考细节内容', isPending: false };

	it('renders "思考过程" when thinking is completed', () => {
		const wrapper = mount(ThinkingBlock, {
			props: {
				id: 'msg-1:thinking:0',
				item: defaultItem,
				segment: defaultSegment,
				open: true,
			},
		});

		expect(wrapper.find('.thinking-label').text()).toBe('思考过程');
		expect(wrapper.find('.thinking-content').exists()).toBe(true);
		expect(wrapper.find('.thinking-block').classes()).toContain('open');
	});

	it('renders "思考中..." when actively thinking', () => {
		const wrapper = mount(ThinkingBlock, {
			props: {
				id: 'msg-1:thinking:0',
				item: { id: 'msg-1', isThinking: true },
				segment: { kind: 'thinking', markdown: '思考中产生的内容', isPending: true },
				open: true,
			},
		});

		expect(wrapper.find('.thinking-label').text()).toBe('思考中...');
		expect(wrapper.find('.thinking-content').exists()).toBe(true);
	});

	it('emits toggle event when clicking summary header', async () => {
		const wrapper = mount(ThinkingBlock, {
			props: {
				id: 'msg-1:thinking:0',
				item: defaultItem,
				segment: defaultSegment,
				open: true,
			},
		});

		await wrapper.find('.thinking-summary').trigger('click');
		expect(wrapper.emitted('toggle')).toHaveLength(1);
	});

	it('hides thinking content when open is false', () => {
		const wrapper = mount(ThinkingBlock, {
			props: {
				id: 'msg-1:thinking:0',
				item: defaultItem,
				segment: defaultSegment,
				open: false,
			},
		});

		expect(wrapper.find('.thinking-content').exists()).toBe(false);
		expect(wrapper.find('.thinking-block').classes()).not.toContain('open');
	});
});
