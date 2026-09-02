import { computed, ref } from 'vue';
import { useHostBridge, isSuperseded } from './hostBridge.js';

// 侧栏「工作目录」视图的状态与取数。
//
// 目录按层懒加载：打开时只取根一层，展开某个目录时才取它的子项，收起不丢弃已取到的结果。
// 宿主按 workspaceRootId 解析真实路径（前端拿不到、也不需要拼路径），隐藏文件在宿主侧过滤。
export function useWorkspaceTree() {
	const { request } = useHostBridge();

	// null 表示侧栏中区仍显示会话列表。
	const root = ref(null);
	// relativePath（'' 为根）-> 该层的条目数组
	const entries = ref(new Map());
	const expanded = ref(new Set());
	const loading = ref(new Set());
	const errors = ref(new Map());
	// 取到宿主单层上限的目录：relativePath -> 上限值。宿主无法区分「正好这么多」与
	// 「被截断」，所以只如实说明上限，不断言后面还有多少。
	const atLimit = ref(new Map());

	const isOpen = computed(() => root.value !== null);

	function clear() {
		entries.value.clear();
		expanded.value.clear();
		loading.value.clear();
		errors.value.clear();
		atLimit.value.clear();
	}

	function open(target) {
		if (!target?.workspaceRootId) {
			return;
		}

		clear();
		root.value = {
			workspaceRootId: target.workspaceRootId,
			name: target.name || '工作目录',
			path: target.path || '',
		};
		return load('');
	}

	function close() {
		root.value = null;
		clear();
	}

	async function load(relativePath) {
		const workspaceRootId = root.value?.workspaceRootId;
		if (!workspaceRootId) {
			return;
		}

		loading.value.add(relativePath);
		errors.value.delete(relativePath);
		try {
			const response = await request('workspace-tree/list', { workspaceRootId, relativePath });
			// 等待期间可能已切回会话列表或换了工作目录，回包一律丢弃。
			if (root.value?.workspaceRootId !== workspaceRootId) {
				return;
			}

			if (!response?.ok) {
				throw new Error(response?.error || '读取目录失败。');
			}

			entries.value.set(relativePath, Array.isArray(response.entries) ? response.entries : []);
			if (response.atEntryLimit) {
				atLimit.value.set(relativePath, response.entryLimit || 0);
			} else {
				atLimit.value.delete(relativePath);
			}

			// 名称与路径以宿主为准（工作区里改过名就跟着更新），但只在确实变了时才写回。
			const name = response.rootName || root.value.name;
			const path = response.rootPath || root.value.path;
			if (name !== root.value.name || path !== root.value.path) {
				root.value = { workspaceRootId, name, path };
			}
		} catch (error) {
			if (isSuperseded(error) || root.value?.workspaceRootId !== workspaceRootId) {
				return;
			}

			errors.value.set(relativePath, error?.message || '读取目录失败。');
		} finally {
			if (root.value?.workspaceRootId === workspaceRootId) {
				loading.value.delete(relativePath);
			}
		}
	}

	function toggle(relativePath) {
		if (expanded.value.has(relativePath)) {
			expanded.value.delete(relativePath);
			return;
		}

		expanded.value.add(relativePath);
		// 已取过的层不再请求；失败过的层允许再试一次。
		if (!entries.value.has(relativePath) || errors.value.has(relativePath)) {
			return load(relativePath);
		}
	}

	// 把「根 + 已展开目录」摊平成带缩进层级的行，模板里一次 v-for 就能渲染整棵树。
	const rows = computed(() => {
		if (!isOpen.value) {
			return [];
		}

		const result = [];
		const walk = (relativePath, depth) => {
			const level = entries.value.get(relativePath) || [];
			for (const entry of level) {
				const isExpanded = entry.isDirectory && expanded.value.has(entry.relativePath);
				result.push({
					key: entry.relativePath,
					kind: 'entry',
					name: entry.name,
					relativePath: entry.relativePath,
					isDirectory: Boolean(entry.isDirectory),
					sizeBytes: entry.sizeBytes ?? null,
					depth,
					expanded: isExpanded,
					loading: loading.value.has(entry.relativePath),
					error: errors.value.get(entry.relativePath) || '',
				});

				if (isExpanded) {
					walk(entry.relativePath, depth + 1);
				}
			}

			if (atLimit.value.has(relativePath)) {
				result.push({
					key: `${relativePath}::at-limit`,
					kind: 'at-limit',
					limit: atLimit.value.get(relativePath) || level.length,
					depth,
				});
			}
		};

		walk('', 0);
		return result;
	});

	const rootLoading = computed(() => loading.value.has(''));
	const rootError = computed(() => errors.value.get('') || '');
	const rootLoaded = computed(() => entries.value.has(''));

	return { root, rows, isOpen, rootLoading, rootError, rootLoaded, open, close, toggle, reload: () => load('') };
}
