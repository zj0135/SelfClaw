import { describe, expect, it } from 'vitest';
import { mount } from '@vue/test-utils';
import MessageBlocks from '../src/components/chat/transcript/MessageBlocks.vue';
import { useTranscriptCollapse } from '../src/composables/useTranscriptCollapse.js';

const thinking = (markdown, isPending) => ({
	kind: 'thinking', markdown, isPending, segmentId: 'm1:thinking:0',
});
const tool = (id, status, extra = {}) => ({
	kind: 'tool', segmentId: id, text: `Read ${id}`, detailText: `result ${id}`, toolName: 'read_file', status, ...extra,
});

function mountBlocks(segments, { isThinking = false } = {}) {
	const collapse = useTranscriptCollapse();
	const item = { id: 'm1', role: 'assistant', status: isThinking ? 'streaming' : 'completed', isThinking, segments };
	const wrapper = mount(MessageBlocks, { props: { item, collapse } });
	return { wrapper, collapse, setSegments: (next, nextThinking = false) => wrapper.setProps({
		item: { ...item, isThinking: nextThinking, status: nextThinking ? 'streaming' : 'completed', segments: next },
	}) };
}

describe('transcript auto collapse', () => {
	it('opens a thinking block while it streams and folds it once thinking completes', async () => {
		const { wrapper, setSegments } = mountBlocks([thinking('正在推理', true)], { isThinking: true });
		expect(wrapper.find('.thinking-block').classes()).toContain('open');
		expect(wrapper.find('.thinking-content').exists()).toBe(true);
		expect(wrapper.find('.thinking-label').text()).toBe('思考中...');

		await setSegments([thinking('推理完毕', false)]);
		expect(wrapper.find('.thinking-block').classes()).not.toContain('open');
		expect(wrapper.find('.thinking-content').exists()).toBe(false);
		expect(wrapper.find('.thinking-label').text()).toBe('思考过程');
		expect(wrapper.find('.thinking-summary').exists()).toBe(true);
	});

	it('folds a thinking block as soon as another segment follows it in a live message', async () => {
		const { wrapper, setSegments } = mountBlocks([thinking('正在推理', true)], { isThinking: true });
		expect(wrapper.find('.thinking-content').exists()).toBe(true);

		await setSegments([thinking('推理完毕', false), { kind: 'content', markdown: '回答', segmentId: 'm1:text:1' }], true);
		expect(wrapper.find('.thinking-content').exists()).toBe(false);
	});

	it('honours a manual thinking choice across the streaming/completed transition', async () => {
		const { wrapper, setSegments } = mountBlocks([thinking('正在推理', true)], { isThinking: true });
		await wrapper.find('.thinking-summary').trigger('click');
		expect(wrapper.find('.thinking-content').exists()).toBe(false);
		await setSegments([thinking('推理完毕', false)]);
		expect(wrapper.find('.thinking-content').exists()).toBe(false);

		await wrapper.find('.thinking-summary').trigger('click');
		expect(wrapper.find('.thinking-content').exists()).toBe(true);
		await setSegments([thinking('仍在推理', true)], true);
		expect(wrapper.find('.thinking-content').exists()).toBe(true);
	});

	it('keeps a single tool collapsed by default while running and after completion', async () => {
		const { wrapper, setSegments } = mountBlocks([tool('t1', 'running', { isPending: true })]);
		expect(wrapper.find('.tool-block').classes()).not.toContain('open');
		expect(wrapper.find('.tool-details').exists()).toBe(false);

		await setSegments([tool('t1', 'completed')]);
		expect(wrapper.find('.tool-block').classes()).toContain('completed');
		expect(wrapper.find('.tool-details').exists()).toBe(false);
	});

	it('lets a manually expanded single tool stay expanded', async () => {
		const { wrapper, setSegments } = mountBlocks([tool('t1', 'completed')]);
		expect(wrapper.find('.tool-details').exists()).toBe(false);
		await wrapper.find('.tool-summary').trigger('click');
		expect(wrapper.find('.tool-details').text()).toContain('result t1');

		await setSegments([tool('t1', 'running', { isPending: true })]);
		expect(wrapper.find('.tool-details').text()).toContain('result t1');
		await setSegments([tool('t1', 'completed')]);
		expect(wrapper.find('.tool-details').text()).toContain('result t1');
	});

	it('opens only the group while tools run, folds it on completion, and never auto-opens members', async () => {
		const active = [tool('t1', 'running', { isPending: true }), tool('t2', 'running')];
		const { wrapper, setSegments } = mountBlocks(active, { isThinking: true });
		expect(wrapper.find('.tool-group-block').classes()).toContain('open');
		expect(wrapper.findAll('.tool-group-details .tool-block')).toHaveLength(2);
		expect(wrapper.find('.tool-group-details .tool-details').exists()).toBe(false);
		expect(wrapper.find('.tool-group-block').classes()).toContain('running');

		await setSegments([tool('t1', 'completed'), tool('t2', 'completed')]);
		expect(wrapper.find('.tool-group-block').classes()).toContain('completed');
		expect(wrapper.find('.tool-group-details').exists()).toBe(false);
	});

	it('remembers a manual group expansion and a manually expanded member', async () => {
		const done = [tool('t1', 'completed'), tool('t2', 'completed')];
		const { wrapper, setSegments } = mountBlocks(done);
		await wrapper.find('.tool-group-summary').trigger('click');
		expect(wrapper.find('.tool-group-details').exists()).toBe(true);
		expect(wrapper.findAll('.tool-group-details .tool-details')).toHaveLength(0);

		await wrapper.find('.tool-group-details .tool-summary').trigger('click');
		expect(wrapper.findAll('.tool-group-details .tool-details')).toHaveLength(1);

		// 忙碌态回来时自动默认变了，但手动展开仍优先。
		await setSegments([tool('t1', 'running'), tool('t2', 'running')], true);
		expect(wrapper.find('.tool-group-details').exists()).toBe(true);
		expect(wrapper.findAll('.tool-group-details .tool-details')).toHaveLength(1);
	});
});
