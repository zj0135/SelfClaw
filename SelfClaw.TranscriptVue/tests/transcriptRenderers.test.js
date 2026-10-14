import { describe, expect, it } from 'vitest';
import { toolStatusLabel } from '../src/renderers/shared.js';
import { buildRenderBlocks, buildToolActionSummary } from '../src/renderers/transcript.js';

describe('toolStatusLabel', () => {
	it('labels completed explicitly and passes unknown statuses through', () => {
		expect(toolStatusLabel('completed')).toBe('成功');
		expect(toolStatusLabel('somethingelse')).toBe('somethingelse');
		expect(toolStatusLabel(undefined)).toBe('');
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
});
