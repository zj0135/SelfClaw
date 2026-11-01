import { describe, expect, it } from 'vitest';
import { renderMarkdown } from '../../src/renderers/markdown.js';

const user = (source) => renderMarkdown(source, { context: 'user' });

describe('用户消息换行', () => {
	it('user 上下文把单个换行渲染成 <br>，assistant 内容仍是软换行', () => {
		expect(user('第一行\n第二行')).toContain('<br>');
		expect(renderMarkdown('第一行\n第二行', { context: 'content' })).not.toContain('<br>');
	});

	it('chip 与换行同时出现时都保留', () => {
		const html = user('@src/a.cs 看这个\n第二行');
		expect(html).toContain('inline-chip--file');
		expect(html).toContain('<br>');
	});
});

describe('转录 chip：技能 [/id]', () => {
	it('user 上下文渲染成 inline-chip--skill', () => {
		const html = user('请用 [/code-review] 看看');
		expect(html).toContain('class="inline-chip inline-chip--skill"');
		expect(html).toContain('<span class="inline-chip-label">code-review</span>');
		expect(html).not.toContain('[/code-review]');
	});

	it('assistant 内容不渲染 chip', () => {
		expect(renderMarkdown('[/code-review]', { context: 'content' })).not.toContain('inline-chip');
	});
});

describe('转录 chip：文件 @路径', () => {
	it('开头与空白后的 @路径 渲染成 inline-chip--file', () => {
		expect(user('@src/Foo.cs')).toContain('<span class="inline-chip inline-chip--file" role="text" title="src/Foo.cs"><span class="inline-chip-label">src/Foo.cs</span></span>');
		expect(user('看一下 @README.md 再说')).toContain('title="README.md"');
		expect(user('目录 @src/')).toContain('title="src/"');
	});

	it('带引号的路径取引号内全文', () => {
		const html = user('@"my docs/a.md"');
		expect(html).toContain('title="my docs/a.md"');
		expect(html).toContain('<span class="inline-chip-label">my docs/a.md</span>');
		expect(html).not.toContain('&quot;my docs');
	});

	it('句尾的 . 留在正文里', () => {
		const html = user('看 @src/a.cs.');
		expect(html).toContain('title="src/a.cs"');
		expect(html).toMatch(/<\/span><\/span>\.<\/p>/);
	});

	it('邮箱、不像路径的 @ 名字、非 user 上下文都不渲染', () => {
		expect(user('a@b.com')).not.toContain('inline-chip');
		expect(user('@张三 看一下')).not.toContain('inline-chip');
		expect(user('@everyone')).not.toContain('inline-chip');
		expect(renderMarkdown('@src/Foo.cs', { context: 'content' })).not.toContain('inline-chip');
	});

	it('HTML 特殊字符不会变成元素', () => {
		const html = user('@"a<b>/c.md"');
		const document = new DOMParser().parseFromString(html, 'text/html');
		expect(document.querySelector('b')).toBeNull();
		expect(document.querySelector('.inline-chip-label')?.textContent).toBe('a<b>/c.md');
		expect(document.querySelector('.inline-chip')?.getAttribute('title')).toBe('a<b>/c.md');
	});
});
