import { getSelectedModelProfileId } from './useComposerModelSelection.js';
import { onScopeDispose, readonly, ref, watch } from 'vue';
import { describeInputReason } from '../renderers/conversationInputReasons.js';

const NOTICE_TIMEOUT_MS = 5000;

export function useChatComposer(session, workspace, transcriptScroll, bridge) {
    const submitting = ref(false);
    const error = ref('');
    const notice = ref('');
    let noticeTimer = null;
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
        notice.value = '';
    }, { flush: 'sync' });

    function showNotice(text) {
        if (!text) return;
        notice.value = text;
        if (noticeTimer) clearTimeout(noticeTimer);
        noticeTimer = setTimeout(() => { notice.value = ''; noticeTimer = null; }, NOTICE_TIMEOUT_MS);
    }

    async function submit(submission) {
        const prompt = submission?.prompt?.trim();
        const queueable = session.agentMode === 'direct';
        const steering = submission?.mode === 'steer' && queueable;
        if (!prompt || disposed || submitting.value || (session.isBusy && !queueable)) return;
        if (steering && !submission.targetTurnId) {
            error.value = '当前回合不能调整，请稍后重试。';
            return;
        }

        const target = session.selectedConversationId || null;
        const selection = generation;
        const queued = Boolean(session.isBusy && queueable);
        const values = { prompt, modelProfileId: getSelectedModelProfileId(), workspaceMode: submission.workspaceMode || 'local' };
        const route = steering ? { kind: 'steer', targetTurnId: submission.targetTurnId } : {};
        // 重试键必须包含投递模式：调整失败的草稿不能复用成一次普通入队。
        const key = JSON.stringify([target, session.agentMode, values, route]);
        const payload = retry?.key === key ? retry.payload : {
            ...values, clientRequestId: crypto.randomUUID(), conversationId: target || crypto.randomUUID(),
            newConversation: !target, ...route,
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
            // 模式退回（如 Skill／hook 要求新回合）由宿主给出原因，界面只负责展示。
            if (response.reason) showNotice(describeInputReason(response.reason, ''));
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
    onScopeDispose(() => {
        disposed = true;
        if (noticeTimer) clearTimeout(noticeTimer);
    });
    return { submitting: readonly(submitting), error: readonly(error), notice: readonly(notice), submit, stop, selectPermissionMode };
}
