import { computed, nextTick, onUnmounted, ref, watch } from 'vue';
import { useHostBridge } from './hostBridge.js';
import { createEmptyActivityState, getSubagentSection, reduceActivityState } from '../renderers/activityPanelState.js';

export function useActivityPanel(parentId) {
	const bridge = useHostBridge();
	const state = ref(createEmptyActivityState());
	const subscription = ref(null);
	const selection = ref(null);
	const loading = ref(false);
	const error = ref('');
	const section = computed(() => getSubagentSection(state.value));
	let disposed = false;
	let cursor = null;
	let retryTimer = null;
	let requestGeneration = 0;

	function apply(incoming) {
		const result = reduceActivityState(state.value, incoming, subscription.value, parentId.value ?? null, selection.value);
		if (result.accepted) {
			state.value = result.state;
			loading.value = false;
			error.value = result.state.stateError || '';
			if (section.value?.listReset) cursor = null;
		}
		if (result.acknowledge) {
			const id = incoming.subscriptionId;
			nextTick(() => {
				if (subscription.value === id) bridge.post({ type: 'activity-panel/rendered', subscriptionId: id, revision: incoming.revision });
			});
		}
	}

	async function requestState(operation, payload = {}) {
		const id = subscription.value;
		if (!id) return;
		const generation = ++requestGeneration;
		try {
			const response = await bridge.request(`activity-panel/${operation}`, { ...payload, subscriptionId: id });
			if (subscription.value === id && response.type === 'activity-panel/state') apply(response);
			return response;
		} catch (failure) {
			if (subscription.value !== id || generation !== requestGeneration) return;
			error.value = failure.message;
			loading.value = false;
			throw failure;
		}
	}

	async function subscribe() {
		window.clearTimeout(retryTimer);
		if (subscription.value) bridge.post({ type: 'activity-panel/unsubscribe', subscriptionId: subscription.value });
		state.value = createEmptyActivityState();
		selection.value = null;
		cursor = null;
		error.value = '';
		loading.value = false;
		subscription.value = null;
		if (disposed || !parentId.value || !bridge.hasHost()) return;
		subscription.value = crypto.randomUUID();
		loading.value = true;
		const id = subscription.value;
		try { await requestState('subscribe', { parentConversationId: parentId.value }); }
		catch (failure) {
			if (subscription.value === id && failure.message === 'activity-scope-mismatch') {
				retryTimer = window.setTimeout(subscribe, 150);
			}
		}
	}

	async function refresh(nextCursor = cursor) {
		if (!subscription.value) return subscribe();
		cursor = nextCursor;
		try { return await requestState('get-state', { cursor }); }
		catch (failure) {
			if (failure.message === 'activity-subscription-invalid') return subscribe();
			return null;
		}
	}

	function onVisible() { if (document.visibilityState === 'visible') refresh(); }
	bridge.on('activity-panel/state', apply);
	watch(parentId, subscribe, { immediate: true, flush: 'sync' });
	document.addEventListener('visibilitychange', onVisible);
	onUnmounted(() => {
		disposed = true;
		window.clearTimeout(retryTimer);
		document.removeEventListener('visibilitychange', onVisible);
		if (subscription.value) bridge.post({ type: 'activity-panel/unsubscribe', subscriptionId: subscription.value });
		subscription.value = null;
	});
	return { state, section, selection, subscription, loading, error, requestState, refresh, bridge };
}
