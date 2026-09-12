import { computed, ref, shallowRef, watch } from 'vue';

export function useSubagentActivity(panel) {
	const selectedTaskId = ref(null);
	const detailLoading = ref(false);
	const cancelling = ref(new Set());
	const commandError = ref('');
	const pages = ref([null]);
	const pageIndex = ref(0);
	const pageLoading = ref(false);
	let requestedCursor = null;
	const retainedDetail = shallowRef(null);
	const detailInvalidated = computed(() => panel.section.value?.detailSelectionId === panel.selection.value && panel.section.value?.detailError === 'content-changed');
	const detail = computed(() => {
		const value = panel.section.value;
		if (detailInvalidated.value && retainedDetail.value?.taskId === selectedTaskId.value) {
			return { ...retainedDetail.value, task: value.selectedTask ?? retainedDetail.value.task };
		}
		return value?.detailSelectionId === panel.selection.value && value.detail?.taskId === selectedTaskId.value ? value.detail : null;
	});
	watch(panel.section, (value) => {
		if (value?.detailSelectionId === panel.selection.value && value.detail?.taskId === selectedTaskId.value) retainedDetail.value = value.detail;
	}, { flush: 'sync' });

	watch(panel.subscription, () => {
		selectedTaskId.value = null;
		retainedDetail.value = null;
		detailLoading.value = false;
		cancelling.value = new Set();
		commandError.value = '';
		pages.value = [null];
		pageIndex.value = 0;
		pageLoading.value = false;
		requestedCursor = null;
	});
	watch(panel.section, (section) => {
		if (section?.detailSelectionId === panel.selection.value) detailLoading.value = false;
		if (section?.listReset) { pages.value = [null]; pageIndex.value = 0; pageLoading.value = false; }
		else if (section) {
			const cursor = section.cursor ?? null;
			const index = pages.value.indexOf(cursor);
			if (index >= 0) pageIndex.value = index;
			if (cursor === requestedCursor) pageLoading.value = false;
		}
	});

	async function selectTask(taskId, blockOffset = null, contentVersion = null) {
		const id = crypto.randomUUID();
		panel.selection.value = id;
		selectedTaskId.value = taskId;
		detailLoading.value = taskId !== null;
		commandError.value = '';
		try { await panel.requestState('select-detail', { taskId, detailSelectionId: id, blockOffset, contentVersion }); }
		catch (failure) { if (panel.selection.value === id) { commandError.value = failure.message; detailLoading.value = false; } }
	}

	async function cancelTask(task) {
		const subscription = panel.subscription.value;
		cancelling.value = new Set([...cancelling.value, task.taskId]);
		try { await panel.bridge.request('activity-panel/cancel-task', { subscriptionId: subscription, taskId: task.taskId }); }
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

	async function readContent(reference, offset, contentVersion) {
		const subscription = panel.subscription.value;
		const selection = panel.selection.value;
		const current = detail.value;
		if (!current) throw new Error('activity-detail-selection-invalid');
		const response = await panel.bridge.request('activity-panel/read-content', {
			subscriptionId: subscription, detailSelectionId: selection, taskId: current.taskId,
			contentVersion: contentVersion ?? current.contentVersion, contentId: reference.contentId, offset,
		});
		if (subscription !== panel.subscription.value || selection !== panel.selection.value ||
			current.contentVersion !== detail.value?.contentVersion) throw new Error('content-changed');
		return response;
	}
	return { detail, detailInvalidated, selectedTaskId, detailLoading, cancelling, commandError, pageIndex, pageLoading, selectTask, cancelTask, changePage, readContent };
}
