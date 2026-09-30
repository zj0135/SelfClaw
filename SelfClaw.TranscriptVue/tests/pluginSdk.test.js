import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { JSDOM } from 'jsdom';
import { expect, it, vi } from 'vitest';

// plugin-sdk.js 是插件作者唯一的契约面，而它只在跨源 iframe 里生效（window.parent !== window），
// 所以用一次性 JSDOM 当插件文档，把外壳换成 postMessage 收集器。这里钉住的是悬浮视图的行为：
// 交互矩形的自动发布、离开矩形后的 release、解除武装的通知，以及面板不受这套机制影响。
const sdkSource = readFileSync(resolve(process.cwd(), '../SelfClaw.Desktop/Assets/plugin-sdk.js'), 'utf8');
const SHELL_ORIGIN = 'https://appassets.selfclaw.local';

function createPluginDocument() {
	const dom = new JSDOM('<!doctype html><html><head></head><body></body></html>', {
		url: 'https://plugin.plugin.selfclaw.local/hud.html?__selfclaw_view=plugin%2Fhud',
		pretendToBeVisual: true,
	});
	const { window } = dom;
	const posted = [];
	Object.defineProperty(window, 'parent', { configurable: true, value: { postMessage: (message) => posted.push(message) } });
	const run = new Function(
		'window', 'document', 'requestAnimationFrame', 'cancelAnimationFrame', 'MutationObserver', 'ResizeObserver', 'console',
		sdkSource);
	run(window, window.document, window.requestAnimationFrame.bind(window), window.cancelAnimationFrame.bind(window),
		window.MutationObserver, undefined, console);

	const shellEvent = (type, payload) => window.dispatchEvent(new window.MessageEvent('message', {
		origin: SHELL_ORIGIN,
		data: { __selfclaw: 1, kind: 'event', type, payload },
	}));

	const frame = () => new Promise((done) => window.requestAnimationFrame(() => done()));

	return {
		window,
		posted,
		shellEvent,
		frame,
		handshake: (slot, permissions = []) => shellEvent('handshake', {
			viewKey: 'plugin/hud', slot, permissions,
			appearance: { theme: 'dark', mode: 'system', uiFontFamily: '', uiFontScale: 1, codeFontFamily: '', codeFontScale: 1 },
			anchors: { titlebar: null, sidebar: null, stage: null, composer: { x: 1, y: 2, width: 3, height: 4 }, dock: null },
		}),
		markInteractive: (rect) => {
			const element = window.document.createElement('div');
			element.setAttribute('data-selfclaw-interactive', '');
			element.getBoundingClientRect = () => ({ left: rect.x, top: rect.y, right: rect.x + rect.width, bottom: rect.y + rect.height, width: rect.width, height: rect.height });
			window.document.body.appendChild(element);
			return element;
		},
		pointerMove: (x, y) => window.document.body.dispatchEvent(new window.MouseEvent('pointermove', { clientX: x, clientY: y, bubbles: true })),
		notices: (type) => posted.filter((message) => message.kind === 'notice' && message.type === type),
		requests: (op) => posted.filter((message) => message.kind === 'request' && message.op === op),
	};
}

it('publishes interactive rects from the marked elements and releases when the pointer leaves them', async () => {
	const plugin = createPluginDocument();
	expect(plugin.posted[0]).toMatchObject({ kind: 'hello' });

	plugin.markInteractive({ x: 10, y: 20, width: 100, height: 40 });
	plugin.handshake('floating', ['host.composer.write']);

	expect(plugin.window.selfclaw.slot).toBe('floating');
	expect(plugin.window.selfclaw.viewKey).toBe('plugin/hud');
	expect(plugin.window.selfclaw.layout.anchors.composer).toEqual({ x: 1, y: 2, width: 3, height: 4 });
	// 悬浮视图的根背景默认透明，否则插件会盖住整个应用。
	expect(plugin.window.document.getElementById('__selfclaw_floating_defaults')).not.toBeNull();

	await plugin.frame();
	expect(plugin.notices('hit-regions').at(-1).payload.rects).toEqual([{ x: 10, y: 20, width: 100, height: 40 }]);

	// 指针在矩形内：不打扰外壳（此时外壳已武装本帧）。
	plugin.pointerMove(50, 30);
	expect(plugin.notices('hit-regions-release')).toHaveLength(0);

	// 指针离开矩形：立刻交还指针，不必等宿主发现。
	plugin.pointerMove(500, 500);
	expect(plugin.notices('hit-regions-release')).toHaveLength(1);

	// 外壳解除武装的通知送达插件的处理器，插件据此清理自己的 hover 态。
	const released = vi.fn();
	plugin.window.selfclaw.on('hit-released', released);
	plugin.shellEvent('hit-released');
	expect(released).toHaveBeenCalledTimes(1);

	// 地标是事实推送：变化后立即可读。
	plugin.shellEvent('anchors-changed', { titlebar: null, sidebar: null, stage: null, composer: null, dock: null });
	expect(plugin.window.selfclaw.layout.anchors.composer).toBeNull();

	// 关闭自己走外壳本地处理，不需要额外权限。
	plugin.window.selfclaw.close();
	expect(plugin.requests('view.close')).toHaveLength(1);
});

it('leaves the docked slot on the ordinary pointer model', async () => {
	const plugin = createPluginDocument();
	plugin.markInteractive({ x: 10, y: 20, width: 100, height: 40 });
	plugin.handshake('right', []);

	expect(plugin.window.document.getElementById('__selfclaw_floating_defaults')).toBeNull();
	await plugin.frame();
	plugin.pointerMove(50, 30);
	plugin.pointerMove(500, 500);
	expect(plugin.notices('hit-regions')).toHaveLength(0);
	expect(plugin.notices('hit-regions-release')).toHaveLength(0);
});
