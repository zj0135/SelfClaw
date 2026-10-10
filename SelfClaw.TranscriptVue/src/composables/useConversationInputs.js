import { onScopeDispose, reactive, readonly, ref } from 'vue';
import { describeInputReason } from '../renderers/conversationInputReasons.js';

// 队列状态是宿主推送的有界快照：订阅时立即拉取一份，之后由 `conversation-input/state`
// 覆盖。旧订阅、旧 queueRevision 或其他会话的推送一律丢弃，避免切换会话后旧响应回滚新选择。
//
// 该 composable 只持有展示状态与关联请求；调度、暂停、revision 递增和 canSteer 判定
// 全部由宿主完成，界面不自行推断权限。

const NOTICE_TIMEOUT_MS = 5000;

function normalizeItem(item) {
	return {
		inputId: String(item.inputId || ''),
		sequence: Number(item.sequence) || 0,
		kind: item.kind === 'steer' ? 'steer' : 'follow-up',
		status: item.status || 'pending',
		revision: Number(item.revision) || 0,
		preview: item.preview || '',
		reasonCode: item.reasonCode || null,
		errorMessage: item.errorMessage || null,
	};
}

export function createQueueState() {
	return {
		conversationId: null, subscriptionId: null, queueRevision: 0, paused: false,
		pauseReason: null, canSteer: false, steerReason: null, steerTargetTurnId: null,
		items: [], cancelledInputIds: [], loaded: false, truncated: false, error: '',
	};
}

export function reduceQueueState(state, payload) {
	if (payload?.type && payload.type !== 'conversation-input/state') return { state, accepted: false };
	if (!payload || !state.conversationId || payload.conversationId !== state.conversationId) {
		return { state, accepted: false };
	}
	if (state.subscriptionId && payload.subscriptionId && payload.subscriptionId !== state.subscriptionId) {
		return { state, accepted: false };
	}
	const revision = Number(payload.queueRevision) || 0;
	if (state.loaded && revision < state.queueRevision) {
		return { state, accepted: false };
	}
	return {
		state: {
			...state,
			subscriptionId: payload.subscriptionId || state.subscriptionId,
			queueRevision: revision,
			paused: Boolean(payload.paused),
			pauseReason: payload.pauseReason || null,
			canSteer: Boolean(payload.canSteer),
			steerReason: payload.steerReason || null,
			steerTargetTurnId: payload.steerTargetTurnId || null,
			items: Array.isArray(payload.items) ? payload.items.map(normalizeItem) : [],
			cancelledInputIds: Array.isArray(payload.cancelledInputIds) ? payload.cancelledInputIds.map(String) : [],
			truncated: Boolean(payload.truncated),
			loaded: true,
		},
		accepted: true,
	};
}

export function useConversationInputs(bridge) {
	const state = reactive(createQueueState());
	const editing = ref(null);
	const notice = ref('');
	let generation = 0;
	let disposed = false;
	let noticeTimer = null;
	// 已由本地成功取消的项不再触发“已加入上下文”；取消失败时必须撤销，否则会永久吞掉真正的消费提示。
	const locallyCancelled = new Set();

	function showNotice(text) {
		notice.value = text;
		if (noticeTimer) clearTimeout(noticeTimer);
		noticeTimer = setTimeout(() => { notice.value = ''; noticeTimer = null; }, NOTICE_TIMEOUT_MS);
	}

	function clearNotice() {
		if (noticeTimer) clearTimeout(noticeTimer);
		noticeTimer = null;
		notice.value = '';
	}

	function announceConsumedSteers(previous, next, cancelledInputIds) {
		const remaining = new Set(next.map(item => item.inputId));
		const cancelled = new Set(cancelledInputIds || []);
		for (const item of previous) {
			if (item.kind !== 'steer' || remaining.has(item.inputId)) continue;
			if (locallyCancelled.delete(item.inputId)) continue;
			// 用户停止会丢弃整个队列，此时消失的 steer 是被取消而非消费，不能谎报“已加入上下文”。
			if (cancelled.has(item.inputId)) continue;
			showNotice('已加入上下文');
		}
	}

	function apply(payload) {
		if (disposed) return;
		const result = reduceQueueState(state, payload);
		if (!result.accepted) return;
		const previous = state.items;
		Object.assign(state, result.state);
		announceConsumedSteers(previous, state.items, state.cancelledInputIds);
	}

	function onState(payload) { apply(payload); }
	const offState = bridge.on('conversation-input/state', onState);

	async function subscribe(conversationId) {
		const current = ++generation;
		editing.value = null;
		clearNotice();
		locallyCancelled.clear();
		state.conversationId = conversationId;
		state.subscriptionId = null;
		state.queueRevision = 0;
		state.loaded = false;
		state.paused = false;
		state.pauseReason = null;
		state.canSteer = false;
		state.steerReason = null;
		state.steerTargetTurnId = null;
		state.error = '';
		state.items = [];
		state.cancelledInputIds = [];
		if (!conversationId) return;
		const subscriptionId = crypto.randomUUID();
		state.subscriptionId = subscriptionId;
		try {
			const response = await bridge.request('conversation-input/subscribe', { subscriptionId, conversationId });
			if (disposed || current !== generation) return;
			apply(response);
		} catch (error) {
			if (!disposed && current === generation) state.error = error?.message || '';
		}
	}

	// 重载或冲突后重新拉完整状态；永远不把局部返回值当快照合并。
	async function refresh() {
		if (!state.subscriptionId) return;
		const current = generation;
		try {
			const response = await bridge.request('conversation-input/get-state', { subscriptionId: state.subscriptionId });
			if (disposed || current !== generation) return;
			apply(response);
		} catch (error) {
			if (!disposed && current === generation) state.error = error?.message || '';
		}
	}

	async function run(operation, payload) {
		const current = generation;
		state.error = '';
		try {
			const response = await bridge.request(`conversation-input/${operation}`, payload);
			if (disposed || current !== generation) return null;
			if (response?.ok === false) {
				state.error = describeInputReason(response.reason, response.error || '队列操作未生效。');
				if (response.conflict || response.status === 'conflict' || response.status === 'missing') await refresh();
			}
			return response;
		} catch (error) {
			if (!disposed && current === generation) state.error = error?.message || '';
			return null;
		}
	}

	const cancel = (inputId, revision) => {
		locallyCancelled.add(inputId);
		const pending = run('cancel', { inputId, expectedRevision: revision });
		// 冲突/超时后该项仍可能是被宿主消费而消失：撤销抑制标记，让下一次快照给出提示。
		pending.then(response => { if (!response?.ok) locallyCancelled.delete(inputId); });
		return pending;
	};
	const retry = (inputId, revision) => run('retry', { inputId, expectedRevision: revision });
	const pause = () => run('pause', { conversationId: state.conversationId, expectedQueueRevision: state.queueRevision });
	const resume = () => run('resume', { conversationId: state.conversationId, expectedQueueRevision: state.queueRevision });

	function promote(inputId, revision) {
		if (!state.canSteer || !state.steerTargetTurnId) {
			state.error = describeInputReason(state.steerReason, '当前回合不能调整。');
			return Promise.resolve(null);
		}

		return run('promote', { inputId, expectedRevision: revision, targetTurnId: state.steerTargetTurnId });
	}

	async function startEdit(inputId) {
		editing.value = null;
		state.error = '';
		const current = generation;
		try {
			const response = await bridge.request('conversation-input/detail', { inputId });
			if (disposed || current !== generation || !response?.ok) return;
			if (String(response.inputId || '').toLowerCase() !== String(inputId).toLowerCase()) return;
			editing.value = { inputId, revision: Number(response.revision) || 0, prompt: response.prompt || '', saving: false };
		} catch (error) {
			if (!disposed && current === generation) state.error = error?.message || '';
		}
	}

	function setEditPrompt(prompt) {
		if (editing.value) editing.value = { ...editing.value, prompt };
	}

	function cancelEdit() { editing.value = null; }

	async function saveEdit() {
		if (!editing.value || editing.value.saving) return;
		const draft = { ...editing.value, saving: true };
		editing.value = draft;
		state.error = '';
		const current = generation;
		let response = null;
		try {
			response = await bridge.request('conversation-input/edit', {
				inputId: draft.inputId, expectedRevision: draft.revision, prompt: draft.prompt,
			});
		} catch (error) {
			if (!disposed && current === generation) state.error = error?.message || '';
			if (editing.value?.inputId === draft.inputId) editing.value = { ...editing.value, saving: false };
			return;
		}
		if (disposed || current !== generation) return;
		if (response?.ok) {
			editing.value = null;
			showNotice('已更新待发送内容');
			return;
		}

		// 冲突时保留用户正在编辑的正文，只刷新列表并跟进最新 revision，让用户可以直接重试。
		if (editing.value?.inputId === draft.inputId) editing.value = { ...editing.value, saving: false };
		state.error = describeInputReason(response?.reason, response?.error || '编辑未生效，请核对最新队列后重试。');
		await refresh();
		const latest = state.items.find(item => item.inputId === draft.inputId);
		if (latest && editing.value?.inputId === draft.inputId) {
			editing.value = { ...editing.value, revision: latest.revision };
		}
	}

	function unsubscribe() {
		if (state.subscriptionId) bridge.post({ type: 'conversation-input/unsubscribe', subscriptionId: state.subscriptionId });
		state.subscriptionId = null;
		state.items = [];
		state.loaded = false;
	}

	onScopeDispose(() => {
		disposed = true;
		generation++;
		offState?.();
		if (noticeTimer) clearTimeout(noticeTimer);
		unsubscribe();
	});

	return {
		state: readonly(state), editing: readonly(editing), notice: readonly(notice),
		subscribe, unsubscribe, refresh, cancel, edit: startEdit, setEditPrompt, cancelEdit, saveEdit,
		retry, promote, pause, resume, clearNotice,
	};
}
