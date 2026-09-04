import { onMounted, ref } from 'vue';
import { isSuperseded, useHostBridge } from './hostBridge';

const emptyState = () => ({
	revision: 0,
	agents: [],
	subagents: [],
	plugins: [],
	skills: [],
	mcpServers: [],
});

const agentSaveKey = (agentId) => `agent:${agentId}`;
const agentBindingKey = (agentId, kind, id) => `agent:${agentId}:${kind}:${id}`;
const agentSubagentKey = (agentId, subagentId) => `agent:${agentId}:subagent:${subagentId}`;
const subagentSaveKey = (subagentId) => `subagent:${subagentId}`;
const subagentBindingKey = (subagentId, kind, id) => `subagent:${subagentId}:${kind}:${id}`;

export function useAgentSettings() {
	const { request, requestLatest, on } = useHostBridge();
	const state = ref(emptyState());
	const loading = ref(false);
	const error = ref('');
	const pendingKeys = ref(new Set());

	function setPending(key, value) {
		const next = new Set(pendingKeys.value);
		if (value) next.add(key);
		else next.delete(key);
		pendingKeys.value = next;
	}

	const isAgentSaving = (agentId) => pendingKeys.value.has(agentSaveKey(agentId));
	const isAgentBindingPending = (agentId, kind, id) => pendingKeys.value.has(agentBindingKey(agentId, kind, id));
	const isSubagentAllowancePending = (agentId, subagentId) => pendingKeys.value.has(agentSubagentKey(agentId, subagentId));
	const isSubagentSaving = (subagentId) => pendingKeys.value.has(subagentSaveKey(subagentId));
	const isSubagentBindingPending = (subagentId, kind, id) => pendingKeys.value.has(subagentBindingKey(subagentId, kind, id));

	async function load() {
		loading.value = true;
		try {
			const payload = await requestLatest('agents/get-state', 'agents/get-state');
			state.value = payload.state || emptyState();
			error.value = '';
		} catch (cause) {
			if (!isSuperseded(cause)) error.value = cause?.message || '无法加载代理助手设置。';
		} finally {
			loading.value = false;
		}
	}

	async function mutate(key, operation) {
		if (pendingKeys.value.has(key)) return null;
		setPending(key, true);
		error.value = '';
		try {
			return await operation();
		} catch (cause) {
			error.value = cause?.message || '代理助手设置更新失败。';
			return null;
		} finally {
			setPending(key, false);
		}
	}

	function applyAgent(agent, revision) {
		const index = state.value.agents.findIndex((candidate) => candidate.id === agent.id);
		if (index >= 0) {
			state.value.agents[index] = agent;
		} else {
			// 新增的代理，添加到列表中
			state.value.agents.push(agent);
		}
		state.value.revision = revision;
	}

	function applySubagent(subagent, revision) {
		const index = state.value.subagents.findIndex((candidate) => candidate.id === subagent.id);
		if (index >= 0) {
			state.value.subagents[index] = subagent;
		} else {
			// 新增的子代理，添加到列表中
			state.value.subagents.push(subagent);
		}
		state.value.revision = revision;
	}

	async function createAgent(form) {
		const response = await mutate('agent:create', () => request('agents/create-agent', form));
		if (!response) return false;
		applyAgent(response.agent, response.revision);
		return response.agent;
	}

	async function createSubagent(form) {
		const response = await mutate('subagent:create', () => request('agents/create-subagent', form));
		if (!response) return false;
		applySubagent(response.subagent, response.revision);
		return response.subagent;
	}

	async function saveAgent(agentId, form) {
		const response = await mutate(agentSaveKey(agentId), () => request('agents/save-agent', { id: agentId, ...form }));
		if (!response) return false;
		applyAgent(response.agent, response.revision);
		return true;
	}

	async function setAgentBinding(agentId, kind, id, enabled) {
		const response = await mutate(agentBindingKey(agentId, kind, id), () => request('agents/set-binding', { agentId, kind, id, enabled }));
		if (!response) return false;
		applyAgent(response.agent, response.revision);
		return true;
	}

	async function setSubagentAllowance(agentId, subagentId, enabled) {
		const response = await mutate(agentSubagentKey(agentId, subagentId), () =>
			request('agents/set-subagent-binding', { agentId, subagentId, enabled })
		);
		if (!response) return false;
		applyAgent(response.agent, response.revision);
		return true;
	}

	async function saveSubagent(subagentId, form) {
		const response = await mutate(subagentSaveKey(subagentId), () => request('agents/save-subagent', { id: subagentId, ...form }));
		if (!response) return false;
		applySubagent(response.subagent, response.revision);
		return true;
	}

	async function setSubagentBinding(subagentId, kind, id, enabled) {
		const response = await mutate(subagentBindingKey(subagentId, kind, id), () =>
			request('agents/set-subagent-extension-binding', { subagentId, kind, id, enabled })
		);
		if (!response) return false;
		applySubagent(response.subagent, response.revision);
		return true;
	}

	async function deleteAgent(agentId) {
		const response = await mutate(`agent:delete:${agentId}`, () => request('agents/delete-agent', { id: agentId }));
		if (!response) return false;
		// 从列表中移除该代理
		state.value.agents = state.value.agents.filter((agent) => agent.id !== agentId);
		state.value.revision = response.revision;
		return true;
	}

	// 复用扩展状态 revision：Agent 绑定变更与扩展页共享同一条刷新通道。
	on('extensions/state-changed', (payload) => {
		if ((payload.revision || 0) > state.value.revision) load();
	});

	onMounted(load);

	return {
		state,
		loading,
		error,
		isAgentSaving,
		isAgentBindingPending,
		isSubagentAllowancePending,
		isSubagentSaving,
		isSubagentBindingPending,
		load,
		createAgent,
		createSubagent,
		saveAgent,
		deleteAgent,
		setAgentBinding,
		setSubagentAllowance,
		saveSubagent,
		setSubagentBinding,
	};
}
