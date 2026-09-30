import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { defineComponent, h } from 'vue';
import { flushPromises, mount } from '@vue/test-utils';
import { createPluginTranscriptProjector, maximumPluginTranscriptBytes } from '../src/renderers/pluginTranscript.js';

const { bridge, subscribers } = vi.hoisted(() => {
    const subscribers = new Map();
    return { subscribers, bridge: { request: vi.fn(), on: vi.fn((type, handler) => { subscribers.set(type, handler); return () => subscribers.delete(type); }), post: vi.fn(), hasHost: () => true } };
});
vi.mock('../src/composables/hostBridge.js', () => ({ hostBridge: bridge, useHostBridge: () => bridge }));
import { usePluginViews } from '../src/composables/usePluginViews.js';

const ORIGIN = 'https://plugin.plugin.selfclaw.local';
const mounted = [];
let suspended = false;

afterEach(() => {
    mounted.splice(0).forEach((wrapper) => wrapper.unmount());
    vi.useRealTimers();
    vi.clearAllMocks();
    subscribers.clear();
    document.body.innerHTML = '';
    suspended = false;
});

beforeEach(() => {
    suspended = false;
});

function descriptor(key, slot = 'right') {
    const [pluginId, viewId] = key.split('/');
    return {
        key, pluginId, viewId, title: viewId, icon: 'puzzle', slot, origin: ORIGIN,
        url: `${ORIGIN}/${viewId}.html`, defaultWidth: slot === 'right' ? 380 : null,
        enabled: true, status: 'ready', permissions: ['ui.panel', 'ui.floating', 'host.transcript.read', 'host.composer.write'], networkOrigins: [],
    };
}

function fixture({ views = [], openViews = [], activeView = null } = {}) {
    bridge.request.mockImplementation(async (type, payload) => {
        if (type === 'plugin-host/get-views') return { views, openViews, activeView };
        if (type === 'plugin-host/open') return { view: views.find((candidate) => candidate.key === payload.viewKey) ?? descriptor(payload.viewKey), url: `/frame/${payload.viewKey}` };
        return { ok: true };
    });
    let pluginViews;
    mounted.push(mount(defineComponent({ setup() { pluginViews = usePluginViews({ isSuspended: () => suspended }); return () => h('div'); } })));
    return pluginViews;
}

// 帧在真实外壳里由 PluginFrame 注册；这里直接用元素替身，注册与身份判定走的是同一条路径。
function registerFrame(pluginViews, key) {
    const element = document.createElement('div');
    const posted = [];
    element.contentWindow = { postMessage: (message, origin) => posted.push({ message, origin }) };
    document.body.appendChild(element);
    pluginViews.registerFrame(key, element);
    return { element, posted };
}

function postToFrame(source, data) {
    window.dispatchEvent(new MessageEvent('message', { source, origin: ORIGIN, data: { __selfclaw: 1, ...data } }));
}

// 地标测量由 ResizeObserver / MutationObserver / resize 驱动、rAF 合并：断言前推进一轮。
async function refreshAnchors() {
    window.dispatchEvent(new Event('resize'));
    await new Promise((resolve) => window.requestAnimationFrame(resolve));
    await flushPromises();
}

// 持久化是防抖的（400ms），断言前等它落盘。
const settleSave = () => new Promise((resolve) => setTimeout(resolve, 450));

function move(target, x, y) {
    target.dispatchEvent(new MouseEvent('pointermove', { clientX: x, clientY: y, bubbles: true }));
}

it('coalesces in-flight opens and closes a result invalidated before its response', async () => {
    let finish;
    const pluginViews = fixture();
    bridge.request.mockImplementation(async (type) => {
        if (type === 'plugin-host/get-views') return { views: [], openViews: [] };
        if (type === 'plugin-host/open') return new Promise((resolve) => { finish = resolve; });
        return { ok: true };
    });
    await flushPromises();
    const first = pluginViews.open('plugin/one');
    const second = pluginViews.open('plugin/one');
    expect(first).toBe(second);
    expect(bridge.request.mock.calls.filter(([type]) => type === 'plugin-host/open')).toHaveLength(1);
    await pluginViews.close('plugin/one');
    finish({ view: descriptor('plugin/one'), url: '/one' });
    await first;
    expect(pluginViews.openViews.value).toEqual([]);
    expect(bridge.request).toHaveBeenCalledWith('plugin-host/close', { viewKey: 'plugin/one' });
});

it('restores saved views, keeps the docked active view and rejects messages from a forged origin', async () => {
    const pluginViews = fixture({
        views: [descriptor('plugin/one'), descriptor('plugin/hud', 'floating')],
        openViews: ['plugin/one', 'plugin/hud'],
        activeView: 'plugin/one',
    });
    await flushPromises();

    expect(pluginViews.openViews.value.map((view) => view.key)).toEqual(['plugin/one', 'plugin/hud']);
    expect(pluginViews.dockedViews.value.map((view) => view.key)).toEqual(['plugin/one']);
    expect(pluginViews.floatingViews.value.map((view) => view.key)).toEqual(['plugin/hud']);
    expect(pluginViews.activeKey.value).toBe('plugin/one');
    await settleSave();
    expect(bridge.request).toHaveBeenCalledWith('plugin-host/save-views', { views: ['plugin/one', 'plugin/hud'], activeView: 'plugin/one' });

    const { element, posted } = registerFrame(pluginViews, 'plugin/hud');
    window.dispatchEvent(new MessageEvent('message', {
        source: element.contentWindow, origin: 'https://forged.example',
        data: { __selfclaw: 1, kind: 'hello' },
    }));
    expect(posted).toHaveLength(0);

    postToFrame(element.contentWindow, { kind: 'hello' });
    expect(posted).toHaveLength(1);
    const handshake = posted[0].message;
    expect(posted[0].origin).toBe(ORIGIN);
    expect(handshake.type).toBe('handshake');
    // 握手必须一次带齐 slot 与地标：悬浮视图靠它决定自己的坐标与对齐方式。
    expect(handshake.payload.slot).toBe('floating');
    expect(handshake.payload.viewKey).toBe('plugin/hud');
    expect(handshake.payload.anchors).toHaveProperty('composer');
});

it('arms only the floating frame that declares an interactive rect and releases it on request', async () => {
    const pluginViews = fixture({
        views: [descriptor('plugin/hud', 'floating')],
        openViews: ['plugin/hud'],
    });
    await flushPromises();
    const { element, posted } = registerFrame(pluginViews, 'plugin/hud');

    expect(pluginViews.armedKey.value).toBe('');
    move(document.body, 50, 20);
    expect(pluginViews.armedKey.value).toBe('');

    postToFrame(element.contentWindow, { kind: 'notice', type: 'hit-regions', payload: { rects: [{ x: 10, y: 10, width: 100, height: 40 }] } });
    move(document.body, 50, 20);
    expect(pluginViews.armedKey.value).toBe('plugin/hud');

    // 已武装时指针仍在帧上（父文档看到的目标就是该帧元素）：保持武装，由插件侧决定何时离开。
    move(element, 500, 500);
    expect(pluginViews.armedKey.value).toBe('plugin/hud');

    // 插件自己要求的解除：外壳仍然回一条 hit-released（每次解除恰好一条通知），
    // 插件不需要区分是谁发起的。
    postToFrame(element.contentWindow, { kind: 'notice', type: 'hit-regions-release' });
    expect(pluginViews.armedKey.value).toBe('');
    expect(posted.filter(({ message }) => message.type === 'hit-released')).toHaveLength(1);

    // 指针跑到上层元素（对话框）上时立刻解除武装，不依赖插件配合，也不依赖子文档收到 pointerout。
    const before = posted.filter(({ message }) => message.type === 'hit-released').length;
    postToFrame(element.contentWindow, { kind: 'notice', type: 'hit-regions', payload: { rects: [{ x: 10, y: 10, width: 100, height: 40 }] } });
    move(document.body, 50, 20);
    expect(pluginViews.armedKey.value).toBe('plugin/hud');
    move(document.body, 900, 700);
    expect(pluginViews.armedKey.value).toBe('');
    expect(posted.filter(({ message }) => message.type === 'hit-released')).toHaveLength(before + 1);
});

it('ignores malformed regions and never arms while the shell suspends the layer', async () => {
    const pluginViews = fixture({
        views: [descriptor('plugin/hud', 'floating')],
        openViews: ['plugin/hud'],
    });
    await flushPromises();
    const { element } = registerFrame(pluginViews, 'plugin/hud');

    postToFrame(element.contentWindow, {
        kind: 'notice',
        type: 'hit-regions',
        payload: { rects: [{ x: Number.NaN, y: 0, width: 10, height: 10 }, { x: 0, y: 0, width: -4, height: 10 }] },
    });
    move(document.body, 1, 1);
    expect(pluginViews.armedKey.value).toBe('');

    postToFrame(element.contentWindow, { kind: 'notice', type: 'hit-regions', payload: { rects: [{ x: 0, y: 0, width: 200, height: 200 }] } });
    suspended = true;
    move(document.body, 10, 10);
    expect(pluginViews.armedKey.value).toBe('');
});

it('closes a view on its own request and refuses composer writes without the permission', async () => {
    const pluginViews = fixture({
        views: [descriptor('plugin/hud', 'floating')],
        openViews: ['plugin/hud'],
    });
    await flushPromises();
    const { element, posted } = registerFrame(pluginViews, 'plugin/hud');

    postToFrame(element.contentWindow, { kind: 'request', id: 'r1', op: 'composer.insert', args: { text: 'hi' } });
    await flushPromises();
    expect(posted.at(-1).message).toMatchObject({ kind: 'response', id: 'r1', ok: true });

    postToFrame(element.contentWindow, { kind: 'request', id: 'r2', op: 'view.close' });
    await flushPromises();
    expect(bridge.request).toHaveBeenCalledWith('plugin-host/close', { viewKey: 'plugin/hud' });
    expect(posted.at(-1).message).toMatchObject({ kind: 'response', id: 'r2', ok: true });

    // 权限来自宿主推回来的描述符，不是插件自报；这里模拟一条没有 host.composer.write 的视图。
    const other = fixture({ views: [{ ...descriptor('plugin/plain', 'floating'), permissions: ['ui.floating'] }], openViews: ['plugin/plain'] });
    bridge.request.mockImplementation(async (type, payload) => type === 'plugin-host/get-views'
        ? { views: [{ ...descriptor('plugin/plain', 'floating'), permissions: ['ui.floating'] }], openViews: ['plugin/plain'] }
        : { view: { ...descriptor(payload.viewKey, 'floating'), permissions: ['ui.floating'] }, url: '/plain' });
    await flushPromises();
    const frame = registerFrame(other, 'plugin/plain');
    postToFrame(frame.element.contentWindow, { kind: 'request', id: 'r3', op: 'composer.insert', args: { text: 'hi' } });
    await flushPromises();
    expect(frame.posted.at(-1).message).toMatchObject({ kind: 'response', id: 'r3', ok: false });
    expect(frame.posted.at(-1).message.error).toContain('host.composer.write');
});

// 地标在窗口坐标里测量，但插件只认得自己视口里的数：推送前必须换算到每个帧自己的坐标系。
it('localizes anchors into the frame that receives them', async () => {
    const pluginViews = fixture({
        views: [descriptor('plugin/hud', 'floating')],
        openViews: ['plugin/hud'],
    });
    await flushPromises();
    const { element, posted } = registerFrame(pluginViews, 'plugin/hud');
    const hud = document.createElement('div');
    hud.dataset.anchor = 'composer';
    hud.getBoundingClientRect = () => ({ x: 300, y: 700, width: 600, height: 100 });
    document.body.appendChild(hud);
    // 帧自己不在窗口原点：视口的左上角是 (280, 46)。
    element.getBoundingClientRect = () => ({ x: 280, y: 46, width: 600, height: 754, right: 880, bottom: 800 });

    // 地标是被观察的：先让外壳测到它，再握手/推送。
    await refreshAnchors();
    postToFrame(element.contentWindow, { kind: 'hello' });
    expect(posted.at(-1).message.payload.anchors.composer).toEqual({ x: 20, y: 654, width: 600, height: 100 });

    hud.getBoundingClientRect = () => ({ x: 300, y: 600, width: 600, height: 100 });
    await refreshAnchors();

    const pushed = posted.filter(({ message }) => message.type === 'anchors-changed').at(-1)?.message.payload;
    expect(pushed?.composer).toEqual({ x: 20, y: 554, width: 600, height: 100 });
});

it('pushes measured anchors after the shell layout changes', async () => {
    const pluginViews = fixture({
        views: [descriptor('plugin/one')],
        openViews: ['plugin/one'],
    });
    await flushPromises();
    const { element, posted } = registerFrame(pluginViews, 'plugin/one');
    postToFrame(element.contentWindow, { kind: 'hello' });
    expect(posted.at(-1).message.payload.anchors.composer).toBeNull();

    const composer = document.createElement('div');
    composer.dataset.anchor = 'composer';
    composer.getBoundingClientRect = () => ({ x: 12, y: 640, width: 800, height: 96 });
    document.body.appendChild(composer);
    window.dispatchEvent(new Event('resize'));
    await new Promise((resolve) => window.requestAnimationFrame(resolve));
    await flushPromises();

    const anchors = posted.filter(({ message }) => message.type === 'anchors-changed').at(-1)?.message.payload;
    expect(anchors?.composer).toEqual({ x: 12, y: 640, width: 800, height: 96 });
});

it('drops every frame of a plugin when the host evicts it', async () => {
    const pluginViews = fixture({
        views: [descriptor('plugin/hud', 'floating')],
        openViews: ['plugin/hud'],
    });
    await flushPromises();
    const { element } = registerFrame(pluginViews, 'plugin/hud');
    postToFrame(element.contentWindow, { kind: 'notice', type: 'hit-regions', payload: { rects: [{ x: 0, y: 0, width: 100, height: 100 }] } });
    move(document.body, 10, 10);
    expect(pluginViews.armedKey.value).toBe('plugin/hud');

    subscribers.get('plugin-host/evict')({ pluginId: 'plugin' });
    await flushPromises();

    expect(pluginViews.openViews.value).toEqual([]);
    expect(pluginViews.armedKey.value).toBe('');
});

it('caps a recoverable plugin snapshot in UTF-8 and reuses unchanged projected items', () => {
    const project = createPluginTranscriptProjector();
    const items = Array.from({ length: 500 }, (_, id) => ({ id: String(id), kind: 'message', segments: [{ kind: 'content', markdown: '中文'.repeat(1000) }] }));
    const first = project({ revision: 1, items });
    expect(new TextEncoder().encode(JSON.stringify({ __selfclaw: 1, kind: 'event', type: 'transcript', payload: first })).length).toBeLessThanOrEqual(maximumPluginTranscriptBytes);
    expect(first.truncated).toBe(true);
    expect(first.totalItems).toBe(500);
    const next = project({ revision: 2, items });
    expect(next.items[0]).toBe(first.items[0]);
    expect(next.items.at(-1).id).toBe('499');
});
