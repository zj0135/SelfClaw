import { computed, nextTick, reactive, readonly, ref } from 'vue';
import { useTranscriptBridge } from './transcriptBridge.js';

// 全局/模块级切换标记：当侧栏导航触发切换会话时，
// 立即标记目标 conversationId，避免等待宿主异步查询期间回退到新对话空态造成输入框跳动。
const pendingConversationId = ref(undefined);

export function setPendingConversation(id) {
	pendingConversationId.value = id ?? null;
}

export function useChatTranscript(scroll) {
	const state = reactive({
		items: [], selectedConversationId: null, isBusy: false, isContinuation: false,
		agentMode: 'cli', selectedAgentId: '', selectedAgentName: '', capabilityRevision: 0,
		activityText: '', toolPermissionMode: 'require-approval', usage: null, todo: null,
	});

	// 历史会话加载中判定：
	// 1. 明确被侧栏标记正在切换到一个具体的历史会话（pendingConversationId 有有效 id）；
	// 2. 或者宿主推送的 selectedConversationId 已经是一个具体历史会话（非 null），但消息列表 items 仍为空且非 busy。
	const isLoadingHistory = computed(() => {
		if (pendingConversationId.value !== undefined && pendingConversationId.value !== null) {
			return true;
		}
		return state.selectedConversationId !== null && state.items.length === 0 && !state.isBusy;
	});

	// 只有真正的新对话（没有会话 ID，且无消息、未忙碌、非历史切换中）才算空对话，输入框居中。
	const isEmptyConversation = computed(() => {
		if (isLoadingHistory.value) {
			return false;
		}
		if (pendingConversationId.value !== undefined) {
			return pendingConversationId.value === null && state.items.length === 0 && !state.isBusy;
		}
		return state.selectedConversationId === null && state.items.length === 0 && !state.isBusy;
	});

	function replaceState(payload) {
		const conversationId = payload.selectedConversationId || null;
		const autoScroll = Boolean(payload.autoScroll);
		const before = scroll.captureBeforeUpdate(conversationId);
		state.items = Array.isArray(payload.items) ? payload.items : [];
		state.selectedConversationId = conversationId;
		state.isBusy = Boolean(payload.isBusy);
		state.isContinuation = Boolean(payload.isContinuation);
		state.activityText = payload.activityText || '';
		state.agentMode = payload.agentMode || 'cli';
		state.selectedAgentId = payload.selectedAgentId || '';
		state.selectedAgentName = payload.selectedAgentName || '';
		state.capabilityRevision = Number(payload.capabilityRevision) || 0;
		state.toolPermissionMode = payload.toolPermissionMode || 'require-approval';
		state.usage = payload.usage || null;
		// 计划随会话切换整体替换：后端在无计划时省略字段，这里必须清空，否则会残留上一条会话的计划。
		state.todo = payload.todo || null;

		// 收到匹配的目标会话且已到达，解除 pending
		if (pendingConversationId.value !== undefined) {
			if (pendingConversationId.value === conversationId) {
				pendingConversationId.value = undefined;
			}
		}

		nextTick(() => scroll.settleAfterUpdate(autoScroll, before));
	}

	useTranscriptBridge().on(replaceState);
	return { state: readonly(state), isEmptyConversation, isLoadingHistory };
}
