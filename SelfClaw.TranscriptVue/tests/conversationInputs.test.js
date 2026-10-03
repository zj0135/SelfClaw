import { effectScope } from 'vue';
import { afterEach, expect, it, vi } from 'vitest';
import { createQueueState, reduceQueueState, useConversationInputs } from '../src/composables/useConversationInputs.js';

let scope;
afterEach(() => scope?.stop());

function createBridge() {
	const subscribers = new Map();
	const responses = [];
	return {
		request: vi.fn(async (type, payload) => {
			responses.push({ type, payload });
			return { type: 'conversation-input/state', requestId: payload.requestId, conversationId: payload.conversationId,
				subscriptionId: payload.subscriptionId, queueRevision: 1, paused: false, pauseReason: null, canSteer: false,
				items: [{ inputId: 'i-1', sequence: 1, kind: 'follow-up', status: 'pending', revision: 1, preview: 'queued' }] };
		}),
		post: vi.fn(),
		on: vi.fn((type, handler) => {
			subscribers.set(type, handler);
			return () => subscribers.delete(type);
		}),
		push(payload) { subscribers.get('conversation-input/state')?.(payload); },
		requests: responses,
	};
}

it('rejects a push from another conversation or an older revision', () => {
	const state = { ...createQueueState(), conversationId: 'A', subscriptionId: 's1', loaded: true, queueRevision: 5 };
	expect(reduceQueueState(state, { conversationId: 'B', subscriptionId: 's1', queueRevision: 6 }).accepted).toBe(false);
	expect(reduceQueueState(state, { conversationId: 'A', subscriptionId: 's2', queueRevision: 6 }).accepted).toBe(false);
	expect(reduceQueueState(state, { conversationId: 'A', subscriptionId: 's1', queueRevision: 4 }).accepted).toBe(false);
	const accepted = reduceQueueState(state, { conversationId: 'A', subscriptionId: 's1', queueRevision: 6, items: [] });
	expect(accepted.accepted).toBe(true);
	expect(accepted.state.queueRevision).toBe(6);
});

it('subscribes, applies pushed state and sends revision-bound operations', async () => {
	const bridge = createBridge();
	scope = effectScope();
	const queue = scope.run(() => useConversationInputs(bridge));

	await queue.subscribe('A');
	expect(bridge.request).toHaveBeenCalledWith('conversation-input/subscribe',
		expect.objectContaining({ conversationId: 'A' }));
	expect(queue.state.items).toHaveLength(1);

	bridge.push({ conversationId: 'A', subscriptionId: queue.state.subscriptionId, queueRevision: 3, paused: true,
		pauseReason: 'turn-failed', items: [{ inputId: 'i-2', status: 'held', revision: 4, preview: 'held' }] });
	expect(queue.state.queueRevision).toBe(3);
	expect(queue.state.paused).toBe(true);
	expect(queue.state.items[0].status).toBe('held');

	await queue.cancel('i-2', 4);
	expect(bridge.request).toHaveBeenLastCalledWith('conversation-input/cancel', { inputId: 'i-2', expectedRevision: 4 });

	await queue.pause();
	expect(bridge.request).toHaveBeenLastCalledWith('conversation-input/pause',
		expect.objectContaining({ conversationId: 'A' }));
});

it('drops state pushed for a conversation that is no longer selected', async () => {
	const bridge = createBridge();
	scope = effectScope();
	const queue = scope.run(() => useConversationInputs(bridge));
	await queue.subscribe('A');
	const revision = queue.state.queueRevision;
	bridge.push({ conversationId: 'B', subscriptionId: queue.state.subscriptionId, queueRevision: revision + 5, items: [] });
	expect(queue.state.conversationId).toBe('A');
	expect(queue.state.queueRevision).toBe(revision);
});

it('restores state on reload by resubscribing to the selected conversation', async () => {
	const bridge = createBridge();
	scope = effectScope();
	const queue = scope.run(() => useConversationInputs(bridge));
	await queue.subscribe('A');
	const firstRevision = queue.state.queueRevision;
	await queue.subscribe('A');
	expect(bridge.request).toHaveBeenCalledTimes(2);
	expect(queue.state.queueRevision).toBe(firstRevision);
});

it('uses a GUID subscription accepted by the real host', async () => {
    const bridge = createBridge();
    scope = effectScope();
    const queue = scope.run(() => useConversationInputs(bridge));
    await queue.subscribe('A');
    expect(queue.state.subscriptionId).toMatch(/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i);
});

it('does not apply a partial pause result as a complete snapshot', () => {
    const state = { ...createQueueState(), conversationId: 'A', subscriptionId: 's1', loaded: true, queueRevision: 3, items: [{ inputId: 'kept' }] };
    const result = reduceQueueState(state, { type: 'conversation-input/result', conversationId: 'A', queueRevision: 3, paused: true, ok: true });
    expect(result.accepted).toBe(false);
    expect(result.state.items).toHaveLength(1);
});

it('normalizes a numeric historical status while preserving string wire status', () => {
    const state = { ...createQueueState(), conversationId: 'A' };
    const result = reduceQueueState(state, { conversationId: 'A', items: [{ status: 3 }, { status: 'claimed' }] });
    expect(result.state.items.map(item => item.status)).toEqual(['held', 'claimed']);
});
