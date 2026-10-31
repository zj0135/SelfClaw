import { effectScope, reactive } from 'vue';
import { afterEach, expect, it, vi } from 'vitest';
import { createQueueState, reduceQueueState, useConversationInputs } from '../src/composables/useConversationInputs.js';
import { useChatComposer } from '../src/composables/useChatComposer.js';

let scope;
afterEach(() => scope?.stop());

function createBridge(responder) {
	const subscribers = new Map();
	const bridge = {
		request: vi.fn(responder),
		post: vi.fn(),
		on: vi.fn((type, handler) => {
			subscribers.set(type, handler);
			return () => subscribers.delete(type);
		}),
		push(payload) { subscribers.get('conversation-input/state')?.(payload); },
	};
	return bridge;
}

function defaultResponder(type, payload) {
	if (type === 'conversation-input/detail') {
		return { type: 'conversation-input/result', requestId: payload.requestId, operation: 'detail', ok: true,
			inputId: payload.inputId, revision: 2, prompt: 'full body' };
	}
	return { type: 'conversation-input/state', conversationId: payload.conversationId, subscriptionId: payload.subscriptionId,
		queueRevision: 1, paused: false, pauseReason: null, canSteer: false, items: [
			{ inputId: 'i-1', sequence: 1, kind: 'followup', status: 'pending', revision: 1, preview: 'queued' }] };
}

function statePayload(conversationId, subscriptionId, overrides = {}) {
	return { type: 'conversation-input/state', conversationId, subscriptionId, queueRevision: 1,
		paused: false, pauseReason: null, canSteer: false, items: [], ...overrides };
}

function run(responder = defaultResponder) {
	const bridge = createBridge(responder);
	scope = effectScope();
	const queue = scope.run(() => useConversationInputs(bridge));
	return { bridge, queue };
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
	const { bridge, queue } = run();

	await queue.subscribe('A');
	expect(bridge.request).toHaveBeenCalledWith('conversation-input/subscribe',
		expect.objectContaining({ conversationId: 'A' }));
	expect(queue.state.items).toHaveLength(1);

	bridge.push(statePayload('A', queue.state.subscriptionId, { queueRevision: 3, paused: true,
		pauseReason: 'turn-failed', canSteer: true, steerReason: null, steerTargetTurnId: 'turn-1',
		items: [{ inputId: 'i-2', status: 'held', revision: 4, preview: 'held' }] }));
	expect(queue.state.queueRevision).toBe(3);
	expect(queue.state.paused).toBe(true);
	expect(queue.state.canSteer).toBe(true);
	expect(queue.state.steerTargetTurnId).toBe('turn-1');
	expect(queue.state.items[0].status).toBe('held');

	await queue.cancel('i-2', 4);
	expect(bridge.request).toHaveBeenLastCalledWith('conversation-input/cancel', { inputId: 'i-2', expectedRevision: 4 });

	await queue.pause();
	expect(bridge.request).toHaveBeenLastCalledWith('conversation-input/pause',
		expect.objectContaining({ conversationId: 'A', expectedQueueRevision: 3 }));
});

it('drops state pushed for a conversation that is no longer selected', async () => {
	const { bridge, queue } = run();
	await queue.subscribe('A');
	const revision = queue.state.queueRevision;
	bridge.push(statePayload('B', queue.state.subscriptionId, { queueRevision: revision + 5, items: [] }));
	expect(queue.state.conversationId).toBe('A');
	expect(queue.state.queueRevision).toBe(revision);
});

it('restores state on reload by resubscribing to the selected conversation', async () => {
	const { bridge, queue } = run();
	await queue.subscribe('A');
	const firstRevision = queue.state.queueRevision;
	await queue.subscribe('A');
	expect(bridge.request).toHaveBeenCalledTimes(2);
	expect(queue.state.queueRevision).toBe(firstRevision);
});

it('uses a GUID subscription accepted by the real host', async () => {
	const { queue } = run();
	await queue.subscribe('A');
	expect(queue.state.subscriptionId).toMatch(/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i);
});

it('does not apply a partial pause result as a complete snapshot', () => {
	const state = { ...createQueueState(), conversationId: 'A', subscriptionId: 's1', loaded: true, queueRevision: 3, items: [{ inputId: 'kept' }] };
	const result = reduceQueueState(state, { type: 'conversation-input/result', conversationId: 'A', queueRevision: 3, paused: true, ok: true });
	expect(result.accepted).toBe(false);
	expect(result.state.items).toHaveLength(1);
});

it('keeps the host string status', () => {
	const state = { ...createQueueState(), conversationId: 'A' };
	const result = reduceQueueState(state, { conversationId: 'A', items: [{ status: 'held' }, { status: 'claimed' }] });
	expect(result.state.items.map(item => item.status)).toEqual(['held', 'claimed']);
	expect(createQueueState().error).toBe('');
});

it('keeps only the two remaining inputs after the middle steer leaves the snapshot', async () => {
	const items = [
		{ inputId: 'i-1', sequence: 1, kind: 'follow-up', status: 'pending', revision: 1, preview: 'first' },
		{ inputId: 'i-2', sequence: 2, kind: 'steer', status: 'pending', revision: 2, preview: 'promote me' },
		{ inputId: 'i-3', sequence: 3, kind: 'follow-up', status: 'pending', revision: 1, preview: 'third' },
	];
	const { bridge, queue } = run((type, payload) => ({
		type: 'conversation-input/state', conversationId: payload.conversationId, subscriptionId: payload.subscriptionId,
		queueRevision: 1, canSteer: true, steerTargetTurnId: 'turn-1', items,
	}));
	await queue.subscribe('A');
	expect(queue.state.items.map(item => item.kind)).toEqual(['follow-up', 'steer', 'follow-up']);

	// The boundary consumed only the steer: one row leaves, the two follow-ups keep their order.
	bridge.push(statePayload('A', queue.state.subscriptionId, { queueRevision: 2, canSteer: true,
		steerTargetTurnId: 'turn-1', items: [items[0], items[2]] }));

	expect(queue.state.items.map(item => item.preview)).toEqual(['first', 'third']);
	expect(queue.state.items.map(item => item.kind)).toEqual(['follow-up', 'follow-up']);
	expect(queue.notice.value).toBe('');
});

it('hides a consumed steer at once instead of leaving a confirmation behind', async () => {
	const { bridge, queue } = run((type, payload) => {
		return { type: 'conversation-input/state', conversationId: payload.conversationId, subscriptionId: payload.subscriptionId,
			queueRevision: 1, canSteer: true, steerTargetTurnId: 'turn-1',
			items: [{ inputId: 'steer-1', sequence: 1, kind: 'steer', status: 'pending', revision: 1, preview: 'adjust' }] };
	});
	await queue.subscribe('A');
	expect(queue.state.items).toHaveLength(1);

	// The boundary consumed the steer: it leaves the snapshot and nothing else may outlive it.
	bridge.push(statePayload('A', queue.state.subscriptionId, { queueRevision: 2, canSteer: true, steerTargetTurnId: 'turn-1', items: [] }));

	expect(queue.state.items).toHaveLength(0);
	expect(queue.notice.value).toBe('');
});

it('keeps only the edit confirmation as a notice', async () => {
	const { queue } = run((type, payload) => {
		if (type === 'conversation-input/detail') {
			return { type: 'conversation-input/result', requestId: payload.requestId, operation: 'detail', ok: true,
				inputId: payload.inputId, revision: 2, prompt: 'original body' };
		}
		if (type === 'conversation-input/edit') {
			return { type: 'conversation-input/result', requestId: payload.requestId, operation: 'edit', ok: true, inputId: 'i-1', revision: 3 };
		}
		return defaultResponder(type, payload);
	});
	await queue.subscribe('A');
	await queue.edit('i-1');
	queue.setEditPrompt('edited body');
	await queue.saveEdit();

	expect(queue.notice.value).toBe('已更新待发送内容');
});

it('keeps the edited body on a conflict, refreshes the list and adopts the new revision', async () => {
	const { bridge, queue } = run((type, payload) => {
		if (type === 'conversation-input/detail') {
			return { type: 'conversation-input/result', requestId: payload.requestId, operation: 'detail', ok: true,
				inputId: payload.inputId, revision: 2, prompt: 'original body' };
		}
		if (type === 'conversation-input/edit') {
			return { type: 'conversation-input/result', requestId: payload.requestId, operation: 'edit', ok: false,
				status: 'conflict', conflict: true, reason: 'queue-revision-conflict' };
		}
		if (type === 'conversation-input/get-state') {
			return { type: 'conversation-input/state', conversationId: 'A', subscriptionId: payload.subscriptionId,
				queueRevision: 9, items: [{ inputId: 'i-1', sequence: 1, kind: 'followup', status: 'pending', revision: 5, preview: 'other' }] };
		}
		return defaultResponder(type, payload);
	});
	await queue.subscribe('A');
	await queue.edit('i-1');
	expect(queue.editing.value.prompt).toBe('original body');

	queue.setEditPrompt('my preserved draft');
	await queue.saveEdit();

	expect(queue.editing.value.prompt).toBe('my preserved draft');
	expect(queue.editing.value.revision).toBe(5);
	expect(queue.state.queueRevision).toBe(9);
	expect(queue.state.error).toBeTruthy();
	expect(bridge.request).toHaveBeenCalledWith('conversation-input/get-state', expect.anything());
});

it('promotes with the host-published target and refuses locally when the host says steer is unavailable', async () => {
	const { bridge, queue } = run((type, payload) => {
		if (type === 'conversation-input/promote') {
			return { type: 'conversation-input/result', requestId: payload.requestId, operation: 'promote', ok: true };
		}
		return { type: 'conversation-input/state', conversationId: payload.conversationId, subscriptionId: payload.subscriptionId,
			queueRevision: 1, canSteer: true, steerReason: null, steerTargetTurnId: 'turn-9',
			items: [{ inputId: 'i-1', sequence: 1, kind: 'followup', status: 'pending', revision: 3, preview: 'queued' }] };
	});
	await queue.subscribe('A');

	await queue.promote('i-1', 3);
	expect(bridge.request).toHaveBeenLastCalledWith('conversation-input/promote',
		{ inputId: 'i-1', expectedRevision: 3, targetTurnId: 'turn-9' });

	bridge.push(statePayload('A', queue.state.subscriptionId, { queueRevision: 2, canSteer: false,
		steerReason: 'skill-requires-new-turn', items: [{ inputId: 'i-1', sequence: 1, kind: 'followup', status: 'pending', revision: 3, preview: 'queued' }] }));
	await queue.promote('i-1', 3);
	expect(bridge.request).toHaveBeenCalledTimes(2);
	expect(queue.state.error).toContain('新回合');
});

it('ignores an old conversation result after switching subscriptions', async () => {
	const pending = [];
	const { bridge, queue } = run((type, payload) => {
		if (type === 'conversation-input/cancel') {
			return new Promise((resolve) => pending.push({ resolve, requestId: payload.requestId }));
		}
		return { type: 'conversation-input/state', conversationId: payload.conversationId, subscriptionId: payload.subscriptionId,
			queueRevision: 1, items: [] };
	});
	await queue.subscribe('A');
	const cancelling = queue.cancel('i-1', 1);
	await queue.subscribe('B');

	pending[0].resolve({ type: 'conversation-input/result', requestId: pending[0].requestId, ok: false, status: 'conflict', reason: 'queue-revision-conflict' });
	await cancelling;

	expect(queue.state.conversationId).toBe('B');
	expect(queue.state.error).toBe('');
	expect(bridge.request.mock.calls.filter(call => call[0] === 'conversation-input/get-state')).toHaveLength(0);
});

it('never forces the transcript reading position when queue state changes around a queued ACK', async () => {
	const subscribers = new Map();
	const bridge = {
		request: vi.fn(async (type, payload) => {
			if (type === 'send-prompt') return { accepted: true };
			if (type === 'conversation-input/subscribe') return statePayload(payload.conversationId, payload.subscriptionId);
			return { ok: true };
		}),
		post: vi.fn(),
		on: vi.fn((type, handler) => { subscribers.set(type, handler); return () => subscribers.delete(type); }),
		push(payload) { subscribers.get('conversation-input/state')?.(payload); },
	};
	scope = effectScope();
	const session = reactive({ isBusy: true, agentMode: 'direct', selectedConversationId: 'A' });
	const scrolling = { resumeFollow: vi.fn() };
	const workspace = { refresh: vi.fn(async () => {}) };
	const composed = scope.run(() => ({
		queue: useConversationInputs(bridge),
		composer: useChatComposer(session, workspace, scrolling, bridge),
	}));
	await composed.queue.subscribe('A');

	const accept = vi.fn();
	const pending = composed.composer.submit({ prompt: 'queued while busy', accept });
	bridge.push(statePayload('A', composed.queue.state.subscriptionId, { queueRevision: 2, canSteer: false,
		items: [{ inputId: 'i-1', sequence: 1, kind: 'followup', status: 'pending', revision: 1, preview: 'queued while busy' }] }));
	await pending;

	expect(accept).toHaveBeenCalledOnce();
	expect(scrolling.resumeFollow).not.toHaveBeenCalled();
	expect(composed.queue.state.items).toHaveLength(1);
});
