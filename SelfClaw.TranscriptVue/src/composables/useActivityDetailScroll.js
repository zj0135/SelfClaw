import { computed, nextTick, onScopeDispose, ref, shallowRef, watch } from 'vue';

export function useActivityDetailScroll(detail, scrollElement, invalidated, positions = new Map()) {
	const displayed = shallowRef(null);
	const following = ref(true);
	const hasNewContent = computed(() => invalidated?.value || (detail.value && displayed.value?.contentVersion !== detail.value.contentVersion));
	let generation = 0;

	function savePosition() {
		const value = displayed.value;
		const element = scrollElement.value;
		if (!value || !element) return;
		const top = element.getBoundingClientRect?.().top ?? 0;
		const anchor = [...(element.querySelectorAll?.('[data-activity-block-id]') ?? [])]
			.find((block) => block.getBoundingClientRect().bottom > top);
		positions.delete(value.taskId);
		positions.set(value.taskId, {
			top: element.scrollTop, following: following.value, blockOffset: value.blockOffset,
			anchorId: anchor?.dataset.activityBlockId, anchorTop: anchor ? anchor.getBoundingClientRect().top - top : 0,
		});
		if (positions.size > 64) positions.delete(positions.keys().next().value);
	}

	function restorePosition(saved, value) {
		const element = scrollElement.value;
		if (!element) return;
		const anchor = saved?.anchorId && [...(element.querySelectorAll?.('[data-activity-block-id]') ?? [])]
			.find((block) => block.dataset.activityBlockId === saved.anchorId);
		if (saved?.following) element.scrollTop = element.scrollHeight;
		else if (anchor) element.scrollTop += anchor.getBoundingClientRect().top - element.getBoundingClientRect().top - saved.anchorTop;
		else element.scrollTop = saved && saved.blockOffset === value.blockOffset ? saved.top : 0;
	}

	watch(detail, async (value) => {
		const current = ++generation;
		const changedTask = value?.taskId !== displayed.value?.taskId;
		if (changedTask) savePosition();
		const saved = changedTask && value ? positions.get(value.taskId) : null;
		if (changedTask) following.value = saved?.following ?? true;
		if (following.value || changedTask || !value) {
			displayed.value = value;
			await nextTick();
			if (current !== generation || !value) return;
			const element = scrollElement.value;
			if (changedTask) restorePosition(saved, value);
			else if (element && following.value) element.scrollTop = element.scrollHeight;
		}
	}, { immediate: true });

	watch(scrollElement, (element, _, onCleanup) => {
		if (!element || typeof ResizeObserver === 'undefined') return;
		const observer = new ResizeObserver(() => {
			if (following.value && displayed.value) element.scrollTop = element.scrollHeight;
		});
		observer.observe(element);
		if (element.firstElementChild) observer.observe(element.firstElementChild);
		onCleanup(() => observer.disconnect());
	}, { flush: 'post' });

	function onScroll() {
		const element = scrollElement.value;
		if (!element || !displayed.value) return;
		following.value = element.scrollHeight - element.scrollTop - element.clientHeight < 35;
		savePosition();
		if (following.value && hasNewContent.value && !invalidated?.value) resume();
	}

	async function resume() {
		const current = ++generation;
		following.value = true;
		displayed.value = detail.value;
		await nextTick();
		if (current !== generation) return;
		if (scrollElement.value) scrollElement.value.scrollTop = scrollElement.value.scrollHeight;
		savePosition();
	}

	onScopeDispose(() => { generation++; savePosition(); });
	return { displayed, hasNewContent, onScroll, resume };
}
