<script setup>
import { computed, reactive, ref, watch } from 'vue';
import {
	AlertTriangle,
	Bot,
	Check,
	ChevronDown,
	LoaderCircle,
	Network,
	Puzzle,
	Wrench,
	Workflow,
} from 'lucide-vue-next';
import CapabilityBindingCard from './CapabilityBindingCard.vue';
import BindingDialog from './BindingDialog.vue';

const props = defineProps({
	agent: { type: Object, required: true },
	index: { type: String, default: '00' },
	plugins: { type: Array, default: () => [] },
	skills: { type: Array, default: () => [] },
	mcpServers: { type: Array, default: () => [] },
	subagents: { type: Array, default: () => [] },
	saving: { type: Boolean, default: false },
	bindingPending: { type: Function, default: () => false },
	allowancePending: { type: Function, default: () => false },
});

const emit = defineEmits(['save', 'toggle-binding', 'toggle-subagent']);

const form = reactive({ name: '', description: '', mode: 'direct', instructions: '' });
const openSectionKey = ref('');

watch(
	() => props.agent.id,
	() => {
		form.name = props.agent.name;
		form.description = props.agent.description;
		form.mode = props.agent.mode;
		form.instructions = props.agent.instructions;
		openSectionKey.value = '';
	},
	{ immediate: true },
);

const isDirty = computed(
	() =>
		form.name !== props.agent.name ||
		form.description !== props.agent.description ||
		form.mode !== props.agent.mode ||
		form.instructions !== props.agent.instructions,
);

const includes = (list, id) => (list || []).some((candidate) => candidate.toLowerCase() === id.toLowerCase());

// 插件贡献的技能 / 托管的 MCP 随插件绑定继承，不能独立绑定（与扩展页口径一致）。
const pluginItems = computed(() =>
	props.plugins.map((plugin) => ({
		id: plugin.id,
		name: plugin.name,
		description: plugin.description,
		status: plugin.status,
		bound: includes(props.agent.pluginIds, plugin.id),
	})),
);

const skillItems = computed(() =>
	props.skills.map((skill) => ({
		id: skill.id,
		name: skill.name,
		description: skill.description,
		status: skill.status,
		bound: skill.sourcePluginId
			? includes(props.agent.pluginIds, skill.sourcePluginId)
			: includes(props.agent.skillIds, skill.id),
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
			? includes(props.agent.pluginIds, server.sourcePluginId)
			: includes(props.agent.mcpServerIds, server.id),
		managedBy: server.sourcePluginId || null,
	})),
);

const subagentItems = computed(() =>
	props.subagents.map((subagent) => ({
		id: subagent.id,
		name: subagent.name,
		description: subagent.description,
		status: subagent.isValid ? 'ready' : 'broken',
		bound: includes(props.agent.subagentIds, subagent.id),
	})),
);

const sections = computed(() => [
	{
		key: 'plugin',
		kicker: 'PLUGINS',
		title: '插件',
		hint: '仅全局已启用且状态可用的插件会在 Direct 回合中生效',
		icon: Puzzle,
		items: pluginItems.value,
		emptyText: '尚未安装任何插件，可前往「插件」设置页导入',
	},
	{
		key: 'skill',
		kicker: 'SKILLS',
		title: '技能',
		hint: '插件贡献的技能随插件绑定自动生效，运行时可显式激活',
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
	{
		key: 'subagent',
		kicker: 'SUBAGENTS',
		title: '子代理白名单',
		hint: '绑定后，Direct 回合可通过 delegate_to_subagent 委派任务',
		icon: Workflow,
		items: subagentItems.value,
		emptyText: '暂无子代理定义，可在左侧切换到「子代理」面板查看',
	},
]);

const openSection = computed(
	() => sections.value.find((section) => section.key === openSectionKey.value) || null,
);

function dialogPending(id) {
	if (!openSection.value) return false;
	return openSection.value.key === 'subagent'
		? props.allowancePending(id)
		: props.bindingPending(openSection.value.key, id);
}

function onDialogToggle(item, enabled) {
	if (!openSection.value) return;
	if (openSection.value.key === 'subagent') {
		emit('toggle-subagent', item.id, enabled);
	} else {
		emit('toggle-binding', openSection.value.key, item.id, enabled);
	}
}

function submit() {
	if (!isDirty.value || props.saving) return;
	emit('save', {
		name: form.name.trim(),
		description: form.description.trim(),
		mode: form.mode,
		instructions: form.instructions,
	});
}
</script>

<template>
	<main class="detail">
		<span class="ghost-num" aria-hidden="true">{{ index }}</span>

		<header class="detail-head sc-rise" style="--i: 0">
			<div class="dh-icon" aria-hidden="true"><Bot :size="26" :stroke-width="1.7" /></div>
			<div class="dh-meta">
				<div class="dh-kicker">AGENT / {{ index }}</div>
				<div class="dh-title">
					<h2 :title="agent.name">{{ agent.name }}</h2>
				</div>
				<p :title="agent.description || '暂无描述'">{{ agent.description || '暂无描述' }}</p>
			</div>
			<span class="mode-badge" :class="agent.mode">{{ agent.mode === 'cli' ? 'CLI' : 'DIRECT' }}</span>
			<span v-if="agent.isBuiltIn" class="builtin-badge">内置</span>
		</header>

		<div class="detail-body">
			<div v-if="agent.warnings?.length" class="notice warn sc-rise" style="--i: 1">
				<AlertTriangle :size="15" :stroke-width="2" aria-hidden="true" />
				<div>
					<strong>定义文件存在告警</strong>
					<p v-for="warning in agent.warnings" :key="warning">{{ warning }}</p>
				</div>
			</div>

			<section class="card sc-rise" style="--i: 2">
				<div class="card-head">
					<div>
						<div class="card-kicker">PROFILE</div>
						<h3>基本信息</h3>
					</div>
					<button class="btn save-btn" type="button" :disabled="!isDirty || saving" @click="submit">
						<LoaderCircle v-if="saving" :size="14" :stroke-width="2.2" class="spin-ico" aria-hidden="true" />
						<Check v-else :size="14" :stroke-width="2.4" aria-hidden="true" />
						{{ saving ? '保存中…' : '保存' }}
					</button>
				</div>

				<div class="form-grid">
					<div class="field">
						<label class="fl" for="agent-name">名称</label>
						<input id="agent-name" v-model="form.name" class="input" type="text" maxlength="120" />
					</div>
					<div class="field">
						<label class="fl" for="agent-mode">运行模式</label>
						<div class="select">
							<select id="agent-mode" v-model="form.mode" aria-label="运行模式">
								<option value="direct">Direct — 进程内调用 AI 服务商</option>
								<option value="cli">CLI — 运行本地编程 CLI 子进程</option>
							</select>
							<ChevronDown :size="15" :stroke-width="2" class="chev" aria-hidden="true" />
						</div>
					</div>
					<div class="field span-2">
						<label class="fl" for="agent-desc">描述</label>
						<input
							id="agent-desc"
							v-model="form.description"
							class="input"
							type="text"
							placeholder="一句话说明该代理的职责"
						/>
					</div>
					<div class="field span-2">
						<label class="fl" for="agent-instructions">系统指令（Instructions）</label>
						<textarea
							id="agent-instructions"
							v-model="form.instructions"
							class="input mono instructions"
							rows="8"
							placeholder="写入该代理的系统提示，Direct 模式注入系统消息，CLI 模式作为附加系统提示"
						></textarea>
					</div>
				</div>
				<p v-if="form.mode !== agent.mode" class="field-hint">运行模式修改将在保存后生效。</p>
			</section>

			<CapabilityBindingCard
				v-if="agent.mode === 'direct'"
				class="sc-rise"
				style="--i: 3"
				:sections="sections"
				@open="(section) => (openSectionKey = section.key)"
			/>

			<div v-else class="notice info sc-rise" style="--i: 3">
				<Bot :size="15" :stroke-width="2" aria-hidden="true" />
				<div>
					<strong>CLI 模式</strong>
					<p>CLI 回合仅使用系统指令作为附加提示；插件、技能、MCP 与子代理由 CLI 子进程自身的配置与权限策略接管。</p>
				</div>
			</div>
		</div>

		<BindingDialog
			:open="Boolean(openSection)"
			:kicker="openSection?.kicker || ''"
			:title="openSection ? `绑定${openSection.title}` : ''"
			:hint="openSection?.hint || ''"
			:items="openSection?.items || []"
			:empty-text="openSection?.emptyText || ''"
			:pending="dialogPending"
			@close="openSectionKey = ''"
			@toggle="onDialogToggle"
		/>
	</main>
</template>

<style scoped>
@import '../../../styles/settings-console.css';
@import '../../../styles/agent-detail-shared.css';
</style>
