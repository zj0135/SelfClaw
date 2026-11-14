import { describe, expect, it } from 'vitest';
import { mount } from '@vue/test-utils';
import ThinkingBlock from '../src/components/chat/transcript/ThinkingBlock.vue';
import { useTranscriptCollapse } from '../src/composables/useTranscriptCollapse.js';

describe('useTranscriptCollapse', () => {
	it('follows the phase-aware automatic defaults for untouched blocks', () => {
		const collapse = useTranscriptCollapse();
		const thinkingId = 'msg-1:thinking:0';
		const groupId = 'msg-1:tool-group:0';

		// 思考 / 工具组：进行中展开，完成后折叠。
		expect(collapse.isThinkingOpen(thinkingId, true)).toBe(true);
		expect(collapse.isThinkingOpen(thinkingId, false)).toBe(false);
		expect(collapse.isToolGroupOpen(groupId, true)).toBe(true);
		expect(collapse.isToolGroupOpen(groupId, false)).toBe(false);

		// 单个工具、组内成员与任务计划卡默认折叠。
		expect(collapse.isToolOpen('msg-1:tool:0', false)).toBe(false);
		expect(collapse.isToolOpen('group-member', false)).toBe(false);
		expect(collapse.isTodoOpen('msg-1:todo:0', false)).toBe(false);
	});

	it('keeps a manual choice when the automatic default changes', () => {
		const collapse = useTranscriptCollapse();
		const thinkingId = 'msg-1:thinking:0';
		const groupId = 'msg-1:tool-group:0';
		const toolId = 'msg-1:tool:0';

		// 进行中手动收起，完成后仍保持收起；再点一次展开，完成后也保持展开。
		collapse.toggleThinking(thinkingId, true);
		expect(collapse.isThinkingOpen(thinkingId, true)).toBe(false);
		expect(collapse.isThinkingOpen(thinkingId, false)).toBe(false);
		collapse.toggleThinking(thinkingId, false);
		expect(collapse.isThinkingOpen(thinkingId, true)).toBe(true);
		expect(collapse.isThinkingOpen(thinkingId, false)).toBe(true);

		// 工具组完成后手动展开，下一轮忙碌态也不重置。
		collapse.toggleToolGroup(groupId, false);
		expect(collapse.isToolGroupOpen(groupId, false)).toBe(true);
		expect(collapse.isToolGroupOpen(groupId, true)).toBe(true);

		// 单卡手动展开后一直保持展开。
		collapse.toggleTool(toolId, false);
		expect(collapse.isToolOpen(toolId, false)).toBe(true);

		// 手动状态按 id 隔离，不污染别的块。
		expect(collapse.isToolOpen('other-tool', false)).toBe(false);
		expect(collapse.isThinkingOpen('other-thinking', true)).toBe(true);
		expect(collapse.isToolGroupOpen('other-group', true)).toBe(true);
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

	it('does not treat a completed thinking block as live inside a still-streaming message', () => {
		const wrapper = mount(ThinkingBlock, {
			props: {
				id: 'msg-1:thinking:0',
				item: { id: 'msg-1', isThinking: true },
				segment: defaultSegment,
				open: true,
			},
		});

		expect(wrapper.find('.thinking-label').text()).toBe('思考过程');
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
