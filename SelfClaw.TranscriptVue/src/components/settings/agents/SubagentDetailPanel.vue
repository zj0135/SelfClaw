<script setup>
import { computed, reactive, ref, watch } from 'vue';
import {
	AlertTriangle,
	Network,
	Puzzle,
	Wrench,
	Workflow,
} from 'lucide-vue-next';
import CapabilityBindingCard from './CapabilityBindingCard.vue';
import BindingDialog from './BindingDialog.vue';
import SubagentBasicCapabilityDialog from './SubagentBasicCapabilityDialog.vue';

const props = defineProps({
	subagent: { type: Object, required: true },
	index: { type: String, default: '00' },
	plugins: { type: Array, default: () => [] },
	skills: { type: Array, default: () => [] },
	mcpServers: { type: Array, default: () => [] },
	saving: { type: Boolean, default: false },
	bindingPending: { type: Function, default: () => false },
});

const emit = defineEmits(['save', 'toggle-binding']);

const openSectionKey = ref('');
const showBasicDialog = ref(false);

watch(
	() => props.subagent.id,
	() => {
		openSectionKey.value = '';
	},
	{ immediate: true },
);

const includes = (list, id) => (list || []).some((candidate) => candidate.toLowerCase() === id.toLowerCase());

const pluginItems = computed(() =>
	props.plugins.map((plugin) => ({
		id: plugin.id,
		name: plugin.name,
		description: plugin.description,
		status: plugin.status,
		bound: includes(props.subagent.pluginIds, plugin.id),
	})),
);

const skillItems = computed(() =>
	props.skills.map((skill) => ({
		id: skill.id,
		name: skill.name,
		description: skill.description,
		status: skill.status,
		bound: skill.sourcePluginId
			? includes(props.subagent.pluginIds, skill.sourcePluginId)
			: includes(props.subagent.skillIds, skill.id),
		managedBy: skill.sourcePluginId || null,
	})),
);

const mcpItems = computed(() =>
	props.mcpServers.map((server) => ({
		id: server.id,
		name: server.name,
		description: server.description || `${server.transport} transport`,
		status: server.status,
		bound: server.sourcePluginId
			? includes(props.subagent.pluginIds, server.sourcePluginId)
			: includes(props.subagent.mcpServerIds, server.id),
		managedBy: server.sourcePluginId || null,
	})),
);

const sections = computed(() => [
	{
		key: 'basic',
		kicker: 'BASIC',
		title: '基本能力',
		hint: '配置子代理的基本信息和系统指令',
		icon: Workflow,
		items: [],
		isBasic: true,
	},
	{
		key: 'plugin',
		kicker: 'PLUGINS',
		title: '插件',
		hint: '仅全局已启用且状态可用的插件会在子代理回合中生效',
		icon: Puzzle,
		items: pluginItems.value,
		emptyText: '尚未安装任何插件，可前往「插件」设置页导入',
	},
	{
		key: 'skill',
		kicker: 'SKILLS',
		title: '技能',
		hint: '插件贡献的技能随插件绑定自动生效',
		icon: Wrench,
		items: skillItems.value,
		emptyText: '尚未安装任何技能',
	},
	{
		key: 'mcpServer',
		kicker: 'MCP SERVERS',
		title: 'MCP 服务器',
		hint: '插件托管的 MCP 服务器随插件绑定自动生效，不能独立绑定',
		icon: Network,
		items: mcpItems.value,
		emptyText: '尚未配置任何 MCP 服务器，可前往「插件」设置页添加',
	},
]);

const openSection = computed(
	() => sections.value.find((section) => section.key === openSectionKey.value) || null,
);

function dialogPending(id) {
	return openSection.value ? props.bindingPending(openSection.value.key, id) : false;
}

function onDialogToggle(item, enabled) {
	if (openSection.value) emit('toggle-binding', openSection.value.key, item.id, enabled);
}

function onSectionOpen(section) {
	if (section.key === 'basic') {
		showBasicDialog.value = true;
	} else {
		openSectionKey.value = section.key;
	}
}

function onBasicSave(data) {
	emit('save', data);
	showBasicDialog.value = false;
}
</script>

<template>
	<main class="detail">
		<span class="ghost-num" aria-hidden="true">{{ index }}</span>

		<header class="detail-head sc-rise" style="--i: 0">
			<div class="dh-icon" aria-hidden="true">
				<Workflow :size="26" :stroke-width="1.7" />
			</div>
			<div class="dh-meta">
				<div class="dh-kicker">SUBAGENT / {{ index }}</div>
				<div class="dh-title">
					<h2 :title="subagent.name">{{ subagent.name }}</h2>
				</div>
				<p :title="subagent.description || '暂无描述'">{{ subagent.description || '暂无描述' }}</p>
			</div>
			<span class="mode-badge">{{ subagent.toolPolicy }}</span>
			<span v-if="!subagent.isValid" class="invalid-badge">需修复</span>
		</header>

		<div class="detail-body">
			<div v-if="subagent.diagnostics?.length" class="notice warn sc-rise" style="--i: 1">
				<AlertTriangle :size="15" :stroke-width="2" aria-hidden="true" />
				<div>
					<strong>定义文件存在问题</strong>
					<p v-for="diagnostic in subagent.diagnostics" :key="diagnostic">{{ diagnostic }}</p>
				</div>
			</div>

			<CapabilityBindingCard class="sc-rise" style="--i: 2" :sections="sections"
				@open="onSectionOpen" />
		</div>

		<BindingDialog :open="Boolean(openSection)" :kicker="openSection?.kicker || ''"
			:title="openSection ? `绑定${openSection.title}` : ''" :hint="openSection?.hint || ''"
			:items="openSection?.items || []" :empty-text="openSection?.emptyText || ''" :pending="dialogPending"
			@close="openSectionKey = ''" @toggle="onDialogToggle" />

		<SubagentBasicCapabilityDialog :open="showBasicDialog" :subagent="subagent" :saving="saving"
			@close="showBasicDialog = false" @save="onBasicSave" />
	</main>
</template>

<style scoped>
@import '../../../styles/settings-console.css';
@import '../../../styles/agent-detail-shared.css';

.invalid-badge {
	flex: 0 0 auto;
	padding: 5px 10px;
	border: 1px solid color-mix(in srgb, var(--sc-err) 35%, transparent);
	border-radius: 99px;
	background: var(--sc-err-soft);
	color: var(--sc-err);
	font-size: var(--fs-11);
	font-weight: 600;
}
</style>
