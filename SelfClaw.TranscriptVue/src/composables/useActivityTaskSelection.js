import { computed, watch } from 'vue';

export function useActivityTaskSelection(panel, activity, preferences, constrained) {
	const detailVisible = computed(() => preferences.value.view.open && !constrained.value);
	let restoredSubscription = null;

	function select(taskId) {
		preferences.value.view.taskId = taskId;
		activity.selectTask(taskId);
	}

	watch(detailVisible, (open) => {
		if (panel.subscription.value) activity.selectTask(open ? preferences.value.view.taskId : null);
	});
	watch(panel.section, (section) => {
		if (!section || restoredSubscription === panel.subscription.value) return;
		restoredSubscription = panel.subscription.value;
		if (detailVisible.value && preferences.value.view.taskId) activity.selectTask(preferences.value.view.taskId);
	});
	return { select };
}
