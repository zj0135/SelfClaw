import { onScopeDispose, reactive, readonly } from 'vue';

// 队列状态是宿主推送的有界快照：订阅时立即拉取一份，之后由 `conversation-input/state`
// 覆盖。旧订阅或旧 queueRevision 的推送一律丢弃，避免切换会话后旧响应回滚新选择。
//
// 该 composable 只持有展示状态与关联请求；调度、暂停和 revision 递增全部由宿主持久化。

function normalizeItem(item) {
	return {
		inputId: item.inputId,
		sequence: Number(item.sequence) || 0,
		kind: item.kind || 'follow-up',
		status: typeof item.status === 'number' ? ['pending', 'claimed', 'consumed', 'held', 'cancelled'][item.status] : (item.status || 'pending'),
		revision: Number(item.revision) || 0,
		preview: item.preview || '',
		reasonCode: item.reasonCode || null,
		errorMessage: item.errorMessage || null,
	};
}

export function createQueueState() {
	return {
		conversationId: null, subscriptionId: null, queueRevision: 0, paused: false,
		pauseReason: null, canSteer: false, items: [], loaded: false, truncated: false,
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
			items: Array.isArray(payload.items) ? payload.items.map(normalizeItem) : [],
			truncated: Boolean(payload.truncated),
			loaded: true,
		},
		accepted: true,
	};
}

export function useConversationInputs(bridge) {
	const state = reactive(createQueueState());
	let generation = 0;
	let disposed = false;

	function apply(payload) {
		if (disposed) return;
		const result = reduceQueueState(state, payload);
		if (!result.accepted) return;
		Object.assign(state, result.state);
	}

	function onState(payload) { apply(payload); }
	const offState = bridge.on('conversation-input/state', onState);

	async function subscribe(conversationId) {
		const current = ++generation;
		state.conversationId = conversationId;
		state.subscriptionId = null;
		state.queueRevision = 0;
		state.loaded = false;
		state.paused = false;
		state.pauseReason = null;
		state.error = '';
		state.items = [];
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

	async function run(operation, payload) {
		const current = generation;
		state.error = '';
		try {
			const response = await bridge.request(`conversation-input/${operation}`, payload);
			if (disposed || current !== generation) return response;
			if (response?.type === 'conversation-input/state' && !disposed) apply(response);
			if (response?.ok === false) state.error = response.error || response.errorCode || '队列操作未生效。';
			return response;
		} catch (error) {
			if (!disposed && current === generation) state.error = error?.message || '';
			return null;
		}
	}

	const cancel = (inputId, revision) => run('cancel', { inputId, expectedRevision: revision });
	const edit = (inputId, revision, prompt) => run('edit', { inputId, expectedRevision: revision, prompt });
	const retry = (inputId, revision) => run('retry', { inputId, expectedRevision: revision });
	const pause = () => run('pause', { conversationId: state.conversationId, expectedQueueRevision: state.queueRevision });
	const resume = () => run('resume', { conversationId: state.conversationId, expectedQueueRevision: state.queueRevision });
	const detail = (inputId) => run('detail', { inputId });

	function unsubscribe() {
		const subscriptionId = state.subscriptionId;
		if (subscriptionId) bridge.post({ type: 'conversation-input/unsubscribe', subscriptionId });
		state.subscriptionId = null;
		state.items = [];
		state.loaded = false;
	}

	onScopeDispose(() => {
		disposed = true;
		generation++;
		offState?.();
		unsubscribe();
	});

	return { state: readonly(state), subscribe, unsubscribe, cancel, edit, retry, pause, resume, detail };
}
