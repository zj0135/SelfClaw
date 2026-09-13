import { effectScope, nextTick, reactive } from 'vue';
import { afterEach, expect, it, vi } from 'vitest';
import { useChatTurnStatus } from '../src/composables/useChatTurnStatus.js';

let scope;
afterEach(() => { scope?.stop(); vi.useRealTimers(); });

it('keeps time through text updates, resets on conversation changes, and stops its timer on unmount', async () => {
	vi.useFakeTimers();
	const session = reactive({ isBusy: true, selectedConversationId: 'first', items: [] });
	scope = effectScope();
	const status = scope.run(() => useChatTurnStatus(session));
	vi.advanceTimersByTime(2000);
	expect(status.value.elapsedText).toBe('2s');
	session.items = [{ role: 'assistant', isThinking: true, segments: [{ kind: 'content', markdown: 'progress' }] }];
	await nextTick();
	vi.advanceTimersByTime(1000);
	expect(status.value.elapsedText).toBe('3s');
	session.selectedConversationId = 'second';
	await nextTick();
	expect(status.value.elapsedText).toBe('0s');
	scope.stop();
	expect(vi.getTimerCount()).toBe(0);
});

it('does not duplicate the preparing indicator and hides when the turn completes', async () => {
	vi.useFakeTimers();
	const session = reactive({ isBusy: true, selectedConversationId: 'first', items: [{ role: 'assistant', isThinking: true, segments: [] }] });
	scope = effectScope();
	const status = scope.run(() => useChatTurnStatus(session));
	expect(status.value).toBeNull();
	session.items[0].segments.push({ kind: 'content', markdown: 'progress' });
	expect(status.value.label).toBe('执行中');
	session.isBusy = false;
	await nextTick();
	expect(status.value).toBeNull();
	expect(vi.getTimerCount()).toBe(0);
});
