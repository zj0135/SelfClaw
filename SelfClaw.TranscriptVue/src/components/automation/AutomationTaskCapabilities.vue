<script setup>
import { computed, onMounted, ref } from 'vue';
import { ChevronDown, Sparkles } from 'lucide-vue-next';
import { isSuperseded, useHostBridge } from '../../composables/hostBridge';

const props = defineProps({
	form: { type: Object, required: true },
});

const { request } = useHostBridge();

const plugins = ref([]);
const skills = ref([]);
const mcpServers = ref([]);
const models = ref([]);
const workspaces = ref([]);
const agents = ref([]);
const expanded = ref(false);
const error = ref('');

const collections = computed(() => [
	{ key: 'pluginIds', label: '插件', items: plugins.value },
	{ key: 'skillIds', label: '技能', items: skills.value },
	{ key: 'mcpServerIds', label: 'MCP', items: mcpServers.value },
]);

function isSelected(key, id) {
	return (props.form[key] || []).includes(id);
}

function toggle(key, id) {
	const current = new Set(props.form[key] || []);
	if (current.has(id)) current.delete(id);
	else current.add(id);
	props.form[key] = [...current];
}

function applyPreset(agentId) {
	const agent = agents.value.find((item) => item.id === agentId);
	if (!agent) return;
	props.form.pluginIds = [...(agent.pluginIds || [])];
	props.form.skillIds = [...(agent.skillIds || [])];
	props.form.mcpServerIds = [...(agent.mcpServerIds || [])];
	if (agent.instructions && !props.form.instructions) props.form.instructions = agent.instructions;
}

onMounted(async () => {
	try {
		const [extensions, providers, workspace, agentState] = await Promise.all([
			request('extensions/get-state'),
			request('ai-providers/get-state'),
			request('get-workspace-selection', {}),
			request('agents/get-state'),
		]);
		const extensionState = extensions.state || {};
		plugins.value = (extensionState.plugins || []).map((item) => ({ id: item.id, name: item.name || item.id, enabled: item.enabled !== false }));
		skills.value = (extensionState.skills || []).map((item) => ({ id: item.id, name: item.name || item.id, enabled: item.enabled !== false }));
		mcpServers.value = (extensionState.mcpServers || []).map((item) => ({ id: item.id, name: item.name || item.id, enabled: item.enabled !== false }));
		models.value = (providers.state?.providers || [])
			.filter((provider) => provider.enabled)
			.flatMap((provider) => (provider.models || [])
				.filter((model) => model.enabled !== false)
				.map((model) => ({ id: model.modelProfileId, name: `${provider.name || provider.catalogId} · ${model.name || model.model}` })));
		workspaces.value = (workspace.roots || []).map((root) => ({ id: root.id, name: root.name || root.rootPath || root.id }));
		agents.value = agentState.state?.agents || [];
	} catch (cause) {
		if (!isSuperseded(cause)) error.value = cause?.message || '加载能力配置失败。';
	}
});
</script>

<template>
	<div class="ac-capabilities">
		<div class="ac-capability-row">
			<label class="ac-field">
				<span class="ac-field-label">模型</span>
				<select :value="form.modelProfileId || ''" @change="form.modelProfileId = $event.target.value || null">
					<option value="">默认模型</option>
					<option v-for="model in models" :key="model.id" :value="model.id">{{ model.name }}</option>
				</select>
			</label>
			<label class="ac-field">
				<span class="ac-field-label">工作目录</span>
				<select :value="form.workspaceRootId || ''" @change="form.workspaceRootId = $event.target.value || null">
					<option value="">无（不绑定）</option>
					<option v-for="root in workspaces" :key="root.id" :value="root.id">{{ root.name }}</option>
				</select>
			</label>
			<label class="ac-field">
				<span class="ac-field-label"><Sparkles class="ac-field-icon" aria-hidden="true" />代理预设</span>
				<select value="" @change="applyPreset($event.target.value)">
					<option value="">选择预设…</option>
					<option v-for="agent in agents" :key="agent.id" :value="agent.id">{{ agent.name }}</option>
				</select>
			</label>
		</div>

		<button type="button" class="ac-advanced-toggle" :aria-expanded="expanded" @click="expanded = !expanded">
			<ChevronDown class="ac-advanced-icon" :class="{ 'is-open': expanded }" aria-hidden="true" />
			<span>能力扩展绑定</span>
			<span class="ac-advanced-count">{{ (form.pluginIds?.length || 0) + (form.skillIds?.length || 0) + (form.mcpServerIds?.length || 0) }}</span>
		</button>

		<div v-if="expanded" class="ac-capability-collections">
			<div v-for="collection in collections" :key="collection.key" class="ac-collection">
				<span class="ac-field-label">{{ collection.label }}</span>
				<p v-if="!collection.items.length" class="ac-collection-empty">无可用项</p>
				<div v-else class="ac-chips">
					<button v-for="item in collection.items" :key="item.id" type="button" class="ac-chip"
						:class="{ 'is-on': isSelected(collection.key, item.id), 'is-disabled': !item.enabled }"
						:aria-pressed="isSelected(collection.key, item.id)" @click="toggle(collection.key, item.id)">
						{{ item.name }}
					</button>
				</div>
			</div>
		</div>

		<p v-if="error" class="ac-capability-error">{{ error }}</p>
	</div>
</template>

<style scoped>
.ac-capabilities {
	display: flex;
	flex-direction: column;
	gap: 12px;
}

.ac-capability-row {
	display: grid;
	grid-template-columns: repeat(auto-fit, minmax(180px, 1fr));
	gap: 10px;
}

.ac-field {
	display: flex;
	min-width: 0;
	flex-direction: column;
	gap: 5px;
}

.ac-field-label {
	display: inline-flex;
	align-items: center;
	gap: 5px;
	color: var(--muted);
	font-family: var(--font-code, monospace);
	font-size: var(--fs-10);
	font-weight: 650;
	letter-spacing: 0.08em;
	text-transform: uppercase;
}

.ac-field-icon {
	width: 12px;
	height: 12px;
	color: var(--accent);
}

.ac-field select {
	width: 100%;
	padding: 7px 9px;
	border: 1px solid var(--line-1);
	border-radius: 8px;
	background: var(--panel-soft);
	color: var(--text);
	font-size: var(--fs-12);
	cursor: pointer;
	transition: border-color 0.15s;
}

.ac-field select:focus {
	border-color: var(--accent);
	outline: none;
}

.ac-advanced-toggle {
	display: flex;
	align-items: center;
	gap: 7px;
	padding: 7px 10px;
	border: 1px dashed var(--line-2);
	border-radius: 9px;
	background: transparent;
	color: var(--muted);
	font-size: var(--fs-12);
	font-weight: 600;
	cursor: pointer;
	transition: border-color 0.15s, color 0.15s;
}

.ac-advanced-toggle:hover {
	border-color: var(--accent);
	color: var(--text);
}

.ac-advanced-icon {
	width: 13.5px;
	height: 13.5px;
	transition: transform 0.18s var(--ease-spring);
}

.ac-advanced-icon.is-open {
	transform: rotate(180deg);
}

.ac-advanced-count {
	padding: 0 6px;
	height: 16px;
	line-height: 16px;
	border-radius: 999px;
	background: var(--panel-muted);
	font-family: var(--font-code, monospace);
	font-size: 10px;
	color: var(--muted);
}

.ac-capability-collections {
	display: flex;
	flex-direction: column;
	gap: 10px;
	padding: 10px;
	border: 1px solid var(--line-1);
	border-radius: 10px;
	background: var(--panel-soft);
}

.ac-collection {
	display: flex;
	flex-direction: column;
	gap: 6px;
}

.ac-collection-empty {
	margin: 0;
	color: var(--faint);
	font-size: var(--fs-11);
}

.ac-chips {
	display: flex;
	flex-wrap: wrap;
	gap: 6px;
	max-height: 132px;
	overflow-y: auto;
}

.ac-chip {
	padding: 3px 9px;
	border: 1px solid var(--line-1);
	border-radius: 6px;
	background: var(--panel);
	color: var(--muted);
	font-size: var(--fs-11);
	font-weight: 550;
	cursor: pointer;
	transition: border-color 0.14s, color 0.14s, background 0.14s, transform 0.14s var(--ease-spring);
}

.ac-chip:hover:not(.is-on) {
	border-color: var(--line-2);
	color: var(--text);
}

.ac-chip:active {
	transform: scale(0.95);
}

.ac-chip.is-on {
	border-color: var(--accent);
	background: color-mix(in srgb, var(--accent) 15%, transparent);
	color: var(--accent-2);
	font-weight: 600;
}

.ac-chip.is-disabled {
	opacity: 0.45;
}

.ac-capability-error {
	margin: 0;
	color: var(--danger);
	font-size: var(--fs-11);
}
</style>
