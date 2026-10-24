import { describe, expect, it } from 'vitest';
import { toolStatusLabel } from '../src/renderers/shared.js';
import {
	buildRenderBlocks,
	buildToolActionSummary,
	extractUserMessageText,
	extractUserNavItems,
} from '../src/renderers/transcript.js';

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

describe('extractUserMessageText', () => {
	it('extracts plain text from user message segments while ignoring images and attachments', () => {
		const item = {
			id: 'msg-1',
			role: 'user',
			segments: [
				{ kind: 'content', markdown: '请帮我优化这段代码\n第二行内容' },
			],
			attachments: [
				{ fileName: 'demo.png', mediaType: 'image/png', sourceUrl: 'https://example.com/demo.png' },
			],
		};

		expect(extractUserMessageText(item)).toBe('请帮我优化这段代码 第二行内容');
	});

	it('returns empty string when message only has attachments without text', () => {
		const item = {
			id: 'msg-2',
			role: 'user',
			segments: [],
			attachments: [
				{ fileName: 'demo.png', mediaType: 'image/png', sourceUrl: 'https://example.com/demo.png' },
			],
		};

		expect(extractUserMessageText(item)).toBe('');
	});

	it('falls back to text or content property when segments are not provided', () => {
		expect(extractUserMessageText({ text: '  hello  world  ' })).toBe('hello world');
		expect(extractUserMessageText({ content: 'fallback content\nnewline' })).toBe('fallback content newline');
		expect(extractUserMessageText(null)).toBe('');
	});
});

describe('extractUserNavItems', () => {
	it('filters only user messages and extracts their text', () => {
		const items = [
			{ id: 'u1', role: 'user', segments: [{ kind: 'content', markdown: '第一轮问题' }] },
			{ id: 'a1', role: 'assistant', segments: [{ kind: 'content', markdown: '回答一' }] },
			{ id: 'to1', kind: 'turn-outcome', role: 'system' },
			{ id: 'u2', role: 'user', segments: [{ kind: 'content', markdown: '第二轮问题' }] },
			{ id: 'a2', role: 'assistant', segments: [{ kind: 'content', markdown: '回答二' }] },
		];

		const navItems = extractUserNavItems(items);
		expect(navItems).toEqual([
			{ id: 'u1', text: '第一轮问题' },
			{ id: 'u2', text: '第二轮问题' },
		]);
	});

	it('returns empty array when there are no user messages', () => {
		expect(extractUserNavItems([])).toEqual([]);
		expect(extractUserNavItems([{ id: 'a1', role: 'assistant' }])).toEqual([]);
		expect(extractUserNavItems(null)).toEqual([]);
	});
});
