import { getSelectedModelProfileId } from './useComposerModelSelection.js';
import { onScopeDispose, readonly, ref, watch } from 'vue';

export function useChatComposer(session, workspace, transcriptScroll, bridge) {
    const submitting = ref(false);
    const error = ref('');
    let disposed = false;
    let generation = 0;
    let active = null;
    let retry = null;

    watch(() => session.selectedConversationId, (id, previous) => {
        // Admission of this new conversation is allowed to publish its selection before the ACK.
        if (!previous && active?.payload.newConversation && id === active.payload.conversationId) return;
        generation++;
        submitting.value = false;
        error.value = '';
    }, { flush: 'sync' });

    async function submit(submission) {
        const prompt = submission?.prompt?.trim();
        const queueable = session.agentMode === 'direct';
        if (!prompt || disposed || submitting.value || (session.isBusy && !queueable)) return;
        const target = session.selectedConversationId || null;
        const selection = generation;
        const queued = Boolean(session.isBusy && queueable);
        const values = { prompt, modelProfileId: getSelectedModelProfileId(), workspaceMode: submission.workspaceMode || 'local' };
        const key = JSON.stringify([target, session.agentMode, values]);
        const payload = retry?.key === key ? retry.payload : {
            ...values, clientRequestId: crypto.randomUUID(), conversationId: target || crypto.randomUUID(), newConversation: !target,
        };
        const operation = { key, payload };
        retry = operation;
        active = operation;
        submitting.value = true;
        error.value = '';
        try {
            const response = await bridge.request('send-prompt', payload, { timeout: 120000 });
            if (disposed || selection !== generation || active !== operation) return;
            if (!response?.accepted) throw new Error(response?.error || '发送请求未被接受。');
            retry = null;
            if (!queued) transcriptScroll.resumeFollow();
            submission.accept?.();
            await workspace.refresh();
        } catch (failure) {
            if (!disposed && selection === generation && active === operation) error.value = failure?.message || '发送失败，请重试。';
        } finally {
            if (active === operation && selection === generation) submitting.value = false;
        }
    }

    function stop() { if (session.isBusy) bridge.post({ type: 'stop-generation' }); }
    function selectPermissionMode(mode) { if (mode) bridge.post({ type: 'select-tool-permission-mode', mode }); }
    onScopeDispose(() => { disposed = true; });
    return { submitting: readonly(submitting), error: readonly(error), submit, stop, selectPermissionMode };
}
