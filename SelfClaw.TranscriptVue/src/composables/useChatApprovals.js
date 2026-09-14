import { onMounted, readonly, shallowRef } from 'vue';
import { useToast } from './useToast.js';

export function useChatApprovals(bridge) {
	const pending = shallowRef(null);
	let revision = 0;
	let resolving = false;
	function apply(payload) {
		if (payload.revision < revision) return;
		revision = payload.revision;
		pending.value = payload.approval ? { ...payload.approval, conversationTitle: payload.conversationTitle } : null;
	}
	bridge.on('tool-approval/state', apply);
	async function refresh() {
		try { apply(await bridge.request('tool-approval/get-state')); }
		catch (error) { useToast().showToast(error.message); }
	}
	onMounted(refresh);

	async function resolve(toolExecutionId, approved) {
		if (!toolExecutionId || resolving) return;
		resolving = true;
		try { await bridge.request('resolve-tool-approval', { toolExecutionId, approved }); }
		catch (error) { useToast().showToast(error.message); }
		finally { resolving = false; await refresh(); }
	}
	return { pending: readonly(pending), resolve };
}
