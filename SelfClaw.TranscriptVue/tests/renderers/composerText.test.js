import { describe, expect, it } from 'vitest';
import {
	createCommandChip,
	createFileChip,
	createSkillChip,
	detectMentionTrigger,
	formatChipToken,
	serializeEditorNodes,
} from '../../src/renderers/composerText.js';

describe('detectMentionTrigger', () => {
	it('识别开头的触发符与空 query', () => {
		expect(detectMentionTrigger('@')).toEqual({ kind: 'file', query: '', start: 0 });
		expect(detectMentionTrigger('/')).toEqual({ kind: 'command', query: '', start: 0 });
		expect(detectMentionTrigger('$')).toEqual({ kind: 'skill', query: '', start: 0 });
	});

	it('识别空白（含换行）之后的触发符并带出 query', () => {
		expect(detectMentionTrigger('看 @src/a')).toEqual({ kind: 'file', query: 'src/a', start: 2 });
		expect(detectMentionTrigger('x\n$co')).toEqual({ kind: 'skill', query: 'co', start: 2 });
		expect(detectMentionTrigger('先 /cle')).toEqual({ kind: 'command', query: 'cle', start: 2 });
	});

	it('词中的触发符不触发', () => {
		expect(detectMentionTrigger('a@b.com')).toBeNull();
		expect(detectMentionTrigger('foo/bar')).toBeNull();
		expect(detectMentionTrigger('[/code-review')).toBeNull();
		expect(detectMentionTrigger('价格100$')).toBeNull();
	});

	it('query 里出现空白后不再触发', () => {
		expect(detectMentionTrigger('@src a')).toBeNull();
		expect(detectMentionTrigger('@src ')).toBeNull();
		expect(detectMentionTrigger('')).toBeNull();
	});
});

describe('formatChipToken', () => {
	it('技能保持宿主契约 [/id]', () => {
		expect(formatChipToken('skill', 'code-review')).toBe('[/code-review]');
		expect(formatChipToken('skill', 'office/presentation')).toBe('[/office/presentation]');
	});

	it('文件用裸 @路径，含空白或特殊字符时加引号', () => {
		expect(formatChipToken('file', 'src/Foo.cs')).toBe('@src/Foo.cs');
		expect(formatChipToken('file', 'src/')).toBe('@src/');
		expect(formatChipToken('file', 'my docs/a.md')).toBe('@"my docs/a.md"');
		expect(formatChipToken('file', 'a(b)/c.md')).toBe('@"a(b)/c.md"');
		expect(formatChipToken('file', '文档/说明.md')).toBe('@文档/说明.md');
	});

	it('指令是裸 /name，未知类型为空串', () => {
		expect(formatChipToken('command', 'clear')).toBe('/clear');
		expect(formatChipToken('other', 'x')).toBe('');
	});
});

describe('chip 描述符', () => {
	it('文件 chip 统一正斜杠、去掉前导斜杠、目录带尾斜杠', () => {
		expect(createFileChip('src\\lib', { isDirectory: true })).toEqual({ kind: 'file', value: 'src/lib/', label: 'src/lib/', title: 'src/lib/' });
		expect(createFileChip('/src/a.cs')).toEqual({ kind: 'file', value: 'src/a.cs', label: 'src/a.cs', title: 'src/a.cs' });
		expect(createFileChip('src/', { isDirectory: true }).value).toBe('src/');
	});

	it('技能与指令 chip 去掉多余字符', () => {
		expect(createSkillChip(' code-review ')).toEqual({ kind: 'skill', value: 'code-review', label: 'code-review', title: 'code-review' });
		expect(createCommandChip('/clear')).toEqual({ kind: 'command', value: 'clear', label: '/clear', title: '/clear' });
	});
});

describe('serializeEditorNodes', () => {
	function chip(kind, value) {
		const element = document.createElement('span');
		element.setAttribute('contenteditable', 'false');
		element.setAttribute('data-chip-kind', kind);
		element.setAttribute('data-chip-value', value);
		element.textContent = 'label';
		return element;
	}

	it('文本节点原样、chip 按 token、<br> 为换行、nbsp 还原为空格', () => {
		const root = document.createElement('div');
		root.append(
			document.createTextNode('hi '),
			chip('skill', 'code-review'),
			document.createTextNode(' and '),
			chip('file', 'my docs/a.md'),
			document.createTextNode(' ok\n'),
			document.createElement('br'),
		);
		expect(serializeEditorNodes(root.childNodes)).toBe('hi [/code-review] and @"my docs/a.md" ok\n\n');
	});

	it('回退模式的 <div> 分段之间补换行，行内元素只取文字', () => {
		const root = document.createElement('div');
		root.innerHTML = '<div>line1</div><div>line2 <b>bold</b></div>';
		expect(serializeEditorNodes(root.childNodes)).toBe('line1\nline2 bold');
	});

	it('空输入返回空串', () => {
		expect(serializeEditorNodes(undefined)).toBe('');
		expect(serializeEditorNodes(document.createElement('div').childNodes)).toBe('');
	});
});
