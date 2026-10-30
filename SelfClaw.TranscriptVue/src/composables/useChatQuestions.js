import { onMounted, readonly, ref, shallowRef } from 'vue';
import { useToast } from './useToast.js';

// 「Direct ask_user 提问」的宿主状态镜像（对齐 useChatApprovals）。
//
// push 与 get-state 回包同形：{ revision, conversationTitle, question | null }。
// revision 单调递增，旧包一律丢弃——乱序到达的旧快照会把已经答完的批次又弹回界面。
// question === null 表示当前没有待答批次（已作答 / 已跳过 / 别处结清 / 回合取消）。
export function useChatQuestions(bridge) {
	const pending = shallowRef(null);
	const resolving = ref(false);
	let revision = -1;
	let submitting = false;

	function apply(payload) {
		const next = Number(payload?.revision);
		if (Number.isFinite(next)) {
			if (next < revision) {
				return;
			}

			revision = next;
		}

		pending.value = payload?.question
			? { ...payload.question, conversationTitle: payload.conversationTitle || '' }
			: null;
	}

	async function refresh() {
		try { apply(await bridge.request('user-question/get-state')); }
		catch (error) { useToast().showToast(error.message); }
	}

	bridge.on('user-question/state', apply);
	onMounted(refresh);

	// 宿主把问题批次的 id 读成消息体的 `questionRequestId`（WebViewMessageRouter.ResolveUserQuestion），
	// 与 `requestId` 分开：后者是 hostBridge 生成的传输关联 id，宿主原样回显成 operation-result，
	// 两者混用会让状态推送和回包抢同一个槽位。
	async function resolve(requestId, decision) {
		if (!requestId || submitting) {
			return;
		}

		submitting = true;
		resolving.value = true;
		try {
			await bridge.request('resolve-user-question', { questionRequestId: requestId, ...decision });
		}
		catch (error) { useToast().showToast(error.message); }
		finally {
			submitting = false;
			resolving.value = false;
			await refresh();
		}
	}

	return {
		pending: readonly(pending),
		resolving: readonly(resolving),
		answer: (requestId, answers) => resolve(requestId, { answers }),
		skip: (requestId) => resolve(requestId, { skipped: true }),
	};
}
