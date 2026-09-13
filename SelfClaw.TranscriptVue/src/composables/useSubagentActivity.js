import { ref, watch } from 'vue';

export function useSubagentActivity(panel) {
	const cancelling = ref(new Set());
	const commandError = ref('');
	const pages = ref([null]);
	const pageIndex = ref(0);
	const pageLoading = ref(false);
	let requestedCursor = null;
	watch(panel.subscription, () => {
		cancelling.value = new Set();
		commandError.value = '';
		pages.value = [null];
		pageIndex.value = 0;
		pageLoading.value = false;
		requestedCursor = null;
	});
	watch(panel.section, (section) => {
		if (section?.listReset) { pages.value = [null]; pageIndex.value = 0; pageLoading.value = false; }
		else if (section) {
			const cursor = section.cursor ?? null;
			const index = pages.value.indexOf(cursor);
			if (index >= 0) pageIndex.value = index;
			if (cursor === requestedCursor) pageLoading.value = false;
		}
	});

	async function cancelTask(task) {
		const subscription = panel.subscription.value;
		cancelling.value = new Set([...cancelling.value, task.taskId]);
		try { await panel.cancelTask(task.taskId); }
		catch (failure) { if (subscription === panel.subscription.value) commandError.value = failure.message; }
		finally {
			if (subscription === panel.subscription.value) { const next = new Set(cancelling.value); next.delete(task.taskId); cancelling.value = next; }
		}
	}

	async function changePage(direction) {
		if (pageLoading.value) return;
		const subscription = panel.subscription.value;
		let targetIndex = Math.max(0, pageIndex.value - 1);
		if (direction > 0) {
			const next = panel.section.value?.nextCursor;
			if (!next) return;
			pages.value = [...pages.value.slice(0, pageIndex.value + 1), next];
			targetIndex = pageIndex.value + 1;
		}
		requestedCursor = pages.value[targetIndex];
		pageLoading.value = true;
		const response = await panel.refresh(requestedCursor);
		if (subscription === panel.subscription.value && (!response || response.stateError)) pageLoading.value = false;
	}

	return { cancelling, commandError, pageIndex, pageLoading, cancelTask, changePage };
}
