import { afterEach, expect, it, vi } from 'vitest';
import { defineComponent, h } from 'vue';
import { flushPromises, mount } from '@vue/test-utils';
import { createPluginTranscriptProjector, maximumPluginTranscriptBytes } from '../src/renderers/pluginTranscript.js';

const { bridge, subscribers } = vi.hoisted(() => {
    const subscribers = new Map();
    return { subscribers, bridge: { request: vi.fn(), on: vi.fn((type, handler) => { subscribers.set(type, handler); return () => subscribers.delete(type); }), post: vi.fn(), hasHost: () => true } };
});
vi.mock('../src/composables/hostBridge.js', () => ({ hostBridge: bridge, useHostBridge: () => bridge }));
import { usePluginPanels } from '../src/composables/usePluginPanels.js';

const mounted = [];
afterEach(() => { mounted.splice(0).forEach((wrapper) => wrapper.unmount()); vi.useRealTimers(); vi.clearAllMocks(); subscribers.clear(); });
const panel = (key) => ({ key, pluginId: key.split('/')[0], permissions: ['ui.panel', 'host.transcript.read'], origin: 'https://plugin.plugin.selfclaw.local' });
function fixture() {
    let panels;
    mounted.push(mount(defineComponent({ setup() { panels = usePluginPanels(); return () => h('div'); } })));
    return panels;
}

it('coalesces in-flight opens and closes a result invalidated before its response', async () => {
    let finish;
    bridge.request.mockImplementation(async (type, payload) => {
        if (type === 'plugin-host/get-panels') return { panels: [], tabs: [] };
        if (type === 'plugin-host/open') return new Promise((resolve) => { finish = resolve; });
        return { ok: true };
    });
    const panels = fixture();
    await flushPromises();
    const first = panels.open('plugin/one');
    const second = panels.open('plugin/one');
    expect(first).toBe(second);
    expect(bridge.request.mock.calls.filter(([type]) => type === 'plugin-host/open')).toHaveLength(1);
    await panels.close('plugin/one');
    finish({ panel: panel('plugin/one'), url: '/one' });
    await first;
    expect(panels.tabs.value).toEqual([]);
    expect(bridge.request).toHaveBeenCalledWith('plugin-host/close', { panelKey: 'plugin/one' });
});

it('restores the saved active tab and rejects messages from an unrecognized frame origin', async () => {
    bridge.request.mockImplementation(async (type, payload) => type === 'plugin-host/get-panels'
        ? { panels: [panel('plugin/one'), panel('plugin/two')], tabs: ['plugin/one', 'plugin/two'], activeKey: 'plugin/one' }
        : { panel: panel(payload.panelKey), url: '/panel', ok: true });
    const panels = fixture();
    await flushPromises();
    expect(panels.activeKey.value).toBe('plugin/one');
    const postMessage = vi.fn();
    const source = { postMessage };
    panels.registerFrame('plugin/one', { contentWindow: source });
    window.dispatchEvent(new MessageEvent('message', { source, origin: 'https://forged.example', data: { __selfclaw: 1, kind: 'hello' } }));
    expect(postMessage).not.toHaveBeenCalled();
    window.dispatchEvent(new MessageEvent('message', { source, origin: 'https://plugin.plugin.selfclaw.local', data: { __selfclaw: 1, kind: 'hello' } }));
    expect(postMessage).toHaveBeenCalledWith(expect.objectContaining({ type: 'handshake' }), 'https://plugin.plugin.selfclaw.local');
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
