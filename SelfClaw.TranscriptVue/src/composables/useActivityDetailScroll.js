import { nextTick, onScopeDispose, watch } from 'vue';

export function useActivityDetailScroll(detail, scrollElement) {
	let generation = 0;
	let restoring = false;

	watch(detail.scrollRequest, async (request) => {
		const current = ++generation;
		restoring = true;
		await nextTick();
		if (current !== generation) return;
		const element = scrollElement.value;
		if (request && element) {
			const saved = request.position;
			const anchor = saved?.anchorId && [...element.querySelectorAll('[data-activity-block-id]')]
				.find((block) => block.dataset.activityBlockId === saved.anchorId);
			if (request.following) element.scrollTop = element.scrollHeight;
			else if (anchor) element.scrollTop += anchor.getBoundingClientRect().top - element.getBoundingClientRect().top - saved.anchorTop;
			else element.scrollTop = saved?.blockOffset === request.blockOffset ? saved.top : 0;
		}
		restoring = false;
	}, { immediate: true, flush: 'post' });

	watch(scrollElement, (element, _, onCleanup) => {
		if (!element || typeof ResizeObserver === 'undefined') return;
		const observer = new ResizeObserver(() => {
			if (detail.following.value && detail.displayed.value && !detail.loading.value) element.scrollTop = element.scrollHeight;
		});
		observer.observe(element);
		if (element.firstElementChild) observer.observe(element.firstElementChild);
		onCleanup(() => observer.disconnect());
	}, { flush: 'post' });

	function onScroll() {
		const element = scrollElement.value;
		const value = detail.displayed.value;
		if (!element || !value || restoring) return;
		const top = element.getBoundingClientRect().top;
		const anchor = [...element.querySelectorAll('[data-activity-block-id]')]
			.find((block) => block.getBoundingClientRect().bottom > top);
		detail.recordScroll({
			taskId: value.taskId, contentVersion: value.contentVersion, blockOffset: value.blockOffset, top: element.scrollTop,
			anchorId: anchor?.dataset.activityBlockId, anchorTop: anchor ? anchor.getBoundingClientRect().top - top : 0,
		}, element.scrollHeight - element.scrollTop - element.clientHeight < 35);
	}

	onScopeDispose(() => { generation++; });
	return { onScroll };
}
