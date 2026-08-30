import { onUnmounted } from 'vue';

// 与宿主（WebView2）之间唯一的消息通道。
//
// 整个前端里 window.chrome.webview 只应出现在这个文件。其余模块通过
// 下面的三类原语与宿主交互：
//   request / requestLatest —— 请求-回包，按 requestId 关联，返回 Promise；
//   on                       —— 订阅宿主主动推送的状态型消息（replaceState、terminal-* 等）；
//   post                     —— 即发即忘（send-prompt、terminal-input、window-* 等）。
//
// 唯一的 addEventListener('message') 在模块初始化时绑定，随应用生命周期存在、
// 永不解绑（WebView 生命周期 == 应用生命周期）。组件侧的订阅经 useHostBridge()
// 在 onUnmounted 自动退订，监听器本身不随组件生灭波动。

const DEFAULT_TIMEOUT_MS = 30000;

// requestLatest 作废旧请求时，其 Promise 以此错误 reject。调用方应静默吞掉它。
export class SupersededError extends Error {
	constructor(message = '请求已被更新的请求取代。') {
		super(message);
		this.name = 'SupersededError';
	}
}

export function isSuperseded(error) {
	return error instanceof SupersededError || error?.name === 'SupersededError';
}

function createHostBridge() {
	const webview = window.chrome?.webview;

	let sequence = 0;
	// requestId -> { type, resolve, reject, timer }
	const pendingRequests = new Map();
	// requestLatest 的 key -> 当前生效的 requestId
	const latestRequestIds = new Map();
	// type -> Set<handler>
	const subscribers = new Map();
	// 标记为 replayLast 的 type -> 最近一次 payload
	const stickyPayloads = new Map();
	const stickyTypes = new Set();
	let transcriptState = null;
	let transcriptAckFrame = null;
	let pendingTranscriptRevision = 0;

	function hasHost() {
		return Boolean(webview);
	}

	function post(message) {
		webview?.postMessage(message);
	}

	function nextRequestId() {
		return `host-${Date.now()}-${++sequence}`;
	}

	function settlePending(requestId, settle) {
		const pending = pendingRequests.get(requestId);
		if (!pending) {
			return false;
		}

		if (pending.timer !== null) window.clearTimeout(pending.timer);
		pendingRequests.delete(requestId);
		settle(pending);
		return true;
	}

	function request(type, payload = {}, { timeout = DEFAULT_TIMEOUT_MS } = {}) {
		if (!hasHost()) {
			return Promise.reject(new Error('WebView 宿主不可用。'));
		}

		const requestId = nextRequestId();
		const promise = new Promise((resolve, reject) => {
			const timer =
				timeout > 0
					? window.setTimeout(() => {
							settlePending(requestId, (pending) => pending.reject(new Error('请求超时，请稍后重试。')));
						}, timeout)
					: null;

			pendingRequests.set(requestId, { type, resolve, reject, timer });
			post({ type, requestId, ...payload });
		});
		promise.requestId = requestId;
		return promise;
	}

	// 同一 key 上发起新请求时，上一个未决请求立即以 SupersededError 作废，
	// 调用方 await 时只会拿到最新一次的回包。key 可用固定串（全局 latest-wins）
	// 或业务标识（如 CLI id，形成 per-key latest-wins）。
	function requestLatest(key, type, payload = {}, options) {
		const previousId = latestRequestIds.get(key);
		if (previousId) {
			settlePending(previousId, (pending) => pending.reject(new SupersededError()));
		}

		const promise = request(type, payload, options);
		latestRequestIds.set(key, promise.requestId);

		const clearIfCurrent = () => {
			if (latestRequestIds.get(key) === promise.requestId) {
				latestRequestIds.delete(key);
			}
		};
		promise.then(clearIfCurrent, clearIfCurrent);
		return promise;
	}

	function dispatchPush(payload) {
		const type = payload?.type;
		if (!type) {
			return;
		}

		if (stickyTypes.has(type)) {
			stickyPayloads.set(type, payload);
		}

		const handlers = subscribers.get(type);
		if (!handlers) {
			return;
		}

		for (const handler of [...handlers]) {
			handler(payload);
		}
	}

	function reduceTranscriptPush(payload) {
		if (payload.type === 'replaceState') {
			transcriptState = { ...payload, type: 'replaceState' };
			return transcriptState;
		}

		if (payload.type !== 'patchState' || !transcriptState) {
			return null;
		}

		const itemsById = new Map((Array.isArray(transcriptState.items) ? transcriptState.items : []).map((item) => [item.id, item]));
		for (const itemId of payload.removedItemIds || []) {
			itemsById.delete(itemId);
		}

		for (const item of payload.upsertItems || []) {
			itemsById.set(item.id, item);
		}

		const itemOrder = Array.isArray(payload.itemOrder) ? payload.itemOrder : Array.from(itemsById.keys());
		const items = itemOrder.map((itemId) => itemsById.get(itemId)).filter(Boolean);

		transcriptState = {
			...transcriptState,
			...payload,
			type: 'replaceState',
			items,
			conversations: Array.isArray(payload.conversations) ? payload.conversations : transcriptState.conversations,
		};
		// 后端 DefaultIgnoreCondition=WhenWritingNull 会把 null 标量字段整个省略，
		// 扩展运算符因此不会覆盖旧值。这里显式以 payload 为准重置，保证「取消选中」
		// 这类 null 广播能真正生效。
		transcriptState.selectedConversationId = payload.selectedConversationId ?? null;
		delete transcriptState.upsertItems;
		delete transcriptState.removedItemIds;
		delete transcriptState.itemOrder;
		return transcriptState;
	}

	function scheduleTranscriptAcknowledgement(revision) {
		if (!Number.isSafeInteger(revision) || revision <= 0) {
			return;
		}

		pendingTranscriptRevision = Math.max(pendingTranscriptRevision, revision);
		if (transcriptAckFrame !== null) {
			return;
		}

		const schedule =
			typeof window.requestAnimationFrame === 'function' ? window.requestAnimationFrame.bind(window) : (callback) => window.setTimeout(callback, 0);
		transcriptAckFrame = schedule(() => {
			transcriptAckFrame = null;
			const acknowledgedRevision = pendingTranscriptRevision;
			pendingTranscriptRevision = 0;
			post({ type: 'transcript-rendered', revision: acknowledgedRevision });
		});
	}

	function on(type, handler, { replayLast = false } = {}) {
		let handlers = subscribers.get(type);
		if (!handlers) {
			handlers = new Set();
			subscribers.set(type, handlers);
		}

		handlers.add(handler);

		if (replayLast) {
			stickyTypes.add(type);
			const last = stickyPayloads.get(type);
			if (last !== undefined) {
				handler(last);
			}
		}

		return () => {
			const set = subscribers.get(type);
			if (!set) {
				return;
			}

			set.delete(handler);
			if (set.size === 0) {
				subscribers.delete(type);
			}
		};
	}

	function handleIncomingMessage(event) {
		const payload = event?.data;
		if (!payload || typeof payload !== 'object') {
			return;
		}

		// 按 requestId 关联未决请求即可——requestId 由本模块单调生成、全局唯一，
		// 宿主原样回显。回包的 type 允许与请求 type 不同（如 get-programming-assistant-settings
		// 的回包 type 是 programming-assistant-settings），故不参与关联判断。
		// 无匹配 requestId（含不带 requestId 的广播）一律视为宿主主动推送。
		const requestId = payload.requestId;
		if (requestId && pendingRequests.has(requestId)) {
			settlePending(requestId, (pending) => {
				if (payload.error) {
					pending.reject(new Error(payload.error));
				} else {
					pending.resolve(payload);
				}
			});
			return;
		}

		if (payload.type === 'replaceState' || payload.type === 'patchState') {
			const reduced = reduceTranscriptPush(payload);
			if (reduced) {
				dispatchPush(reduced);
				scheduleTranscriptAcknowledgement(payload.revision);
			}
			return;
		}

		dispatchPush(payload);
	}

	webview?.addEventListener('message', handleIncomingMessage);

	return { hasHost, request, requestLatest, on, post };
}

export const hostBridge = createHostBridge();

// 组件侧入口：on(...) 的订阅在组件卸载时自动退订，避免懒加载/动态组件留下野回调。
export function useHostBridge() {
	const disposers = [];

	function on(type, handler, options) {
		const dispose = hostBridge.on(type, handler, options);
		disposers.push(dispose);
		return dispose;
	}

	onUnmounted(() => {
		while (disposers.length) {
			disposers.pop()();
		}
	});

	return {
		hasHost: hostBridge.hasHost,
		request: hostBridge.request,
		requestLatest: hostBridge.requestLatest,
		post: hostBridge.post,
		on,
	};
}
