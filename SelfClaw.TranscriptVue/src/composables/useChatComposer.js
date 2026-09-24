import { getSelectedModelProfileId } from './useComposerModelSelection.js';
import { onScopeDispose, readonly, ref } from 'vue';

export function useChatComposer(session, workspace, transcriptScroll, bridge) {
	const submitting = ref(false);
	const error = ref('');
	let disposed = false;

	async function submit(submission) {
		const prompt = submission?.prompt?.trim();
		if (!prompt || disposed || session.isBusy || submitting.value) return;
		submitting.value = true;
		error.value = '';
		try {
			const response = await bridge.request('send-prompt',
				{ modelProfileId: getSelectedModelProfileId(), prompt, workspaceMode: submission.workspaceMode || 'local' }, { timeout: 120000 });
			if (disposed) return;
			if (!response?.accepted) throw new Error(response?.error || '发送请求未被接受。');
			transcriptScroll.resumeFollow();
			submission.accept?.();
			// The backend already upserts the root list during admission; the default refresh probes
			// only the selected conversation's root instead of re-scanning every workspace root.
			await workspace.refresh();
		} catch (failure) {
			if (!disposed) error.value = failure?.message || '发送失败，请重试。';
		} finally {
			submitting.value = false;
		}
	}

	function stop() { if (session.isBusy) bridge.post({ type: 'stop-generation' }); }
	function selectPermissionMode(mode) { if (mode) bridge.post({ type: 'select-tool-permission-mode', mode }); }
	onScopeDispose(() => { disposed = true; });
	return { submitting: readonly(submitting), error: readonly(error), submit, stop, selectPermissionMode };
}
