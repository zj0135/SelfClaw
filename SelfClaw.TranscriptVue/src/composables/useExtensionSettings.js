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
		item.enabled = enabled;
		item.status = enabled ? (item.status === 'broken' ? 'broken' : 'ready') : 'disabled';
		state.value.revision = response.revision;
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
		item.unacknowledgedPermissions = [];
		state.value.revision = response.revision;
		return true;
	}

	async function deleteItem(kind, item) {
		const response = await mutate(kind, item.id, () =>
			request('extensions/delete', { kind, id: item.id }),
		);
		if (!response) return;
		const collection = kindCollections[kind];
		state.value[collection] = state.value[collection].filter((candidate) => candidate.id !== item.id);
		state.value.revision = response.revision;
	}

	async function setAgentBinding(kind, item, agentId, enabled) {
		const response = await mutate(kind, item.id, () =>
			request('extensions/set-agent-binding', {
				kind,
				id: item.id,
				agentId,
				enabled,
			}),
		);
		if (!response) return;
		await load();
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

	on('extensions/state-changed', (payload) => {
		if ((payload.revision || 0) >= state.value.revision) load();
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
		setAgentBinding,
		saveMcp,
		testMcp,
		importPackage,
	};
}
