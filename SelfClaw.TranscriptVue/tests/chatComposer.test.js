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
