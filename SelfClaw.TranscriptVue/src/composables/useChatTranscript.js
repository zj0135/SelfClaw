import { computed, nextTick, reactive, readonly } from 'vue';
import { useTranscriptBridge } from './transcriptBridge.js';

export function useChatTranscript(scroll) {
	const state = reactive({
		items: [], selectedConversationId: null, isBusy: false,
		agentMode: 'cli', selectedAgentId: '', selectedAgentName: '', capabilityRevision: 0,
		activityText: '', toolPermissionMode: 'require-approval',
	});
	const isEmptyConversation = computed(() => state.items.length === 0 && !state.isBusy);

	function replaceState(payload) {
		const conversationId = payload.selectedConversationId || null;
		const autoScroll = Boolean(payload.autoScroll);
		const before = scroll.captureBeforeUpdate(conversationId);
		state.items = Array.isArray(payload.items) ? payload.items : [];
		state.selectedConversationId = conversationId;
		state.isBusy = Boolean(payload.isBusy);
		state.activityText = payload.activityText || '';
		state.agentMode = payload.agentMode || 'cli';
		state.selectedAgentId = payload.selectedAgentId || '';
		state.selectedAgentName = payload.selectedAgentName || '';
		state.capabilityRevision = Number(payload.capabilityRevision) || 0;
		state.toolPermissionMode = payload.toolPermissionMode || 'require-approval';
		nextTick(() => scroll.settleAfterUpdate(autoScroll, before));
	}

	useTranscriptBridge().on(replaceState);
	return { state: readonly(state), isEmptyConversation };
}
