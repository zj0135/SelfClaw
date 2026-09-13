import { readonly, shallowRef } from 'vue';

export function useChatApprovals(bridge) {
	const pending = shallowRef(null);
	bridge.on('toolApprovalRequest', (payload) => {
		pending.value = {
			toolExecutionId: payload.toolExecutionId, toolName: payload.toolName || '', displayName: payload.displayName || '',
			description: payload.description || '', argumentsJson: payload.argumentsJson || '', sourceKind: payload.sourceKind,
			sourceId: payload.sourceId || '', transportSummary: payload.transportSummary || '', annotationsJson: payload.annotationsJson || '',
		};
	});
	bridge.on('toolApprovalClear', () => { pending.value = null; });

	function resolve(toolExecutionId, approved) {
		if (!toolExecutionId) return;
		if (pending.value?.toolExecutionId === toolExecutionId) pending.value = null;
		bridge.post({ type: 'resolve-tool-approval', toolExecutionId, approved });
	}
	return { pending: readonly(pending), resolve };
}
