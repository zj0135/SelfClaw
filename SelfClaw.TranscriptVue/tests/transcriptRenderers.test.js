import { describe, expect, it } from 'vitest';
import { toolStatusLabel } from '../src/renderers/shared.js';
import { buildRenderBlocks, buildToolActionSummary, resolveToolGroupStatus } from '../src/renderers/transcript.js';

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

describe('buildToolActionSummary', () => {
	it('aggregates tool groups into a localized summary', () => {
		const summary = buildToolActionSummary([
			{ toolName: 'read_file' },
			{ toolName: 'read_file' },
			{ toolName: 'read_file' },
			{ toolName: 'edit_file' },
			{ toolName: 'edit_file' },
		]);

		expect(summary).toBe('读取3个文件，修改2个文件');
	});

	it('counts unclassified tools as calls when the tool name is unknown', () => {
		expect(buildToolActionSummary([{ toolName: 'Read' }, { toolName: 'Bash' }])).toBe('调用2个工具');
		expect(buildToolActionSummary([{ text: 'Read README.md' }])).toBe('调用1个工具');
	});

	it('falls back to calls for extension tools', () => {
		expect(buildToolActionSummary([{ toolName: 'list_issues' }, { toolName: 'read_issue' }])).toBe('调用2个工具');
	});
});

describe('buildRenderBlocks', () => {
	it('renders a notice segment as its own block', () => {
		const blocks = buildRenderBlocks({
			id: 'message-1',
			segments: [
				{ kind: 'notice', text: 'Hook added context (12 chars).', segmentId: 'message-1:notice:0' },
				{ kind: 'content', markdown: 'answer', segmentId: 'message-1:text:1' },
			],
		});

		expect(blocks.map((block) => block.type)).toEqual(['notice', 'body']);
		expect(blocks[0].id).toBe('message-1:notice:0');
		expect(blocks[0].segment.text).toBe('Hook added context (12 chars).');
	});

	it('skips blank body segments so consecutive blocks stay adjacent', () => {
		const blocks = buildRenderBlocks({
			id: 'message-1',
			segments: [
				{ kind: 'tool', toolName: 'read_file', text: 'Read README.md', segmentId: 'message-1:tool:0' },
				{ kind: 'content', markdown: '   ', segmentId: 'message-1:text:1' },
				{ kind: 'tool', toolName: 'list_files', text: 'List src', segmentId: 'message-1:tool:2' },
			],
		});

		expect(blocks.map((block) => block.type)).toEqual(['tool', 'tool']);
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
