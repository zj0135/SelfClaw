import { describe, expect, it } from 'vitest';
import { toolStatusLabel } from '../src/renderers/shared.js';
import { buildRenderBlocks, resolveToolGroupStatus } from '../src/renderers/transcript.js';

describe('toolStatusLabel', () => {
	it('labels a hook-blocked call', () => {
		expect(toolStatusLabel('blocked')).toBe('已拦截');
	});

	it('labels completed explicitly and passes unknown statuses through', () => {
		expect(toolStatusLabel('completed')).toBe('成功');
		expect(toolStatusLabel('somethingelse')).toBe('somethingelse');
		expect(toolStatusLabel(undefined)).toBe('');
	});
});

describe('resolveToolGroupStatus', () => {
	it('ranks a blocked member below running and failed but above cancelled', () => {
		expect(resolveToolGroupStatus([{ status: 'completed' }, { status: 'blocked' }])).toBe('blocked');
		expect(resolveToolGroupStatus([{ status: 'blocked' }, { status: 'failed' }])).toBe('failed');
		expect(resolveToolGroupStatus([{ status: 'blocked' }, { status: 'running' }])).toBe('running');
		expect(resolveToolGroupStatus([{ status: 'blocked' }, { status: 'cancelled' }])).toBe('blocked');
	});
});

describe('buildRenderBlocks', () => {
	it('renders a notice segment as its own block', () => {
		const blocks = buildRenderBlocks({
			id: 'message-1',
			segments: [
				{ kind: 'notice', text: 'Hook added context (12 chars).', segmentId: 'message-1:notice:0' },
				{ kind: 'content', text: 'answer', segmentId: 'message-1:text:1' },
			],
		});

		expect(blocks.map((block) => block.type)).toEqual(['notice', 'body']);
		expect(blocks[0].id).toBe('message-1:notice:0');
		expect(blocks[0].segment.text).toBe('Hook added context (12 chars).');
	});

	it('keeps consecutive notices as separate blocks', () => {
		const blocks = buildRenderBlocks({
			id: 'message-1',
			segments: [
				{ kind: 'notice', text: 'first', segmentId: 'message-1:notice:0' },
				{ kind: 'notice', text: 'second', segmentId: 'message-1:notice:1' },
			],
		});

		expect(blocks).toHaveLength(2);
		expect(blocks.every((block) => block.type === 'notice')).toBe(true);
	});
});
