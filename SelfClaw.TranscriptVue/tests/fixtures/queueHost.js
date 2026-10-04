export function installQueueHost() {
    const base = window.chrome.webview.postMessage;
    const host = window.activityFixture;
    const parent = host.parent;
    let subscription = null;
    let scope = parent;
    let revision = 1;
    let paused = false;
    let canSteer = false;
    let steerReason = null;
    let steerTargetTurnId = null;
    let steerFallbackReason = null;
    let conflictNextEdit = false;
    let ack = null;
    let holdAck = false;
    const conversationIds = [parent];
    const stored = (() => { try { return JSON.parse(localStorage.getItem('queue-items') || '{}') || {}; } catch { return {}; } })();
    const itemsFor = (id) => stored[id] || (stored[id] = []);
    const save = () => localStorage.setItem('queue-items', JSON.stringify(stored));
    const send = (payload) => host.send(payload);

    function state(requestId) {
        send({ type: 'conversation-input/state', requestId, subscriptionId: subscription, conversationId: scope,
            queueRevision: revision, paused, pauseReason: paused ? 'queue-paused' : null,
            canSteer, steerReason, steerTargetTurnId, items: itemsFor(scope) });
    }

    function result(request, operation, ok, extra = {}) {
        send({ type: 'conversation-input/result', requestId: request.requestId, operation, ok, ...extra });
    }

    function mutate() {
        revision += 1;
        save();
        state();
    }

    window.chrome.webview.postMessage = request => {
        if (request.type.startsWith('conversation-input/')) {
            const list = itemsFor(scope);
            if (request.type === 'conversation-input/subscribe') {
                if (!/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(request.subscriptionId))
                    throw new Error('invalid-subscriptionId');
                subscription = request.subscriptionId;
                scope = request.conversationId;
                if (!conversationIds.includes(scope)) conversationIds.push(scope);
                state(request.requestId);
            } else if (request.type === 'conversation-input/get-state') {
                state(request.requestId);
            } else if (request.type === 'conversation-input/detail') {
                const item = list.find(entry => entry.inputId === request.inputId);
                if (!item) result(request, 'detail', false, { status: 'missing' });
                else send({ type: 'conversation-input/result', requestId: request.requestId, operation: 'detail',
                    ok: true, inputId: item.inputId, revision: item.revision, prompt: item.prompt || item.preview });
            } else if (request.type === 'conversation-input/edit') {
                const item = list.find(entry => entry.inputId === request.inputId);
                if (!item) result(request, 'edit', false, { status: 'missing' });
                else if (conflictNextEdit || item.revision !== request.expectedRevision) {
                    conflictNextEdit = false;
                    item.revision += 1;
                    mutate();
                    result(request, 'edit', false, { status: 'conflict', conflict: true, reason: 'queue-revision-conflict' });
                } else {
                    item.prompt = request.prompt;
                    item.preview = request.prompt;
                    item.revision += 1;
                    mutate();
                    result(request, 'edit', true, { inputId: item.inputId, revision: item.revision });
                }
            } else if (request.type === 'conversation-input/promote') {
                const item = list.find(entry => entry.inputId === request.inputId);
                if (!item || item.status !== 'pending' || item.revision !== request.expectedRevision) {
                    result(request, 'promote', false, { status: 'conflict', conflict: true, reason: 'queue-revision-conflict' });
                } else if (request.targetTurnId !== steerTargetTurnId || !canSteer) {
                    result(request, 'promote', false, { status: 'invalid', reason: steerReason || 'target-turn-closed' });
                } else {
                    item.kind = 'steer';
                    item.revision += 1;
                    mutate();
                    result(request, 'promote', true, { inputId: item.inputId, revision: item.revision });
                }
            } else if (request.type === 'conversation-input/cancel') {
                const index = list.findIndex(entry => entry.inputId === request.inputId && entry.revision === request.expectedRevision);
                if (index < 0) result(request, 'cancel', false, { status: 'conflict', conflict: true, reason: 'queue-revision-conflict' });
                else { list.splice(index, 1); mutate(); result(request, 'cancel', true); }
            } else if (request.type === 'conversation-input/retry') {
                const item = list.find(entry => entry.inputId === request.inputId && entry.revision === request.expectedRevision);
                if (!item) result(request, 'retry', false, { status: 'conflict', conflict: true, reason: 'queue-revision-conflict' });
                else { item.status = 'pending'; item.reasonCode = null; item.revision += 1; mutate(); result(request, 'retry', true); }
            } else if (request.type === 'conversation-input/pause' || request.type === 'conversation-input/resume') {
                paused = request.type.endsWith('/pause');
                revision += 1;
                state();
                // Real host sends a partial result after the complete push, at the same revision.
                result(request, paused ? 'pause' : 'resume', true, { conversationId: scope, queueRevision: revision, paused });
            }
            return;
        }
        if (request.type === 'send-prompt') {
            host.requests.push(request);
            const target = request.conversationId || parent;
            const list = itemsFor(target);
            const steer = request.kind === 'steer';
            const fallback = steer && steerFallbackReason;
            if (!list.some(item => item.clientRequestId === request.clientRequestId)) {
                list.push({ inputId: crypto.randomUUID(), clientRequestId: request.clientRequestId, sequence: list.length + 1,
                    kind: steer && !fallback ? 'steer' : 'followup', status: 'pending', revision: 1, preview: request.prompt });
                revision += 1;
                save();
                if (target === scope) state();
            }
            steerFallbackReason = null;
            ack = () => send({ type: 'prompt-submission', requestId: request.requestId, accepted: true,
                conversationId: target, reason: fallback || undefined });
            if (!holdAck) ack();
            return;
        }
        base(request);
    };

    window.queueFixture = {
        holdAck(value) { holdAck = value; },
        releaseAck() { ack?.(); },
        setSteerable(targetTurnId = 'turn-1') {
            canSteer = true;
            steerReason = null;
            steerTargetTurnId = targetTurnId;
            state();
        },
        blockSteer(reason = 'hook-requires-new-turn') {
            canSteer = false;
            steerReason = reason;
            steerTargetTurnId = null;
            state();
        },
        steerFallback(reason = 'hook-requires-new-turn') { steerFallbackReason = reason; },
        conflictNextEdit() { conflictNextEdit = true; },
        consumeSteer(inputId) {
            const list = itemsFor(scope);
            const index = list.findIndex(entry => entry.inputId === inputId);
            if (index >= 0) { list.splice(index, 1); mutate(); }
        },
        transcript({ busy = true, agentMode = 'direct', conversationId = parent, message = 'Original response still running.' } = {}) {
            scope = conversationId;
            if (!conversationIds.includes(conversationId)) conversationIds.push(conversationId);
            send({ type: 'replaceState', revision: 1, selectedConversationId: conversationId, isBusy: busy, agentMode,
                selectedAgentId: 'build', selectedAgentName: 'Build',
                conversations: conversationIds.map((id, index) => ({ id, title: index === 0 ? 'Queue verification' : `Conversation ${index}` })),
                items: [{ id: 'answer', kind: 'message', role: 'assistant', status: 'streaming', isThinking: false,
                    segments: [{ kind: 'content', markdown: message }] }] });
        },
    };
}
