import { afterEach, expect, it, vi } from 'vitest';
import { flushPromises } from '@vue/test-utils';
import { createHostBridge } from '../src/composables/hostBridge.js';
import { createTranscriptBridge } from '../src/composables/transcriptBridge.js';

const disposers = [];
afterEach(() => { disposers.splice(0).forEach((dispose) => dispose()); vi.restoreAllMocks(); });
function fixture(afterRender = () => Promise.resolve()) {
    let incoming;
    const posted = [];
    const host = createHostBridge({ postMessage: (message) => posted.push(message),
        addEventListener: (_, handler) => { incoming = handler; }, removeEventListener() {} });
    const transcript = createTranscriptBridge(host, afterRender);
    disposers.push(() => transcript.dispose(), () => host.dispose());
    return { host, transcript, posted, send: (payload) => incoming({ data: payload }) };
}
const frame = (revision, text = 'current') => ({ type: 'replaceState', revision, items: [{ id: 'message', markdown: text }], conversations: [] });

it('isolates a failing raw subscriber and still delivers and acknowledges a rendered transcript', async () => {
    vi.spyOn(console, 'error').mockImplementation(() => {});
    const panel = fixture();
    panel.host.on('replaceState', () => { throw new Error('optional observer failed'); });
    const rendered = [];
    panel.transcript.on((state) => rendered.push(state.items[0].markdown));
    panel.send(frame(1));
    await flushPromises();
    expect(rendered).toEqual(['current']);
    expect(panel.posted).toContainEqual({ type: 'transcript-applied', revision: 1 });
});

it('does not acknowledge a failed render and recovers on a new complete snapshot', async () => {
    vi.spyOn(console, 'error').mockImplementation(() => {});
    let fail = true;
    const panel = fixture();
    panel.transcript.on(() => { if (fail) throw new Error('render failed'); });
    panel.send(frame(1));
    await flushPromises();
    expect(panel.posted).toContainEqual({ type: 'transcript-rejected', revision: 1 });
    expect(panel.posted).not.toContainEqual({ type: 'transcript-applied', revision: 1 });
    fail = false;
    panel.send(frame(2, 'recovered'));
    await flushPromises();
    expect(panel.posted).toContainEqual({ type: 'transcript-applied', revision: 2 });
    expect(panel.transcript.error.value).toBe('');
});

it('rejects a patch against the wrong version and replays the current snapshot when a view returns', async () => {
    const panel = fixture();
    const first = vi.fn();
    const unsubscribe = panel.transcript.on(first);
    panel.send(frame(4));
    await flushPromises();
    panel.send({ type: 'patchState', revision: 5, baseRevision: 3, upsertItems: [] });
    await flushPromises();
    expect(panel.posted).toContainEqual({ type: 'transcript-rejected', revision: 5 });
    unsubscribe();
    panel.send(frame(6, 'restored'));
    const restored = vi.fn();
    panel.transcript.on(restored);
    await flushPromises();
    expect(restored).toHaveBeenLastCalledWith(expect.objectContaining({ revision: 6 }));
    expect(panel.posted).toContainEqual({ type: 'transcript-applied', revision: 6 });
});

it('waits for rendering and accepts an explicit Vue error before the acknowledgement', async () => {
    let renderDone;
    const panel = fixture(() => new Promise((resolve) => { renderDone = resolve; }));
    panel.transcript.on(() => {});
    panel.send(frame(1));
    expect(panel.posted.some((message) => message.type === 'transcript-applied')).toBe(false);
    panel.transcript.reportRenderFailure(new Error('Vue render error'));
    renderDone();
    await flushPromises();
    expect(panel.posted.some((message) => message.type === 'transcript-applied')).toBe(false);
});
