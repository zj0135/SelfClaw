import { computed, onScopeDispose, reactive, readonly, watch } from 'vue';
import { isSuperseded } from './hostBridge.js';

const EMPTY_SET = Object.freeze({ files: [], atFileLimit: false });

// 审查面板的数据层：改动列表 + 选中文件的 patch，全部走 request / response（latest-wins），没有宿主推送。
// 一次刷新拿回两个作用域（宿主一趟 status v2 就能分出索引 / 工作树两侧），切换下拉不再请求；文件 patch 按需拉。
// 刷新时机：面板变为可见、手动刷新、可见时 workspace 根变化；隐藏时不发请求，再可见时整体刷新。
export function useGitDiff(workspaceRootId, visible, bridge) {
	const state = reactive({
		scope: 'unstaged',
		loading: false,
		error: '',
		loadedOnce: false,
		workspacePath: '',
		repositoryRootPath: '',
		branchName: '',
		isGitAvailable: true,
		isRepository: false,
		unstaged: EMPTY_SET,
		staged: EMPTY_SET,
		selectedPath: null,
		file: null,
		fileLoading: false,
		fileError: '',
	});
	let generation = 0;
	let fileGeneration = 0;
	let disposed = false;

	const files = computed(() => (state.scope === 'staged' ? state.staged : state.unstaged).files);
	const selectedIndex = computed(() => files.value.findIndex((file) => file.path === state.selectedPath));
	const hasPrevious = computed(() => selectedIndex.value > 0);
	const hasNext = computed(() => (selectedIndex.value < 0 ? files.value.length > 0 : selectedIndex.value < files.value.length - 1));

	function normalizeSet(set) {
		return { files: Array.isArray(set?.files) ? set.files : [], atFileLimit: Boolean(set?.atFileLimit) };
	}

	function clearFile() {
		fileGeneration++;
		state.selectedPath = null;
		state.file = null;
		state.fileLoading = false;
		state.fileError = '';
	}

	function resetWithoutWorkspace() {
		state.loadedOnce = false;
		state.error = '';
		state.loading = false;
		state.unstaged = EMPTY_SET;
		state.staged = EMPTY_SET;
		clearFile();
	}

	async function refresh() {
		if (disposed) return;
		const current = ++generation;
		if (!workspaceRootId.value) {
			resetWithoutWorkspace();
			return;
		}
		state.loading = true;
		state.error = '';
		try {
			const response = await bridge.requestLatest('git-diff/changes', 'git-diff/changes');
			if (disposed || current !== generation) return;
			state.workspacePath = response.workspacePath || '';
			state.repositoryRootPath = response.repositoryRootPath || '';
			state.branchName = response.branchName || '';
			state.isGitAvailable = response.isGitAvailable !== false;
			state.isRepository = Boolean(response.isRepository);
			state.unstaged = normalizeSet(response.unstaged);
			state.staged = normalizeSet(response.staged);
			state.loadedOnce = true;
			ensureSelection();
		} catch (error) {
			if (!disposed && current === generation && !isSuperseded(error)) state.error = error?.message || '读取 Git 改动失败。';
		} finally {
			if (!disposed && current === generation) state.loading = false;
		}
	}

	// 面板不该空着：选中项仍在当前作用域里就保留（内容可能变了，重新拉），否则选第一项；没有文件就清空详情。
	function ensureSelection() {
		const target = files.value.find((file) => file.path === state.selectedPath) ?? files.value[0] ?? null;
		if (!target) {
			clearFile();
			return;
		}
		selectFile(target);
	}

	function setScope(scope) {
		if ((scope !== 'staged' && scope !== 'unstaged') || scope === state.scope) return;
		state.scope = scope;
		ensureSelection();
	}

	async function selectFile(file) {
		if (disposed || !file?.path) return;
		const current = ++fileGeneration;
		state.selectedPath = file.path;
		state.file = null;
		state.fileLoading = true;
		state.fileError = '';
		try {
			const response = await bridge.requestLatest('git-diff/file', 'git-diff/file', {
				scope: state.scope,
				path: file.path,
				oldPath: file.oldPath ?? null,
				kind: file.kind,
			});
			if (disposed || current !== fileGeneration) return;
			state.file = {
				path: response.path || file.path,
				oldPath: response.oldPath ?? null,
				kind: response.kind || file.kind,
				isBinary: Boolean(response.isBinary),
				patch: response.patch || '',
				truncated: Boolean(response.truncated),
				addedLines: response.addedLines ?? 0,
				removedLines: response.removedLines ?? 0,
			};
		} catch (error) {
			if (!disposed && current === fileGeneration && !isSuperseded(error)) state.fileError = error?.message || '读取文件改动失败。';
		} finally {
			if (!disposed && current === fileGeneration) state.fileLoading = false;
		}
	}

	// 上一个 / 下一个：工具条按钮与文件列表的 ↑/↓ 都走这里；没有选中时从第一项开始。
	function selectNeighbor(step) {
		const list = files.value;
		if (!list.length) return;
		const target = selectedIndex.value < 0 ? list[0] : list[selectedIndex.value + step];
		if (target) selectFile(target);
	}

	watch(visible, (isVisible) => { if (isVisible) refresh(); }, { immediate: true });
	watch(workspaceRootId, () => {
		clearFile();
		if (visible.value) refresh();
	});
	onScopeDispose(() => { disposed = true; generation++; fileGeneration++; });

	return { state: readonly(state), files, hasPrevious, hasNext, refresh, setScope, selectFile, selectNeighbor };
}
