import { onScopeDispose, reactive, readonly, watch } from 'vue';
import { isSuperseded } from './hostBridge.js';

const gitRequests = {
	refresh: 'get-git-state', 'create-branch': 'git-create-branch', 'switch-branch': 'git-switch-branch',
	'delete-branch': 'git-delete-branch', merge: 'git-merge', 'abort-merge': 'git-abort-merge',
};

export function useWorkspaceSelection(conversationId, bridge) {
	const state = reactive({ current: null, roots: [], commonFolders: [], isLoading: false, error: '', gitLoading: false, gitError: '' });
	let generation = 0;
	let gitGeneration = 0;
	let disposed = false;

	async function requestSelection(type, payload = {}, failureMessage = '工作区请求失败。') {
		if (disposed) return;
		const current = ++generation;
		gitGeneration++;
		state.isLoading = true;
		state.error = '';
		state.gitLoading = false;
		state.gitError = '';
		try {
			const response = await bridge.requestLatest('workspace-selection', type, payload);
			if (disposed || current !== generation) return;
			state.current = response.current || null;
			state.roots = Array.isArray(response.roots) ? response.roots : [];
			state.commonFolders = Array.isArray(response.commonFolders) ? response.commonFolders : [];
			state.error = response.error || '';
		} catch (error) {
			if (!disposed && current === generation && !isSuperseded(error)) state.error = error?.message || failureMessage;
		} finally {
			if (!disposed && current === generation) state.isLoading = false;
		}
	}

	function refresh(refresh = false) { return requestSelection('get-workspace-selection', { refresh: Boolean(refresh) }); }
	function selectRoot(workspaceRootId) {
		if (workspaceRootId) return requestSelection('select-workspace-root', { workspaceRootId });
	}
	function browseFolder() { return requestSelection('browse-workspace-folder'); }
	function deleteRoot(workspaceRootId) {
		if (workspaceRootId) return requestSelection('delete-workspace-root', { workspaceRootId }, '删除工作目录失败。');
	}

	async function runGitAction(action) {
		const type = gitRequests[action?.type];
		if (!type || disposed || state.gitLoading || state.isLoading || !state.current) return;
		const current = ++gitGeneration;
		const selection = generation;
		const isCurrent = () => !disposed && current === gitGeneration && selection === generation;
		state.gitLoading = true;
		state.gitError = '';
		try {
			const response = await bridge.request(type, { branchName: action.branchName, startPoint: action.startPoint });
			if (!isCurrent()) return;
			if (response?.state) {
				state.current = { ...state.current, git: response.state, branchName: response.state.branchName || '',
					isDirty: Boolean(response.state.isDirty), hasMergeConflicts: Boolean(response.state.hasMergeConflicts) };
			}
			if (action.type !== 'refresh') await refresh(true);
		} catch (error) {
			if (isCurrent()) state.gitError = error?.message || 'Git 操作失败。';
		} finally {
			if (isCurrent()) state.gitLoading = false;
		}
	}

	watch(conversationId, () => { state.current = null; refresh(); }, { immediate: true, flush: 'sync' });
	onScopeDispose(() => { disposed = true; generation++; gitGeneration++; });
	return { state: readonly(state), refresh, selectRoot, browseFolder, deleteRoot, runGitAction };
}
