import { computed, readonly, ref, shallowRef, watch } from 'vue';

export function useActivityDetail(panel, preferences, visible) {
	const selectedTaskId = ref(null);
	const content = shallowRef(null);
	const currentTask = shallowRef(null);
	const latest = shallowRef(null);
	const following = ref(true);
	const loading = ref(false);
	const invalidated = ref(false);
	const error = ref('');
	const scrollRequest = shallowRef(null);
	const displayed = computed(() => content.value && { ...content.value, task: currentTask.value ?? content.value.task });
	const hasNewContent = computed(() => invalidated.value || (content.value &&
		(content.value.laterOffset != null || latest.value?.contentVersion !== content.value.contentVersion)));
	let readings = preferences.value.readings;
	let observedSubscription = null;
	let opened = false;
	let generation = 0;
	let pending = null;
	let boundWindow = false;

	function remember(position = readings.get(selectedTaskId.value)?.position ?? null) {
		const value = content.value;
		if (!value) return;
		readings.delete(value.taskId);
		readings.set(value.taskId, {
			following: following.value, blockOffset: value.blockOffset, contentVersion: value.contentVersion,
			window: following.value ? null : value, position,
		});
		if (readings.size > 64) readings.delete(readings.keys().next().value);
	}

	function show(value, position = null) {
		content.value = value;
		scrollRequest.value = { taskId: value.taskId, blockOffset: value.blockOffset, following: following.value, position };
		remember(position);
	}

	async function requestWindow(blockOffset, contentVersion, position = null) {
		const current = ++generation;
		pending = { position };
		boundWindow = blockOffset != null;
		loading.value = selectedTaskId.value !== null;
		error.value = '';
		try { await panel.selectDetail(selectedTaskId.value, blockOffset, contentVersion); }
		catch (failure) {
			if (current !== generation) return;
			error.value = failure.message;
			loading.value = false;
			pending = null;
		}
	}

	function activate(taskId) {
		generation++;
		readings = preferences.value.readings;
		selectedTaskId.value = taskId;
		const saved = taskId ? readings.get(taskId) : null;
		following.value = saved?.following ?? true;
		content.value = saved?.window ?? null;
		currentTask.value = content.value?.task ?? null;
		latest.value = content.value;
		invalidated.value = false;
		scrollRequest.value = content.value ? {
			taskId, blockOffset: content.value.blockOffset, following: false, position: saved.position,
		} : null;
		return requestWindow(following.value ? null : saved.blockOffset, following.value ? null : saved.contentVersion, saved?.position);
	}

	function selectTask(taskId) {
		preferences.value.view.taskId = taskId;
		return activate(taskId);
	}

	function closeDetail() { return selectTask(null); }

	function readWindow(offset) {
		if (!content.value || offset == null || loading.value) return;
		following.value = false;
		remember();
		return requestWindow(offset, content.value.contentVersion);
	}

	function readEarlier() { return readWindow(content.value?.earlierOffset); }
	function readLater() { return readWindow(content.value?.laterOffset); }

	function resumeLatest() {
		if (!selectedTaskId.value || loading.value) return;
		following.value = true;
		return requestWindow(null, null);
	}

	function recordScroll(position, atBottom) {
		const value = content.value;
		if (loading.value || !value || position.taskId !== value.taskId ||
			position.contentVersion !== value.contentVersion || position.blockOffset !== value.blockOffset) return;
		const canFollow = atBottom && value.laterOffset == null && !invalidated.value;
		following.value = canFollow;
		remember(position);
		if (canFollow && boundWindow) resumeLatest();
		else if (canFollow && latest.value && latest.value !== value) show(latest.value);
	}

	function receive(section) {
		if (!selectedTaskId.value || section.detailSelectionId !== panel.selection.value) return;
		currentTask.value = section.selectedTask ?? section.detail?.task ?? currentTask.value;
		invalidated.value = section.detailError === 'content-changed';
		error.value = invalidated.value ? '' : section.detailError ?? '';
		loading.value = false;
		if (invalidated.value) following.value = false;
		if (section.detail?.taskId === selectedTaskId.value) {
			latest.value = section.detail;
			if (pending || following.value || !content.value) show(section.detail, pending?.position);
		}
		pending = null;
		remember();
	}

	watch([panel.subscription, visible, panel.section], ([subscription, open, section]) => {
		if (subscription !== observedSubscription) {
			observedSubscription = subscription;
			generation++;
			opened = false;
			selectedTaskId.value = null;
			content.value = null;
			currentTask.value = null;
			latest.value = null;
			pending = null;
			loading.value = false;
			invalidated.value = false;
			error.value = '';
			scrollRequest.value = null;
		}
		if (!subscription) return;
		if (!open) {
			opened = false;
			if (selectedTaskId.value) activate(null);
			return;
		}
		if (!section) return;
		if (!opened) {
			opened = true;
			if (preferences.value.view.taskId) activate(preferences.value.view.taskId);
			return;
		}
		receive(section);
	}, { immediate: true, flush: 'sync' });

	async function readContent(reference, offset, contentVersion) {
		const subscription = panel.subscription.value;
		const selection = panel.selection.value;
		const value = content.value;
		if (!value) throw new Error('activity-detail-selection-invalid');
		const response = await panel.readContent({
			taskId: value.taskId,
			contentVersion: contentVersion ?? value.contentVersion, contentId: reference.contentId, offset,
		});
		if (subscription !== panel.subscription.value || selection !== panel.selection.value ||
			value.contentVersion !== content.value?.contentVersion) throw new Error('content-changed');
		return response;
	}

	return {
		selectedTaskId: readonly(selectedTaskId), displayed, following: readonly(following), loading: readonly(loading),
		invalidated: readonly(invalidated), error: readonly(error), scrollRequest: readonly(scrollRequest), hasNewContent,
		selectTask, closeDetail, readEarlier, readLater, resumeLatest, recordScroll, readContent,
	};
}
