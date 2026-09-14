import { nextTick, onUnmounted, readonly, ref } from 'vue';
import { hostBridge } from './hostBridge.js';

export function createTranscriptBridge(transport, afterRender = async () => {
	await nextTick();
	await new Promise((resolve) => (window.requestAnimationFrame || ((callback) => window.setTimeout(callback, 0)))(resolve));
}) {
	let transcriptState = null;
	let generation = 0;
	let failedRevision = null;
	let requestedRecovery = false;
	const handlers = new Map();
	const error = ref('');

	function reduceTranscriptPush(payload) {
		if (payload.type === 'replaceState') {
			transcriptState = { ...payload, type: 'replaceState' };
			return transcriptState;
		}

		if (payload.type !== 'patchState' || !transcriptState || payload.baseRevision !== transcriptState.revision) {
			throw new Error('Transcript patch has no matching base revision.');
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
        transcriptState.activityText = payload.activityText ?? null;
		delete transcriptState.upsertItems;
		delete transcriptState.removedItemIds;
		delete transcriptState.itemOrder;
		return transcriptState;
	}


	function reportRenderFailure(failure, revision = transcriptState?.revision) {
		error.value = failure?.message || String(failure);
		if (!Number.isSafeInteger(revision) || failedRevision === revision) return;
		failedRevision = revision;
		transport.post({ type: 'transcript-rejected', revision });
	}

	async function publish(payload) {
		const currentGeneration = ++generation;
		let hasRenderer = false;
		for (const [handler, critical] of handlers) {
			try { handler(payload); }
			catch (failure) {
				console.error('Transcript subscriber failed.', failure);
				if (critical) reportRenderFailure(failure, payload.revision);
			}
			hasRenderer ||= critical;
		}
		if (!hasRenderer) return;
		try { await afterRender(); }
		catch (failure) { reportRenderFailure(failure, payload.revision); }
		if (currentGeneration === generation && failedRevision !== payload.revision) {
            error.value = '';
			transport.post({ type: 'transcript-applied', revision: payload.revision });
        }
	}

	function receive(payload) {
		try {
			if (!Number.isSafeInteger(payload.revision) || payload.revision <= 0)
				throw new Error('Invalid transcript revision.');
			if (transcriptState && payload.revision < transcriptState.revision) return;
			if (payload.type === 'replaceState' && !Array.isArray(payload.items))
				throw new Error('Invalid transcript content.');
			const reduced = reduceTranscriptPush(payload);
			error.value = '';
			requestedRecovery = false;
			void publish(reduced);
		} catch (failure) { reportRenderFailure(failure, payload.revision); }
	}

	function resynchronize() {
		requestedRecovery = true;
		error.value = '';
		transport.post({ type: 'transcript-resync' });
	}

	function on(handler, { critical = true } = {}) {
		handlers.set(handler, critical);
		if (transcriptState) {
            if (critical && (failedRevision === transcriptState.revision || error.value) && !requestedRecovery) resynchronize();
            else void publish(transcriptState);
        }
		else if (!requestedRecovery) resynchronize();
		return () => handlers.delete(handler);
	}

	const disposers = [
		transport.on('replaceState', receive),
		transport.on('patchState', receive),
		transport.on('transcript-unavailable', (payload) => { error.value = payload.error; }),
	];
	return { on, error: readonly(error), reportRenderFailure, resynchronize,
		dispose() { disposers.forEach((dispose) => dispose()); handlers.clear(); generation++; } };
}

export const transcriptBridge = createTranscriptBridge(hostBridge);
export function useTranscriptBridge() {
	const disposers = [];
	onUnmounted(() => disposers.forEach((dispose) => dispose()));
	return { ...transcriptBridge, on(handler, options) {
		const dispose = transcriptBridge.on(handler, options);
		disposers.push(dispose);
		return dispose;
	} };
}
