import { onMounted, ref } from 'vue';
import { isSuperseded, useHostBridge } from './hostBridge.js';

// Direct 工作区命令工具（run_shell_command）的 shell 选择链路：读 → 单选即存 → 以宿主回显为真值。
// 归一化（非法值回默认）只发生在宿主，前端把回显当唯一真值；可用性（pwsh / Git Bash 是否
// 安装）同样由宿主判定，前端只负责把不可用项禁用。

const STATE_TYPE = 'workspace-tools/state';
const GET_TYPE = 'workspace-tools/get';
const SAVE_TYPE = 'workspace-tools/save';

const SHELL_KINDS = ['WindowsPowerShell', 'PowerShellSeven', 'CommandPrompt', 'GitBash'];
const DEFAULT_SHELL_KIND = 'WindowsPowerShell';

function defaultState() {
	return {
		shellKind: DEFAULT_SHELL_KIND,
		shells: SHELL_KINDS.map((kind) => ({
			kind,
			available: kind === DEFAULT_SHELL_KIND,
			resolvedPath: '',
		})),
	};
}

function stateFrom(raw) {
	const source = raw && typeof raw === 'object' ? raw : {};
	const byKind = new Map(
		(Array.isArray(source.shells) ? source.shells : []).map((shell) => [shell?.kind, shell]),
	);
	return {
		shellKind: SHELL_KINDS.includes(source.shellKind) ? source.shellKind : DEFAULT_SHELL_KIND,
		shells: SHELL_KINDS.map((kind) => {
			const shell = byKind.get(kind) || {};
			return {
				kind,
				available: shell.available === true,
				resolvedPath: typeof shell.resolvedPath === 'string' ? shell.resolvedPath : '',
			};
		}),
	};
}

function errorMessage(failure, fallback) {
	const text = typeof failure?.message === 'string' ? failure.message.trim() : '';
	return text || fallback;
}

export function useWorkspaceTools() {
	const { requestLatest, on } = useHostBridge();
	const state = ref(defaultState());
	const loading = ref(true);
	const saving = ref(false);
	const error = ref('');

	function applyState(raw) {
		state.value = stateFrom(raw);
	}

	function isAvailable(kind) {
		return state.value.shells.some((shell) => shell.kind === kind && shell.available);
	}

	async function load() {
		loading.value = true;
		try {
			applyState(await requestLatest('workspace-tools', GET_TYPE));
			error.value = '';
		} catch (failure) {
			if (!isSuperseded(failure)) error.value = errorMessage(failure, '加载命令执行工具设置失败');
		} finally {
			loading.value = false;
		}
	}

	async function selectShell(kind) {
		if (kind === state.value.shellKind || !isAvailable(kind)) return;
		saving.value = true;
		try {
			applyState(await requestLatest(SAVE_TYPE, SAVE_TYPE, { shellKind: kind }));
			error.value = '';
		} catch (failure) {
			if (!isSuperseded(failure)) error.value = errorMessage(failure, '保存命令执行工具设置失败');
		} finally {
			saving.value = false;
		}
	}

	on(STATE_TYPE, (payload) => applyState(payload));

	onMounted(() => {
		void load();
	});

	return { state, loading, saving, error, selectShell };
}
