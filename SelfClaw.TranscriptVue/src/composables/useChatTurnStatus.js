import { computed, onScopeDispose, ref, watch } from 'vue';
import { formatElapsedTime } from '../renderers/elapsedTime.js';

export function useChatTurnStatus(session) {
	const startedAt = ref(null);
	const now = ref(0);
	let timer = null;

	function stopClock() {
		window.clearInterval(timer);
		timer = null;
		startedAt.value = null;
	}

	watch([() => session.isBusy, () => session.selectedConversationId], ([busy]) => {
		stopClock();
		if (!busy) return;
		startedAt.value = Date.now();
		now.value = startedAt.value;
		timer = window.setInterval(() => { now.value = Date.now(); }, 1000);
	}, { immediate: true });
	onScopeDispose(stopClock);

	return computed(() => {
		const last = session.items.at(-1);
		const preparing = last?.role === 'assistant' && last.isThinking && !(Array.isArray(last.segments) && last.segments.length);
		if (!session.isBusy || startedAt.value == null || preparing) return null;
		return { label: '执行中', elapsedText: formatElapsedTime(now.value - startedAt.value) };
	});
}
