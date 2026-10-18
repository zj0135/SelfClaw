import { getSelectedModelProfileId } from './useComposerModelSelection.js';
import { describeInputReason } from '../renderers/conversationInputReasons.js';
import { onScopeDispose, readonly, ref, watch } from 'vue';

export function useChatComposer(session, workspace, transcriptScroll, bridge, attachmentState = null) {
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

    // 附件只传引用（id + 元数据）：存储位置由宿主按 id 还原，前端永远不命名路径。
    function attachmentPayload(submission) {
        const source = Array.isArray(submission?.attachments) ? submission.attachments : attachmentState?.items?.value || [];
        return source
            .filter((item) => item?.attachmentId)
            .map(({ attachmentId, fileName, mediaType, byteLength }) => ({ attachmentId, fileName, mediaType, byteLength }));
    }

    async function submit(submission) {
        const prompt = submission?.prompt?.trim();
        const queueable = session.agentMode === 'direct';
        // 首版图片必须伴随文字：宿主 ValidateAccept 仍拒绝空 prompt。
        if (!prompt || disposed || submitting.value || (session.isBusy && !queueable)) return;

        const target = session.selectedConversationId || null;
        const selection = generation;
        const queued = Boolean(session.isBusy && queueable);
        const attachments = attachmentPayload(submission);
        const values = { prompt, modelProfileId: getSelectedModelProfileId(), workspaceMode: submission.workspaceMode || 'local' };
        // 附件 id 必须进 retry key：否则换图后重试会复用上一份 payload，把旧图再发一次。
        const key = JSON.stringify([target, session.agentMode, values, attachments.map((item) => item.attachmentId)]);
        const payload = retry?.key === key ? retry.payload : {
            ...values, clientRequestId: crypto.randomUUID(),
            // 新对话的 uuid 由 useComposerAttachments 提前生成：附件目录与最终会话必须是同一个。
            conversationId: target || attachmentState?.resolveConversationId?.() || crypto.randomUUID(),
            newConversation: !target,
            attachments,
        };
        const operation = { key, payload };
        retry = operation;
        active = operation;
        submitting.value = true;
        error.value = '';
        try {
            const response = await bridge.request('send-prompt', payload, { timeout: 120000 });
            if (disposed || selection !== generation || active !== operation) return;
            if (!response?.accepted) throw new Error(response?.error || describeInputReason(response?.reason, '发送请求未被接受。'));
            retry = null;
            if (!queued) transcriptScroll.resumeFollow();
            submission.accept?.();
            // 宿主已通过队列持有引用，这里只清空 chip，不能再 discard。
            attachmentState?.clear?.();
            await workspace.refresh();
        } catch (failure) {
            // 发送失败时附件留在输入框里供重试，不在这里清理。
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
