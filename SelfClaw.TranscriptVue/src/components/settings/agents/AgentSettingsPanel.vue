<script setup>
import { computed, reactive, ref, watchEffect } from 'vue';
import { AlertCircle } from 'lucide-vue-next';
import { useAgentSettings } from '../../../composables/useAgentSettings.js';
import { useToast } from '../../../composables/useToast.js';
import AgentListColumn from './AgentListColumn.vue';
import AgentDetailPanel from './AgentDetailPanel.vue';
import SubagentDetailPanel from './SubagentDetailPanel.vue';

const {
	state,
	loading,
	error,
	isAgentSaving,
	isAgentBindingPending,
	isSubagentAllowancePending,
	isSubagentSaving,
	isSubagentBindingPending,
	saveAgent,
	setAgentBinding,
	setSubagentAllowance,
	saveSubagent,
	setSubagentBinding,
} = useAgentSettings();
const { showToast } = useToast();

const activeKind = ref('agent');
// 每个页签各自记住选中项，来回切换不丢上下文。
const selection = reactive({ agent: '', subagent: '' });

const selectedId = computed(() => selection[activeKind.value]);

watchEffect(() => {
	const list = activeKind.value === 'agent' ? state.value.agents : state.value.subagents;
	if (!list.length) return;
	if (!list.some((item) => item.id === selection[activeKind.value])) {
		selection[activeKind.value] = list[0].id;
	}
});

const activeAgent = computed(
	() => state.value.agents.find((agent) => agent.id === selection.agent) || null,
);
const activeSubagent = computed(
	() => state.value.subagents.find((subagent) => subagent.id === selection.subagent) || null,
);

const activeIndex = computed(() => {
	const list = activeKind.value === 'agent' ? state.value.agents : state.value.subagents;
	const active = activeKind.value === 'agent' ? activeAgent.value : activeSubagent.value;
	const index = active ? list.indexOf(active) : -1;
	return index >= 0 ? String(index + 1).padStart(2, '00') : '00';
});

const agentBindingPending = (kind, id) =>
	Boolean(activeAgent.value) && isAgentBindingPending(activeAgent.value.id, kind, id);
const allowancePending = (subagentId) =>
	Boolean(activeAgent.value) && isSubagentAllowancePending(activeAgent.value.id, subagentId);
const subagentBindingPending = (kind, id) =>
	Boolean(activeSubagent.value) && isSubagentBindingPending(activeSubagent.value.id, kind, id);

function onSelect(id) {
	selection[activeKind.value] = id;
}

async function onSaveAgent(form) {
	if (await saveAgent(selection.agent, form)) showToast('代理设置已保存');
}

async function onToggleAgentBinding(kind, id, enabled) {
	await setAgentBinding(selection.agent, kind, id, enabled);
}

async function onToggleSubagentAllowance(subagentId, enabled) {
	await setSubagentAllowance(selection.agent, subagentId, enabled);
}

async function onSaveSubagent(form) {
	if (await saveSubagent(selection.subagent, form)) showToast('子代理设置已保存');
}

async function onToggleSubagentBinding(kind, id, enabled) {
	await setSubagentBinding(selection.subagent, kind, id, enabled);
}
</script>

<template>
	<div class="agent-settings sc-root sc-stage">
		<AgentListColumn v-model:active-kind="activeKind" :agents="state.agents" :subagents="state.subagents"
			:selected-id="selectedId" :loading="loading" @select="onSelect" />

		<div class="detail-wrap">
			<div v-if="error" class="error-bar">
				<AlertCircle :size="15" :stroke-width="2" aria-hidden="true" />
				<span>{{ error }}</span>
			</div>

			<AgentDetailPanel v-if="activeKind === 'agent' && activeAgent" :agent="activeAgent" :index="activeIndex"
				:plugins="state.plugins" :skills="state.skills" :mcp-servers="state.mcpServers"
				:subagents="state.subagents" :saving="isAgentSaving(activeAgent.id)"
				:binding-pending="agentBindingPending" :allowance-pending="allowancePending" @save="onSaveAgent"
				@toggle-binding="onToggleAgentBinding" @toggle-subagent="onToggleSubagentAllowance" />
			<SubagentDetailPanel v-else-if="activeKind === 'subagent' && activeSubagent" :subagent="activeSubagent"
				:index="activeIndex" :plugins="state.plugins" :skills="state.skills" :mcp-servers="state.mcpServers"
				:saving="isSubagentSaving(activeSubagent.id)" :binding-pending="subagentBindingPending"
				@save="onSaveSubagent" @toggle-binding="onToggleSubagentBinding" />
			<div v-else class="detail-empty">
				{{ loading ? '正在加载定义…' : '从左侧选择一个定义查看配置' }}
			</div>
		</div>
	</div>
</template>

<style scoped>
@import '../../../styles/settings-console.css';

.agent-settings {
	position: relative;
	display: grid;
	grid-template-columns: 300px minmax(0, 1fr);
	/* 隐式 auto 行会按内容撑高并被 overflow 裁掉；显式 1fr 行约束到容器高度，内部才可滚动 */
	grid-template-rows: minmax(0, 1fr);
	width: 100%;
	height: 100%;
	min-height: 0;
	overflow: hidden;
	color: var(--sc-text);
	font-family: var(--sc-sans);
	font-size: 14px;
	line-height: 1.5;
}

.agent-settings * {
	box-sizing: border-box;
}

.detail-wrap {
	display: flex;
	min-width: 0;
	min-height: 0;
	flex-direction: column;
	overflow: hidden;
}

.error-bar {
	display: flex;
	align-items: center;
	gap: 8px;
	padding: 10px 34px;
	border-bottom: 1px solid color-mix(in srgb, var(--sc-err) 25%, transparent);
	background: var(--sc-err-soft);
	color: var(--sc-err);
	font-size: 12.5px;
}

.detail-empty {
	display: grid;
	place-items: center;
	height: 100%;
	color: var(--sc-mute);
	font-size: 13px;
}

@media (max-width: 980px) {
	.agent-settings {
		grid-template-columns: 260px minmax(0, 1fr);
	}
}

@media (max-width: 760px) {
	.agent-settings {
		grid-template-columns: 1fr;
		grid-template-rows: auto auto;
		overflow-y: auto;
	}
}
</style>
