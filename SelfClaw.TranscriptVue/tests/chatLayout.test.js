// 主对话布局契约：消息列与输入框列共用 --chat-content-width（列宽上限）与
// --chat-inline-inset（列两侧留白），宽窗口通过 tokens.css 的 min-width 断点放大列宽，
// 窄窗口只收紧留白，两列在任何宽度下同宽同轴，左右留白一致。
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { describe, expect, it } from 'vitest';

// vitest 以 TranscriptVue 目录为 cwd 运行，直接按包根解析。
const read = (relative) => readFileSync(resolve(process.cwd(), relative), 'utf8');

const tokens = read('src/styles/tokens.css');
const app = read('src/App.vue');
const composer = read('src/components/chat/composer/ComposerPanel.vue');

describe('chat layout width token', () => {
	it('tokens.css 定义列宽与留白 token，并提供宽屏/窄屏断点', () => {
		expect(tokens).toMatch(/--chat-content-width:\s*728px/);
		expect(tokens).toMatch(/--chat-inline-inset:\s*72px/);
		const wideBlocks = tokens.match(/@media \(min-width: \d+px\)[\s\S]*?--chat-content-width/g) ?? [];
		expect(wideBlocks.length).toBeGreaterThanOrEqual(2);
		const narrowBlock = tokens.match(/@media \(max-width: \d+px\)[\s\S]*?--chat-inline-inset/g) ?? [];
		expect(narrowBlock.length).toBeGreaterThanOrEqual(1);
	});

	it('transcript 内容列居中并与 token 同宽', () => {
		const contentBlock = app.match(/\.transcript-content \{[\s\S]*?\}/)?.[0] ?? '';
		expect(contentBlock).toContain('width: min(100%, var(--chat-content-width))');
		expect(contentBlock).toContain('margin-inline: auto');
	});

	it('transcript 内边距用留白 token 补齐滚动条占位，正文列不因滚动条变窄', () => {
		const scrollBlock = app.match(/\.transcript-scroll \{[\s\S]*?\}/)?.[0] ?? '';
		expect(scrollBlock).toContain('calc((var(--chat-inline-inset) - 2 * var(--scrollbar-size)) / 2)');
		expect(scrollBlock).toContain('scrollbar-gutter: stable both-edges');
		expect(app).not.toContain('11.5vw');
		expect(app).toMatch(/::-webkit-scrollbar \{[^}]*width: var\(--scrollbar-size\)/);
	});

	it('composer 列宽复用同一组 token，不再写死 728px / 72px / 28px', () => {
		const stackBlock = composer.match(/\.composer-stack \{[\s\S]*?\}/)?.[0] ?? '';
		expect(stackBlock).toContain('calc(100% - var(--chat-inline-inset))');
		expect(stackBlock).toContain('var(--chat-content-width)');
		expect(stackBlock).not.toContain('728px');
		expect(composer).not.toContain('calc(100% - 72px)');
		expect(composer).not.toContain('calc(100% - 28px)');
	});

	it('助手消息铺满内容列（右边缘与输入框对齐），用户气泡仍按 token 收窄', () => {
		const mainBlock = app.match(/\.message-main \{[\s\S]*?\}/)?.[0] ?? '';
		expect(mainBlock).toContain('flex: 1 1 auto');
		expect(mainBlock).toContain('max-width: 100%');
		expect(app).not.toContain('min(76%');

		const userBlock = app.match(/\.message-row\.user \.message-main \{[\s\S]*?\}/)?.[0] ?? '';
		expect(userBlock).toContain('calc(var(--chat-content-width)');
	});
});
