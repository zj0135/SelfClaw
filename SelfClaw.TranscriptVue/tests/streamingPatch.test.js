import { describe, expect, it } from 'vitest';
import { patchStreamingHtml, normalizeStreamingContainer } from '../src/renderers/streamingPatch.js';

describe('streamingPatch', () => {
	it('populates initial non-streaming content directly without animation classes', () => {
		const container = document.createElement('div');
		patchStreamingHtml(container, '<p>Hello World</p>', { isStreaming: false });

		expect(container.innerHTML).toBe('<p>Hello World</p>');
		expect(container.querySelector('.stream-fade-in')).toBeNull();
	});

	it('adds stream-fade-in class to new blocks during streaming', () => {
		const container = document.createElement('div');
		patchStreamingHtml(container, '<p>Streaming initial block</p>', { isStreaming: true });

		const p = container.querySelector('p');
		expect(p).not.toBeNull();
		expect(p.classList.contains('stream-fade-in')).toBe(true);
		expect(p.textContent).toBe('Streaming initial block');
	});

	it('preserves existing block DOM node and appends stream-token span for appended text', () => {
		const container = document.createElement('div');
		patchStreamingHtml(container, '<p>First line</p>', { isStreaming: true });
		const originalP = container.querySelector('p');

		// Streaming update appending text to the same paragraph
		patchStreamingHtml(container, '<p>First line and more content</p>', { isStreaming: true });

		// Paragraph DOM node must be identical (no full reconstruction / flicker)
		expect(container.querySelector('p')).toBe(originalP);

		// The new text is encapsulated in a stream-token with stream-fade-in animation
		const token = container.querySelector('.stream-token');
		expect(token).not.toBeNull();
		expect(token.classList.contains('stream-fade-in')).toBe(true);
		expect(token.textContent).toBe(' and more content');
		expect(originalP.textContent).toBe('First line and more content');
	});

	it('appends consecutive stream-token spans smoothly without destroying earlier tokens', () => {
		const container = document.createElement('div');
		patchStreamingHtml(container, '<p>Part 1</p>', { isStreaming: true });
		const originalP = container.querySelector('p');

		patchStreamingHtml(container, '<p>Part 1, Part 2</p>', { isStreaming: true });
		patchStreamingHtml(container, '<p>Part 1, Part 2, Part 3</p>', { isStreaming: true });

		expect(container.querySelector('p')).toBe(originalP);
		const tokens = container.querySelectorAll('.stream-token');
		expect(tokens.length).toBe(2);
		expect(tokens[0].textContent).toBe(', Part 2');
		expect(tokens[1].textContent).toBe(', Part 3');
		expect(originalP.textContent).toBe('Part 1, Part 2, Part 3');
	});

	it('adds new paragraphs as separate fade-in blocks without touching prior blocks', () => {
		const container = document.createElement('div');
		patchStreamingHtml(container, '<p>Paragraph 1</p>', { isStreaming: true });
		const p1 = container.querySelector('p');

		patchStreamingHtml(container, '<p>Paragraph 1</p><p>Paragraph 2</p>', { isStreaming: true });
		const paragraphs = container.querySelectorAll('p');

		expect(paragraphs.length).toBe(2);
		expect(paragraphs[0]).toBe(p1);
		expect(paragraphs[1].classList.contains('stream-fade-in')).toBe(true);
		expect(paragraphs[1].textContent).toBe('Paragraph 2');
	});

	it('normalizes container when streaming completes', () => {
		const container = document.createElement('div');
		patchStreamingHtml(container, '<p>Paragraph 1</p>', { isStreaming: true });
		patchStreamingHtml(container, '<p>Paragraph 1, more text</p>', { isStreaming: true });

		expect(container.querySelectorAll('.stream-token').length).toBe(1);

		// Streaming ends: clean up all streaming tokens
		normalizeStreamingContainer(container, '<p>Paragraph 1, more text</p>');
		expect(container.querySelectorAll('.stream-token').length).toBe(0);
		expect(container.querySelectorAll('.stream-fade-in').length).toBe(0);
		expect(container.innerHTML).toBe('<p>Paragraph 1, more text</p>');
	});

	it('handles code blocks and lists gracefully with fade-in', () => {
		const container = document.createElement('div');
		patchStreamingHtml(container, '<ul><li>Item 1</li></ul>', { isStreaming: true });
		expect(container.querySelector('ul').classList.contains('stream-fade-in')).toBe(true);

		patchStreamingHtml(container, '<ul><li>Item 1</li><li>Item 2</li></ul>', { isStreaming: true });
		const lis = container.querySelectorAll('li');
		expect(lis.length).toBe(2);
		expect(lis[1].classList.contains('stream-fade-in')).toBe(true);
	});

	it('replaces elements cleanly when inline markup structure changes', () => {
		const container = document.createElement('div');
		patchStreamingHtml(container, '<p>Plain text</p>', { isStreaming: true });
		patchStreamingHtml(container, '<p><strong>Bold text</strong></p>', { isStreaming: true });

		expect(container.querySelector('strong')).not.toBeNull();
		expect(container.textContent).toBe('Bold text');
	});
});
