import { effectScope, reactive } from 'vue';
import { afterEach, expect, it, vi } from 'vitest';
import { useChatComposer } from '../src/composables/useChatComposer.js';

let scope;
afterEach(() => scope?.stop());

function createComposer(session = { isBusy: false }) {
	const response = Promise.withResolvers();
	const bridge = { request: vi.fn(() => response.promise), post: vi.fn() };
	const workspace = { refresh: vi.fn(async () => {}) };
	const scrolling = { resumeFollow: vi.fn() };
	scope = effectScope();
	const state = reactive(session);
	const composer = scope.run(() => useChatComposer(state, workspace, scrolling, bridge));
	return { composer, bridge, response, workspace, scrolling, state };
}

it('keeps the draft until accepted, prevents duplicate submission, then refreshes a provisioned workspace', async () => {
	const { composer, bridge, response, workspace, scrolling } = createComposer();
	const accept = vi.fn();
	const submission = { prompt: '  review this  ', workspaceMode: 'worktree', accept };
	const sent = composer.submit(submission);
	await composer.submit(submission);
	expect(bridge.request).toHaveBeenCalledExactlyOnceWith('send-prompt',
		expect.objectContaining({ modelProfileId: null, prompt: 'review this', workspaceMode: 'worktree', clientRequestId: expect.any(String), conversationId: expect.any(String), newConversation: true }), { timeout: 120000 });
	expect(accept).not.toHaveBeenCalled();
	response.resolve({ accepted: true });
	await sent;
	expect(accept).toHaveBeenCalledOnce();
	expect(scrolling.resumeFollow).toHaveBeenCalledOnce();
	expect(workspace.refresh).toHaveBeenCalledWith();
	expect(composer.submitting.value).toBe(false);
});

it('retains a rejected draft and allows retry', async () => {
	const { composer, bridge, workspace } = createComposer();
	bridge.request.mockResolvedValueOnce({ accepted: false, error: 'Workspace unavailable' }).mockResolvedValueOnce({ accepted: true });
	const accept = vi.fn();
	await composer.submit({ prompt: 'retry this', accept });
	expect(composer.error.value).toBe('Workspace unavailable');
	expect(accept).not.toHaveBeenCalled();
	expect(workspace.refresh).not.toHaveBeenCalled();
	await composer.submit({ prompt: 'retry this', accept });
	expect(composer.error.value).toBe('');
	expect(accept).toHaveBeenCalledOnce();
});

it('does not alter a discarded view when a submission response arrives after unmount', async () => {
	const { composer, response, workspace, scrolling } = createComposer();
	const accept = vi.fn();
	const sent = composer.submit({ prompt: 'send this', accept });
	scope.stop();
	response.resolve({ accepted: true });
	await sent;
	expect(accept).not.toHaveBeenCalled();
	expect(workspace.refresh).not.toHaveBeenCalled();
	expect(scrolling.resumeFollow).not.toHaveBeenCalled();
});

it('queues a Direct submission while a turn is busy', async () => {
	const { composer, bridge, response } = createComposer({ isBusy: true, agentMode: 'direct' });
	const accept = vi.fn();
	const sent = composer.submit({ prompt: 'queued while busy', accept });
	expect(bridge.request).toHaveBeenCalledWith('send-prompt',
		expect.objectContaining({ prompt: 'queued while busy' }), expect.anything());
	response.resolve({ accepted: true });
	await sent;
	expect(accept).toHaveBeenCalledOnce();
});

it('still blocks a busy CLI submission', async () => {
	const { composer, bridge } = createComposer({ isBusy: true, agentMode: 'cli' });
	const accept = vi.fn();
	await composer.submit({ prompt: 'cli busy', accept });
	expect(bridge.request).not.toHaveBeenCalled();
	expect(accept).not.toHaveBeenCalled();
});

it('retains the durable client identity after an ACK timeout', async () => {
    const { composer, bridge } = createComposer({ isBusy: true, agentMode: 'direct', selectedConversationId: 'A' });
    bridge.request.mockRejectedValueOnce(new Error('timeout')).mockResolvedValueOnce({ accepted: true });
    await composer.submit({ prompt: 'same draft' });
    await composer.submit({ prompt: 'same draft' });
    expect(bridge.request.mock.calls[0][1]).toEqual(bridge.request.mock.calls[1][1]);
});

it('does not clear another conversation draft or change its scroll when an old ACK arrives', async () => {
    const { composer, response, state, workspace, scrolling } = createComposer({ isBusy: true, agentMode: 'direct', selectedConversationId: 'A' });
    const accept = vi.fn();
    const pending = composer.submit({ prompt: 'for A', accept });
    state.selectedConversationId = 'B';
    response.resolve({ accepted: true, conversationId: 'A' });
    await pending;
    expect(accept).not.toHaveBeenCalled();
    expect(workspace.refresh).not.toHaveBeenCalled();
    expect(scrolling.resumeFollow).not.toHaveBeenCalled();
});

it('accepting a queued draft preserves the transcript reading position', async () => {
    const { composer, response, scrolling } = createComposer({ isBusy: true, agentMode: 'direct', selectedConversationId: 'A' });
    const pending = composer.submit({ prompt: 'queued' });
    response.resolve({ accepted: true });
    await pending;
    expect(scrolling.resumeFollow).not.toHaveBeenCalled();
});

it('sends a steer bound to the host turn and clears the draft only after the ACK', async () => {
    const { composer, bridge, response } = createComposer({ isBusy: true, agentMode: 'direct' });
    const accept = vi.fn();
    const pending = composer.submit({ prompt: 'adjust now', mode: 'steer', targetTurnId: 'turn-1', accept });

    expect(bridge.request).toHaveBeenCalledWith('send-prompt', expect.objectContaining({
        prompt: 'adjust now', kind: 'steer', targetTurnId: 'turn-1' }), expect.anything());
    expect(accept).not.toHaveBeenCalled();
    response.resolve({ accepted: true });
    await pending;
    expect(accept).toHaveBeenCalledOnce();
    expect(composer.error.value).toBe('');
});

it('reuses the steer identity and route after a timeout and never reuses it for a follow-up', async () => {
    const { composer, bridge } = createComposer({ isBusy: true, agentMode: 'direct', selectedConversationId: 'A' });
    bridge.request.mockRejectedValueOnce(new Error('timeout')).mockResolvedValue({ accepted: true });

    await composer.submit({ prompt: 'same draft', mode: 'steer', targetTurnId: 'turn-1' });
    await composer.submit({ prompt: 'same draft', mode: 'steer', targetTurnId: 'turn-1' });
    expect(bridge.request.mock.calls[0][1]).toEqual(bridge.request.mock.calls[1][1]);
    expect(bridge.request.mock.calls[1][1]).toMatchObject({ kind: 'steer', targetTurnId: 'turn-1' });

    await composer.submit({ prompt: 'same draft' });
    expect(bridge.request.mock.calls[2][1].clientRequestId)
        .not.toBe(bridge.request.mock.calls[1][1].clientRequestId);
    expect(bridge.request.mock.calls[2][1].kind).toBeUndefined();
});

it('reports a host steer fallback as a notice while still accepting the draft', async () => {
    const { composer, response } = createComposer({ isBusy: true, agentMode: 'direct' });
    const accept = vi.fn();
    const pending = composer.submit({ prompt: 'needs a new turn', mode: 'steer', targetTurnId: 'turn-1', accept });
    response.resolve({ accepted: true, reason: 'hook-requires-new-turn' });
    await pending;

    expect(composer.notice.value).toContain('完整审查');
    expect(composer.error.value).toBe('');
    expect(accept).toHaveBeenCalledOnce();
});

it('keeps the full draft and the retry identity when the host rejects for capacity', async () => {
    const { composer, bridge } = createComposer({ isBusy: true, agentMode: 'direct', selectedConversationId: 'A' });
    bridge.request.mockResolvedValueOnce({ accepted: false, error: '队列已满，请先等待或取消部分输入。' })
        .mockResolvedValueOnce({ accepted: true });
    const accept = vi.fn();

    await composer.submit({ prompt: 'too many', accept });
    expect(composer.error.value).toContain('队列已满');
    expect(accept).not.toHaveBeenCalled();

    await composer.submit({ prompt: 'too many', accept });
    expect(bridge.request.mock.calls[0][1]).toEqual(bridge.request.mock.calls[1][1]);
    expect(accept).toHaveBeenCalledOnce();
});
