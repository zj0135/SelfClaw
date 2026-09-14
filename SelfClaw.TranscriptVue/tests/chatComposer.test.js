import { effectScope, reactive } from 'vue';
import { afterEach, expect, it, vi } from 'vitest';
import { useChatComposer } from '../src/composables/useChatComposer.js';

let scope;
afterEach(() => scope?.stop());

function createComposer() {
	const response = Promise.withResolvers();
	const bridge = { request: vi.fn(() => response.promise), post: vi.fn() };
	const workspace = { refresh: vi.fn(async () => {}) };
	const scrolling = { resumeFollow: vi.fn() };
	scope = effectScope();
	const composer = scope.run(() => useChatComposer(reactive({ isBusy: false }), workspace, scrolling, bridge));
	return { composer, bridge, response, workspace, scrolling };
}

it('keeps the draft until accepted, prevents duplicate submission, then refreshes a provisioned workspace', async () => {
	const { composer, bridge, response, workspace, scrolling } = createComposer();
	const accept = vi.fn();
	const submission = { prompt: '  review this  ', workspaceMode: 'worktree', accept };
	const sent = composer.submit(submission);
	await composer.submit(submission);
	expect(bridge.request).toHaveBeenCalledExactlyOnceWith('send-prompt',
		{ modelProfileId: null, prompt: 'review this', workspaceMode: 'worktree' }, { timeout: 120000 });
	expect(accept).not.toHaveBeenCalled();
	response.resolve({ accepted: true });
	await sent;
	expect(accept).toHaveBeenCalledOnce();
	expect(scrolling.resumeFollow).toHaveBeenCalledOnce();
	expect(workspace.refresh).toHaveBeenCalledWith(true);
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
