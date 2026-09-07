import { computed, onMounted, ref } from 'vue';
import { useHostBridge, isSuperseded } from './hostBridge.js';
import { useToast } from './useToast.js';

export function useModelManagement() {
	const { request, requestLatest } = useHostBridge();
	const { showToast } = useToast();
	const models = ref([]);
	const search = ref('');
	const loading = ref(false);
	const error = ref('');
	const editing = ref(null);
	const deleting = ref(null);
	const filteredModels = computed(() => {
		const term = search.value.trim().toLowerCase();
		return models.value.filter((model) => `${model.model} ${model.name}`.toLowerCase().includes(term));
	});

	async function load() {
		loading.value = true;
		error.value = '';
		try {
			const payload = await requestLatest('model-configurations', 'ai-providers/list-model-configurations');
			models.value = payload.configurations ?? [];
		} catch (failure) {
			if (!isSuperseded(failure)) error.value = failure.message || '加载模型配置失败';
		} finally {
			loading.value = false;
		}
	}

	function saved(configuration) {
		const index = models.value.findIndex((model) => model.model === configuration.model);
		if (index >= 0) models.value.splice(index, 1, configuration);
		else models.value.push(configuration);
		editing.value = null;
		showToast('模型配置已保存');
	}

	async function remove(model) {
		if (deleting.value || !window.confirm(`删除 ${model.model} 的共享配置？关联模型将恢复提供商参数。`)) return;
		deleting.value = model.model;
		try {
			await request('ai-providers/delete-model-configuration', { model: model.model });
			models.value = models.value.filter((item) => item.model !== model.model);
			showToast('模型配置已删除');
		} catch (failure) {
			showToast(failure.message || '删除模型配置失败');
		} finally {
			deleting.value = null;
		}
	}

	onMounted(load);
	return { models, search, loading, error, editing, deleting, filteredModels, load, saved, remove };
}
