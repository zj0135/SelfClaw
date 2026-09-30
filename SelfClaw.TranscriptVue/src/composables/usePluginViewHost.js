import { computed, ref, watch } from 'vue';

// 与宿主（C#）的插件视图会话：有哪些视图可用、哪些已打开、用哪份持久化恢复。
// 上限（右侧 8、悬浮 4）由宿主执行，外壳不再自己数一份——同一规则两处实现迟早会漂移，
// 超限的错误消息直接来自宿主。
const SAVE_DEBOUNCE_MS = 400;

export function usePluginViewHost({ request, onOpened, onClosed, onEvicted }) {
	const available = ref([]);
	const openViews = ref([]);
	const activeKey = ref('');
	const error = ref('');
	const opening = new Map();
	const generations = new Map();
	const pluginGenerations = new Map();
	let restoredActiveKey = '';
	let disposed = false;
	let saveTimer = null;

	const dockedViews = computed(() => openViews.value.filter((view) => view.slot === 'right'));
	const floatingViews = computed(() => openViews.value.filter((view) => view.slot === 'floating'));
	const isOpen = computed(() => openViews.value.length > 0);

	function find(key) {
		return openViews.value.find((view) => view.key === key) ?? null;
	}

	function scheduleSave() {
		if (saveTimer) window.clearTimeout(saveTimer);
		saveTimer = window.setTimeout(() => {
			saveTimer = null;
			request('plugin-host/save-views', {
				views: openViews.value.map((view) => view.key),
				activeView: activeKey.value || null,
			}).catch((cause) => { error.value = cause.message; });
		}, SAVE_DEBOUNCE_MS);
	}

	async function load() {
		try {
			const response = await request('plugin-host/get-views');
			available.value = response?.views || [];
			restoredActiveKey = response?.activeView || '';
			error.value = '';
			return response?.openViews || [];
		} catch (cause) {
			error.value = cause?.message || '无法加载插件视图。';
			return [];
		}
	}

	function open(key) {
		const existing = find(key);
		if (existing) {
			activate(key);
			return Promise.resolve(existing);
		}

		if (opening.has(key)) return opening.get(key);
		const version = generations.get(key) || 0;
		const pluginId = key.split('/')[0];
		const pluginVersion = pluginGenerations.get(pluginId) || 0;
		const operation = (async () => {
			try {
				const response = await request('plugin-host/open', { viewKey: key });
				if (disposed || version !== (generations.get(key) || 0) || pluginVersion !== (pluginGenerations.get(pluginId) || 0)) {
					await request('plugin-host/close', { viewKey: key });
					return null;
				}

				// 帧的描述符拉平：组件与推送管线只需要一个对象，避免到处出现 view.view。
				const entry = { ...response.view, url: response.url, ready: false };
				openViews.value = [...openViews.value, entry];
				activate(key);
				error.value = '';
				scheduleSave();
				onOpened?.(entry);
				return entry;
			} catch (cause) {
				if (!disposed) error.value = cause.message;
				return null;
			} finally {
				opening.delete(key);
			}
		})();
		opening.set(key, operation);
		return operation;
	}

	async function close(key) {
		generations.set(key, (generations.get(key) || 0) + 1);
		try {
			await request('plugin-host/close', { viewKey: key });
		} catch (cause) {
			error.value = cause.message;
			return;
		}

		const index = openViews.value.findIndex((view) => view.key === key);
		const removed = openViews.value[index] ?? null;
		openViews.value = openViews.value.filter((view) => view.key !== key);
		if (removed) onClosed?.(removed);
		if (activeKey.value === key) {
			const docked = openViews.value.filter((view) => view.slot === 'right');
			activeKey.value = docked[Math.max(0, Math.min(index, docked.length - 1))]?.key || '';
		}

		scheduleSave();
	}

	function activate(key) {
		const target = find(key);
		if (target?.slot === 'right') activeKey.value = key;
	}

	// 宿主在禁用或删除插件时推送：视图的源已经停止解析，留着帧只会显示一个死框。
	function evict(pluginId) {
		pluginGenerations.set(pluginId, (pluginGenerations.get(pluginId) || 0) + 1);
		if (!openViews.value.some((view) => view.pluginId === pluginId)) return;

		const removed = openViews.value.filter((view) => view.pluginId === pluginId);
		openViews.value = openViews.value.filter((view) => view.pluginId !== pluginId);
		removed.forEach((view) => onEvicted?.(view));
		if (!find(activeKey.value) || find(activeKey.value)?.slot !== 'right') {
			activeKey.value = dockedViews.value[0]?.key || '';
		}

		scheduleSave();
	}

	// 扩展状态变化后：刷新可用列表，并关掉已经不可用的已打开视图。
	async function synchronize() {
		await load();
		const keys = new Set(available.value.map((view) => view.key));
		for (const view of [...openViews.value]) {
			if (!keys.has(view.key)) await close(view.key);
		}
	}

	async function restore(persistedKeys) {
		const keys = new Set(available.value.map((view) => view.key));
		for (const key of persistedKeys.filter((candidate) => keys.has(candidate))) {
			await open(key);
		}

		if (openViews.value.some((view) => view.key === restoredActiveKey && view.slot === 'right')) {
			activeKey.value = restoredActiveKey;
		}
	}

	function dispose() {
		disposed = true;
		if (saveTimer) window.clearTimeout(saveTimer);
	}

	watch(activeKey, scheduleSave);

	return {
		available,
		openViews,
		dockedViews,
		floatingViews,
		activeKey,
		error,
		isOpen,
		load,
		open,
		close,
		activate,
		evict,
		synchronize,
		restore,
		dispose,
	};
}
