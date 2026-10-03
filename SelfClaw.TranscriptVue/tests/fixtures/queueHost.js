export function installQueueHost() {
    const base = window.chrome.webview.postMessage;
    const host = window.activityFixture;
    const parent = host.parent;
    let subscription = null;
    let revision = 1;
    let paused = false;
    let items = JSON.parse(localStorage.getItem('queue-items') || '[]');
    let ack = null;
    let holdAck = false;
    const save = () => localStorage.setItem('queue-items', JSON.stringify(items));
    function state(requestId) {
        host.send({ type: 'conversation-input/state', requestId, subscriptionId: subscription, conversationId: parent,
            queueRevision: revision, paused, pauseReason: paused ? 'queue-paused' : null, canSteer: false, items });
    }
    window.chrome.webview.postMessage = request => {
        if (request.type.startsWith('conversation-input/')) {
            if (request.type === 'conversation-input/subscribe') {
                if (!/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(request.subscriptionId))
                    throw new Error('invalid-subscriptionId');
                subscription = request.subscriptionId;
                state(request.requestId);
            } else if (request.type === 'conversation-input/cancel') {
                items = items.filter(item => item.inputId !== request.inputId); revision++; save(); state();
                host.send({ type: 'conversation-input/result', requestId: request.requestId, operation: 'cancel', ok: true });
            } else if (request.type === 'conversation-input/pause' || request.type === 'conversation-input/resume') {
                paused = request.type.endsWith('/pause'); revision++; state();
                // Real host sends a partial result after the complete push, at the same revision.
                host.send({ type: 'conversation-input/result', requestId: request.requestId, conversationId: parent,
                    queueRevision: revision, paused, operation: paused ? 'pause' : 'resume', ok: true });
            }
            return;
        }
        if (request.type === 'send-prompt') {
            host.requests.push(request);
            if (!items.some(item => item.clientRequestId === request.clientRequestId)) {
                items.push({ inputId: crypto.randomUUID(), clientRequestId: request.clientRequestId, sequence: items.length + 1,
                    kind: 'followup', status: 'pending', revision: 1, preview: request.prompt });
                revision++; save(); state();
            }
            ack = () => host.send({ type: 'prompt-submission', requestId: request.requestId, accepted: true, conversationId: parent });
            if (!holdAck) ack();
            return;
        }
        base(request);
    };
    window.queueFixture = {
        holdAck(value) { holdAck = value; },
        releaseAck() { ack?.(); },
        transcript() {
            host.send({ type: 'replaceState', revision: 1, selectedConversationId: parent, isBusy: true,
                agentMode: 'direct', selectedAgentId: 'build', selectedAgentName: 'Build',
                conversations: [{ id: parent, title: 'Queue verification' }],
                items: [{ id: 'answer', kind: 'message', role: 'assistant', status: 'streaming', isThinking: false,
                    segments: [{ kind: 'content', markdown: 'Original response still running.' }] }] });
        },
    };
}

