import { computed, onMounted, ref } from 'vue';
import { isSuperseded, useHostBridge } from './hostBridge';

const emptyState = () => ({
	revision: 0,
	activeAgentId: null,
	agents: [],
	plugins: [],
	skills: [],
	mcpServers: [],
});

const kindCollections = {
	plugin: 'plugins',
	skill: 'skills',
	mcpServer: 'mcpServers',
};

export function useExtensionSettings() {
	const { request, requestLatest, on } = useHostBridge();
	const state = ref(emptyState());
	const loading = ref(false);
	const error = ref('');
	const pendingKeys = ref(new Set());

	const counts = computed(() => ({
		plugin: state.value.plugins.length,
		skill: state.value.skills.length,
		mcpServer: state.value.mcpServers.length,
	}));

	function setPending(key, value) {
		const next = new Set(pendingKeys.value);
		if (value) next.add(key);
		else next.delete(key);
		pendingKeys.value = next;
	}

	function isPending(kind, id) {
		return pendingKeys.value.has(kind + ':' + id);
	}

	async function load() {
		loading.value = true;
		try {
			const payload = await requestLatest('extensions/get-state', 'extensions/get-state');
			state.value = payload.state || emptyState();
			error.value = '';
		} catch (cause) {
			if (!isSuperseded(cause)) error.value = cause?.message || '无法加载扩展设置。';
		} finally {
			loading.value = false;
		}
	}

	async function mutate(kind, id, operation) {
		const key = kind + ':' + id;
		if (pendingKeys.value.has(key)) return null;
		setPending(key, true);
		error.value = '';
		try {
			return await operation();
		} catch (cause) {
			error.value = cause?.message || '扩展设置更新失败。';
			return null;
		} finally {
			setPending(key, false);
		}
	}

	async function setEnabled(kind, item, enabled) {
		const response = await mutate(kind, item.id, () =>
			request('extensions/set-enabled', { kind, id: item.id, enabled }),
		);
		if (!response) return false;
		// set-enabled 只回 { ok, revision }：status 与未确认权限由 catalog 判定（可能是
		// broken / needs-permission），本地猜 ready 会与能力解析结果不一致。
		await load();
		return true;
	}

	async function acknowledgePluginPermissions(item) {
		const response = await mutate('plugin', item.id, () =>
			request('extensions/acknowledge-plugin-permissions', {
				id: item.id,
				permissions: item.permissions || [],
			}),
		);
		if (!response) return false;
		await load();
		return true;
	}

	async function deleteItem(kind, item) {
		const response = await mutate(kind, item.id, () =>
			request('extensions/delete', { kind, id: item.id }),
		);
		if (!response) return false;
		const collection = kindCollections[kind];
		state.value[collection] = state.value[collection].filter((candidate) => candidate.id !== item.id);
		state.value.revision = response.revision;
		return true;
	}

	async function saveMcp(command) {
		const id = command.id || 'new';
		const response = await mutate('mcpServer', id, () =>
			request('extensions/save-mcp', command),
		);
		if (!response) return null;
		const index = state.value.mcpServers.findIndex((server) => server.id === response.server.id);
		if (index >= 0) state.value.mcpServers[index] = response.server;
		else state.value.mcpServers.push(response.server);
		state.value.revision = response.revision;
		return response.server;
	}

	async function testMcp(id) {
		const response = await mutate('mcpServer', id, () =>
			request('extensions/test-mcp', { id }, { timeout: 0 }),
		);
		if (!response) return null;
		await load();
		return response.result;
	}

	async function importPackage(kind) {
		const response = await mutate(kind, 'import', () =>
			request('extensions/import-package', { kind }, { timeout: 0 }),
		);
		if (!response || response.cancelled) return null;
		await load();
		return response;
	}

	// 只有更新的 revision 才值得重拉：mutation 自身已经 load() 过，宿主随后推送的同号
	// state-changed 若也触发一次，等于每次改动都打两个来回。
	on('extensions/state-changed', (payload) => {
		if ((payload.revision || 0) > state.value.revision) load();
	});

	onMounted(load);

	return {
		state,
		counts,
		loading,
		error,
		isPending,
		load,
		setEnabled,
		acknowledgePluginPermissions,
		deleteItem,
		saveMcp,
		testMcp,
		importPackage,
	};
}
