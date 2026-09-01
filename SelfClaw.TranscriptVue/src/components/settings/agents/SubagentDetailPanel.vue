<script setup>
import { computed, onMounted, reactive, ref, watch } from 'vue';
import {
	AlertTriangle,
	Check,
	ChevronDown,
	LoaderCircle,
	Network,
	Puzzle,
	Wrench,
	Workflow,
} from 'lucide-vue-next';
import { useHostBridge } from '../../../composables/hostBridge.js';
import CapabilityBindingCard from './CapabilityBindingCard.vue';
import BindingDialog from './BindingDialog.vue';

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

const { request } = useHostBridge();

const form = reactive({
	name: '',
	description: '',
	modelProfileId: '',
	toolPolicy: 'read-only',
	maxRunSeconds: 900,
	instructions: '',
});
const openSectionKey = ref('');

watch(
	() => props.subagent.id,
	() => {
		form.name = props.subagent.name;
		form.description = props.subagent.description;
		form.modelProfileId = props.subagent.modelProfileId || '';
		form.toolPolicy = props.subagent.toolPolicy;
		form.maxRunSeconds = props.subagent.maxRunSeconds;
		form.instructions = props.subagent.instructions;
		openSectionKey.value = '';
	},
	{ immediate: true },
);

const enabledModels = ref([]);

onMounted(async () => {
	try {
		const payload = await request('ai-providers/list-enabled-models');
		enabledModels.value = payload.models || [];
	} catch {
		enabledModels.value = [];
	}
});

const isDirty = computed(
	() =>
		form.name !== props.subagent.name ||
		form.description !== props.subagent.description ||
		form.modelProfileId !== (props.subagent.modelProfileId || '') ||
		form.toolPolicy !== props.subagent.toolPolicy ||
		Number(form.maxRunSeconds) !== props.subagent.maxRunSeconds ||
		form.instructions !== props.subagent.instructions,
);

const maxRunValid = computed(() => {
	const value = Number(form.maxRunSeconds);
	return Number.isInteger(value) && value >= 30 && value <= 3600;
});

const canSubmit = computed(
	() =>
		isDirty.value &&
		!props.saving &&
		form.name.trim().length > 0 &&
		form.description.trim().length > 0 &&
		form.instructions.trim().length > 0 &&
		maxRunValid.value,
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

function submit() {
	if (!canSubmit.value) return;
	emit('save', {
		name: form.name.trim(),
		description: form.description.trim(),
		modelProfileId: form.modelProfileId || null,
		toolPolicy: form.toolPolicy,
		maxRunSeconds: Number(form.maxRunSeconds),
		instructions: form.instructions,
	});
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

			<section class="card sc-rise" style="--i: 2">
				<div class="card-head">
					<div>
						<div class="card-kicker">PROFILE</div>
						<h3>基本信息</h3>
					</div>
					<button class="btn save-btn" type="button" :disabled="!canSubmit" @click="submit">
						<LoaderCircle v-if="saving" :size="14" :stroke-width="2.2" class="spin-ico"
							aria-hidden="true" />
						<Check v-else :size="14" :stroke-width="2.4" aria-hidden="true" />
						{{ saving ? '保存中…' : '保存' }}
					</button>
				</div>

				<div class="form-grid">
					<div class="field">
						<label class="fl" for="subagent-name">名称</label>
						<input id="subagent-name" v-model="form.name" class="input" type="text" maxlength="120" />
					</div>
					<div class="field">
						<label class="fl" for="subagent-model">模型配置</label>
						<div class="select">
							<select id="subagent-model" v-model="form.modelProfileId" aria-label="模型配置">
								<option value="">继承父级 Agent 的模型</option>
								<option v-for="model in enabledModels" :key="model.modelProfileId"
									:value="model.modelProfileId">
									{{ model.name }} · {{ model.providerName }}
								</option>
							</select>
							<ChevronDown :size="15" :stroke-width="2" class="chev" aria-hidden="true" />
						</div>
					</div>
					<div class="field span-2">
						<label class="fl" for="subagent-desc">描述</label>
						<input id="subagent-desc" v-model="form.description" class="input" type="text"
							placeholder="一句话说明该子代理的职责" />
					</div>
					<div class="field">
						<label class="fl" for="subagent-tools">工具策略</label>
						<div class="select">
							<select id="subagent-tools" v-model="form.toolPolicy" aria-label="工具策略">
								<option value="none">none — 不注入工作区工具</option>
								<option value="read-only">read-only — 仅只读工具</option>
								<option value="system">system — 完整系统工具</option>
							</select>
							<ChevronDown :size="15" :stroke-width="2" class="chev" aria-hidden="true" />
						</div>
					</div>
					<div class="field">
						<label class="fl" for="subagent-max-run">最长运行时间（秒）</label>
						<input id="subagent-max-run" v-model="form.maxRunSeconds" class="input mono" type="number"
							min="30" max="3600" step="30" />
						<p v-if="!maxRunValid" class="field-error">取值需在 30 到 3600 秒之间。</p>
					</div>
					<div class="field span-2">
						<label class="fl" for="subagent-instructions">系统指令（Instructions）</label>
						<textarea id="subagent-instructions" v-model="form.instructions" class="input mono instructions"
							rows="8" placeholder="写入该子代理执行任务时使用的系统提示"></textarea>
					</div>
				</div>
			</section>

			<CapabilityBindingCard class="sc-rise" style="--i: 3" :sections="sections"
				@open="(section) => (openSectionKey = section.key)" />
		</div>

		<BindingDialog :open="Boolean(openSection)" :kicker="openSection?.kicker || ''"
			:title="openSection ? `绑定${openSection.title}` : ''" :hint="openSection?.hint || ''"
			:items="openSection?.items || []" :empty-text="openSection?.emptyText || ''" :pending="dialogPending"
			@close="openSectionKey = ''" @toggle="onDialogToggle" />
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

.field-error {
	margin: 0;
	color: var(--sc-err);
	font-size: var(--fs-115);
}
</style>
