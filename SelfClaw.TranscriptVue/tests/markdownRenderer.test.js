import { describe, expect, it } from 'vitest';
import { renderMarkdown } from '../src/renderers/markdown.js';

describe('renderMarkdown links', () => {
	it('renders a markdown link instead of throwing', () => {
		const html = renderMarkdown('Read the [docs](https://example.com/docs) first.');
		expect(html).toContain('<a href="https://example.com/docs"');
		expect(html).toContain('docs</a>');
	});

	it('forces target and rel on rendered links', () => {
		const html = renderMarkdown('[docs](https://example.com/docs)');
		expect(html).toContain('target="_blank"');
		expect(html).toContain('rel="noopener noreferrer"');
	});

	it('renders autolinks and keeps inline code untouched', () => {
		const html = renderMarkdown('See <https://example.com/a> and `a[1]` text.');
		expect(html).toContain('href="https://example.com/a"');
		expect(html).toContain('<code>a[1]</code>');
	});

	it('still renders images with their default rule', () => {
		const html = renderMarkdown('![alt](https://example.com/i.png)');
		expect(html).toContain('src="https://example.com/i.png"');
		expect(html).toContain('loading="lazy"');
	});
});
