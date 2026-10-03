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

it('shows the active run before any assistant output and hides when the turn completes', async () => {
	vi.useFakeTimers();
	const session = reactive({ isBusy: true, selectedConversationId: 'first', items: [] });
	scope = effectScope();
	const status = scope.run(() => useChatTurnStatus(session));
	expect(status.value.label).toBe('执行中');
	session.items.push({ kind: 'turn-outcome', role: 'system', segments: [], turnOutcome: { status: 'running' } });
	expect(status.value.label).toBe('执行中');
	session.isBusy = false;
	await nextTick();
	expect(status.value).toBeNull();
	expect(vi.getTimerCount()).toBe(0);
});

it('labels a background continuation so the silent window is not mistaken for an idle conversation', async () => {
	vi.useFakeTimers();
	const session = reactive({ isBusy: true, isContinuation: true, selectedConversationId: 'first', items: [] });
	scope = effectScope();
	const status = scope.run(() => useChatTurnStatus(session));
	vi.advanceTimersByTime(3000);
	expect(status.value.label).toBe('正在处理子代理结果');
	expect(status.value.elapsedText).toBe('3s');
	session.isContinuation = false;
	await nextTick();
	expect(status.value.label).toBe('执行中');
});
