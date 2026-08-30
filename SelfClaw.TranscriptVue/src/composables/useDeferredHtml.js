import { onUnmounted, ref, watch } from 'vue';

export function useDeferredHtml(source, shouldDefer, delayMs = 160) {
	const html = ref('');
	let pendingHtml = '';
	let timer = null;

	function clearTimer() {
		if (timer !== null) {
			window.clearTimeout(timer);
			timer = null;
		}
	}

	watch(
		[source, shouldDefer],
		([nextHtml, defer]) => {
			pendingHtml = nextHtml || '';
			if (!defer) {
				clearTimer();
				html.value = pendingHtml;
				return;
			}

			if (timer !== null) {
				return;
			}

			timer = window.setTimeout(() => {
				timer = null;
				html.value = pendingHtml;
			}, delayMs);
		},
		{ immediate: true }
	);

	onUnmounted(clearTimer);
	return html;
}
