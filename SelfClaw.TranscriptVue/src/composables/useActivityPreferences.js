import { computed, reactive } from 'vue';
import { useTranscriptCollapse } from './useTranscriptCollapse.js';

const preferences = new Map();
export function useActivityPreferences(parentId) {
	return computed(() => {
		const key = parentId.value || 'empty';
		if (!preferences.has(key)) {
			if (preferences.size >= 32) preferences.delete(preferences.keys().next().value);
			preferences.set(key, {
				// 开合与分区由 useSideDock 接管；这里只记每个会话选中的任务。
				view: reactive({ taskId: null }),
				collapse: useTranscriptCollapse(), readings: new Map(),
			});
		}
		return preferences.get(key);
	});
}
