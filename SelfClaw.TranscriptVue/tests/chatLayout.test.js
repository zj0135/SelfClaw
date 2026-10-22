// 主对话布局契约：消息列与输入框列共用 --chat-content-width，
// 宽窗口通过 tokens.css 的 min-width 断点放大该 token，避免全屏留白过大。
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { describe, expect, it } from 'vitest';

// vitest 以 TranscriptVue 目录为 cwd 运行，直接按包根解析。
const read = (relative) => readFileSync(resolve(process.cwd(), relative), 'utf8');

const tokens = read('src/styles/tokens.css');
const app = read('src/App.vue');
const composer = read('src/components/chat/composer/ComposerPanel.vue');

describe('chat layout width token', () => {
	it('tokens.css 定义 --chat-content-width 并提供宽屏断点', () => {
		expect(tokens).toMatch(/--chat-content-width:\s*728px/);
		const wideBlocks = tokens.match(/@media \(min-width: \d+px\)[\s\S]*?--chat-content-width/g) ?? [];
		expect(wideBlocks.length).toBeGreaterThanOrEqual(2);
	});

	it('transcript 内容列居中并与 token 同宽', () => {
		const contentBlock = app.match(/\.transcript-content \{[\s\S]*?\}/)?.[0] ?? '';
		expect(contentBlock).toContain('width: min(100%, var(--chat-content-width))');
		expect(contentBlock).toContain('margin-inline: auto');
	});

	it('composer 列宽复用同一 token，不再写死 728px', () => {
		const stackBlock = composer.match(/\.composer-stack \{[\s\S]*?\}/)?.[0] ?? '';
		expect(stackBlock).toContain('var(--chat-content-width)');
		expect(stackBlock).not.toContain('728px');
	});

	it('消息气泡上限跟随内容列，不再写死 760/620', () => {
		const mainBlock = app.match(/\.message-main \{[\s\S]*?\}/)?.[0] ?? '';
		expect(mainBlock).toContain('calc(var(--chat-content-width)');
		expect(mainBlock).not.toContain('760px');
		expect(app).not.toContain('min(58%, 620px)');
	});
});
