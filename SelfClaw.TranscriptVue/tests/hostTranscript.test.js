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

it('applies an outcome-only patch without changing fragment identity or inventing assistant output', async () => {
    const panel = fixture();
    let state;
    panel.transcript.on((value) => { state = value; });
    const user = { id: 'u1', kind: 'message', role: 'user', status: 'sealed', segments: [] };
    const outcome = { id: 'turn-outcome:t1', kind: 'turn-outcome', role: 'system', segments: [], turnOutcome: { turnId: 't1', status: 'running' } };
    panel.send({ type: 'replaceState', revision: 1, items: [user, outcome], conversations: [] });
    await flushPromises();
    panel.send({ type: 'patchState', revision: 2, baseRevision: 1, upsertItems: [{ ...outcome,
        turnOutcome: { turnId: 't1', status: 'failed', errorMessage: 'no output', usage: { totalTokens: null } } }] });
    await flushPromises();
    expect(state.items[0]).toBe(user);
    expect(state.items).toHaveLength(2);
    expect(state.items.some((item) => item.role === 'assistant')).toBe(false);
    expect(state.items[1].turnOutcome.status).toBe('failed');
    expect(state.items[1].turnOutcome.usage.totalTokens).toBeNull();
    expect(panel.posted).toContainEqual({ type: 'transcript-applied', revision: 2 });
});

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

it('clears the todo plan as soon as a push omits the field', async () => {
    const panel = fixture();
    const seen = [];
    panel.transcript.on((state) => seen.push(state.todo ?? null));
    const todo = { items: [{ content: '投影到 transcript 卡片', status: 'in_progress', activeForm: '正在投影' }] };
    const first = { ...frame(1), selectedConversationId: 'conv-1', todo };
    panel.send(first);
    await flushPromises();
    expect(seen.at(-1)).toEqual(todo);

    // 后端序列化是 WhenWritingNull：无计划时 `todo` 整个字段缺席。补丁沿用上一份快照，
    // 若不显式重置，切会话后 dock 会一直显示上一条会话的计划。
    panel.send({ type: 'patchState', revision: 2, baseRevision: 1, selectedConversationId: 'conv-2', upsertItems: [] });
    await flushPromises();
    expect(seen.at(-1)).toBeNull();
    expect(seen.at(-1)).not.toEqual(todo);

    // 整份替换同理：新快照不带字段时不得继承上一条计划。
    panel.send({ ...frame(3), selectedConversationId: 'conv-2' });
    await flushPromises();
    expect(seen.at(-1)).toBeNull();
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
