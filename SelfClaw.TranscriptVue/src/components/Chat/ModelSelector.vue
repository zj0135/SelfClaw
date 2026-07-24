<script setup>
import { ref, computed, nextTick, onMounted, onBeforeUnmount, watch } from 'vue';
import { ChevronDown, ChevronRight, Check, Bot, Terminal } from 'lucide-vue-next';
import claudeIcon from '../../../assets/agents-icons/claude.svg';
import codexIcon from '../../../assets/agents-icons/codex.svg';
import opencodeIcon from '../../../assets/agents-icons/opencode.svg';

/**
 * 模型选择器（药丸按钮 + 设置弹出面板）
 * 「代理」列表与设置页的「编程助手」共享同一后端状态：
 * 打开时通过 get-programming-assistant-settings 读取扫描结果，
 * 选中代理通过 select-programming-cli 持久化，下一次发送回合即生效。
 */

const props = defineProps({
	executionMode: {
		type: String,
		default: 'cli',
	},
});

const emit = defineEmits(['update:agent', 'update:model', 'update:reasoning']);

// 模式：本地 CLI / 提供商（Direct API）。默认跟随当前 Desktop Agent 的 front matter，
// 用户在分段控件上的选择会经 select-composer-mode 持久化为覆盖值，宿主回推后二者一致。
const selectedMode = ref(props.executionMode === 'direct' ? 'direct' : 'cli');
watch(() => props.executionMode, (mode) => {
	selectedMode.value = mode === 'direct' ? 'direct' : 'cli';
});
const isDirect = computed(() => selectedMode.value === 'direct');

const defaultModel = 'Default (CLI config)';

// 已知 CLI 的展示图标：与设置页「编程助手」共用同一组 SVG 资源；未知 id 走 fallback 线条图形。
const agentPresentation = {
	claude: { iconSrc: claudeIcon, iconBackground: '#ffffff' },
	codex: { iconSrc: codexIcon, iconBackground: '#ffffff' },
	opencode: { iconSrc: opencodeIcon, iconBackground: '#ffffff' },
};

const detectedAgents = ref([]);
const directModels = ref([]);
const activeDirectModelProfileId = ref('');
const selectedCliId = ref('');
const loaded = ref(false);
const loadError = ref('');
let requestCounter = 0;
let latestDirectRequestId = '';

const selectedAgent = computed(() => detectedAgents.value.find((agent) => agent.id === selectedCliId.value) || null);
const selectedDirectModel = computed(() =>
	directModels.value.find((model) => model.modelProfileId === activeDirectModelProfileId.value) || null);

// 模型下拉：来自选中 CLI 的模型列表（当前后端只提供 Default）。
const models = computed(() => selectedAgent.value?.models?.length ? selectedAgent.value.models : [defaultModel]);
const activeModel = ref(defaultModel);

// 推理等级：仅部分 CLI（Codex）暴露；首项为 Default 哨兵，长度 > 1 时才展示这一行。
const reasoningLevels = computed(() => selectedAgent.value?.reasoningLevels?.length ? selectedAgent.value.reasoningLevels : []);
const activeReasoning = ref(defaultModel);
const hasReasoning = computed(() => reasoningLevels.value.length > 1);

// 展开状态
const open = ref(false);
const menuOpen = ref(false);
const reasoningMenuOpen = ref(false);
const rootRef = ref(null);
const popoverRef = ref(null);

// 弹层方向：'down' 贴按钮下方，'up' 贴按钮上方。
// 对话视图中输入栏位于窗口底部，向下展开会被 main-content 的 overflow 裁剪，
// 因此打开时按可用空间动态决定方向（空间估算 → 渲染后按实际高度校正一次）。
const placement = ref('down');
const estimatedPopoverHeight = 340;

function updatePlacement() {
	const rect = rootRef.value?.getBoundingClientRect();
	if (!rect) {
		return;
	}

	const popoverHeight = popoverRef.value?.offsetHeight || estimatedPopoverHeight;
	const spaceBelow = window.innerHeight - rect.bottom;
	const spaceAbove = rect.top;
	placement.value = spaceBelow < popoverHeight + 16 && spaceAbove > spaceBelow ? 'up' : 'down';
}

const modelLabel = computed(() => {
	if (!loaded.value) {
		return '加载中…';
	}
	if (isDirect.value) {
		return selectedDirectModel.value
			? `${selectedDirectModel.value.providerName} · ${selectedDirectModel.value.name}`
			: '未选择提供商模型';
	}
	if (!selectedAgent.value) {
		return '未选择 CLI';
	}
	return activeModel.value === defaultModel ? selectedAgent.value.name : activeModel.value;
});

function postToHost(message) {
	window.chrome?.webview?.postMessage(message);
}

function requestSettings() {
	postToHost({
		type: 'get-programming-assistant-settings',
		requestId: `composer-cli-${Date.now()}-${++requestCounter}`,
	});

	if (!window.chrome?.webview) {
		loaded.value = true;
	}
}

function requestDirectModels() {
	const requestId = `composer-direct-${Date.now()}-${++requestCounter}`;
	latestDirectRequestId = requestId;
	postToHost({
		type: 'ai-providers/list-enabled-models',
		requestId,
	});

	if (!window.chrome?.webview) {
		directModels.value = [];
		loaded.value = true;
	}
}

function requestActiveSource() {
	loadError.value = '';
	if (isDirect.value) {
		requestDirectModels();
	} else {
		requestSettings();
	}
}

function applySettings(payload) {
	const rawTools = Array.isArray(payload?.tools) ? payload.tools : [];
	detectedAgents.value = rawTools
		.filter((tool) => tool?.id)
		.map((tool) => ({
			id: tool.id,
			name: tool.name || tool.id,
			models: Array.isArray(tool.models) ? tool.models.filter((m) => typeof m === 'string' && m.trim()) : [],
			reasoningLevels: Array.isArray(tool.reasoningLevels) ? tool.reasoningLevels.filter((r) => typeof r === 'string' && r.trim()) : [],
			...(agentPresentation[tool.id] || { glyph: 'open', tint: '#eef0f3', ink: '#171a1f' }),
		}));

	const normalized = typeof payload?.selectedCliId === 'string' ? payload.selectedCliId.trim().toLowerCase() : '';
	selectedCliId.value = detectedAgents.value.some((agent) => agent.id === normalized) ? normalized : '';

	// 恢复上次持久化的模型：命中当前 CLI 的模型列表才采用，否则回落到该 CLI 的第一项（默认）。
	const persistedModel = typeof payload?.selectedModel === 'string' ? payload.selectedModel : '';
	if (persistedModel && models.value.includes(persistedModel)) {
		activeModel.value = persistedModel;
	} else if (!models.value.includes(activeModel.value)) {
		activeModel.value = models.value[0] || defaultModel;
	}

	// 同理恢复推理等级（仅 Codex 有）。
	const persistedReasoning = typeof payload?.selectedReasoningLevel === 'string' ? payload.selectedReasoningLevel : '';
	if (persistedReasoning && reasoningLevels.value.includes(persistedReasoning)) {
		activeReasoning.value = persistedReasoning;
	} else if (!reasoningLevels.value.includes(activeReasoning.value)) {
		activeReasoning.value = reasoningLevels.value[0] || defaultModel;
	}
	loadError.value = payload?.error ? `CLI 设置同步失败：${payload.error}` : '';
	loaded.value = true;
}

function onHostMessage(event) {
	const payload = event?.data;
	if (payload?.type === 'programming-assistant-settings' && !isDirect.value) {
		applySettings(payload);
		return;
	}

	if (payload?.type === 'ai-providers/list-enabled-models' && isDirect.value) {
		if (payload.requestId && payload.requestId !== latestDirectRequestId) {
			return;
		}

		directModels.value = (Array.isArray(payload.models) ? payload.models : [])
			.filter((model) => model?.modelProfileId)
			.map((model) => ({
				modelProfileId: String(model.modelProfileId),
				name: model.name || model.model || 'Unnamed model',
				model: model.model || '',
				providerName: model.providerName || 'Provider',
			}));
		const persistedId = payload.defaultModelProfileId ? String(payload.defaultModelProfileId) : '';
		activeDirectModelProfileId.value = directModels.value.some((model) => model.modelProfileId === persistedId)
			? persistedId
			: '';
		loadError.value = payload.error ? `模型同步失败：${payload.error}` : '';
		loaded.value = true;
		return;
	}

	if (payload?.type === 'ai-providers/set-default-model' && isDirect.value && payload.error) {
		loadError.value = `默认模型保存失败：${payload.error}`;
		requestDirectModels();
	}
}

function togglePanel() {
	open.value = !open.value;
	if (!open.value) {
		menuOpen.value = false;
		reasoningMenuOpen.value = false;
		return;
	}

	updatePlacement();
	nextTick(updatePlacement);

	if (!loaded.value) {
		// 自愈：挂载时的请求若丢失（宿主未就绪等），打开面板时再拉一次配置。
		requestActiveSource();
	}
}
function closePanel() {
	open.value = false;
	menuOpen.value = false;
	reasoningMenuOpen.value = false;
}

function pickMode(mode) {
	if (selectedMode.value === mode) {
		return;
	}

	selectedMode.value = mode;
	postToHost({
		type: 'select-composer-mode',
		requestId: `composer-mode-${Date.now()}-${++requestCounter}`,
		mode,
	});
}

function pickAgent(agent) {
	if (agent.id === selectedCliId.value) {
		return;
	}

	selectedCliId.value = agent.id;
	activeModel.value = agent.models?.[0] || defaultModel;
	activeReasoning.value = agent.reasoningLevels?.[0] || defaultModel;
	emit('update:agent', agent.id);
	postToHost({
		type: 'select-programming-cli',
		requestId: `composer-cli-${Date.now()}-${++requestCounter}`,
		cliId: agent.id,
	});
}

function pickDirectModel(model) {
	if (!model?.modelProfileId || model.modelProfileId === activeDirectModelProfileId.value) {
		menuOpen.value = false;
		return;
	}

	activeDirectModelProfileId.value = model.modelProfileId;
	menuOpen.value = false;
	loadError.value = '';
	emit('update:model', model.modelProfileId);
	postToHost({
		type: 'ai-providers/set-default-model',
		requestId: `composer-direct-default-${Date.now()}-${++requestCounter}`,
		scope: 'desktop-default',
		modelProfileId: model.modelProfileId,
	});
}

function toggleMenu() {
	menuOpen.value = !menuOpen.value;
	if (menuOpen.value) {
		reasoningMenuOpen.value = false;
	}
}
function pickModel(m) {
	activeModel.value = m;
	menuOpen.value = false;
	emit('update:model', m);
	// 持久化到宿主（desktop-settings.json 的 programming_assistant.selectedModel），下次启动默认选中。
	postToHost({
		type: 'select-programming-model',
		requestId: `composer-model-${Date.now()}-${++requestCounter}`,
		model: m,
	});
}

function toggleReasoningMenu() {
	reasoningMenuOpen.value = !reasoningMenuOpen.value;
	if (reasoningMenuOpen.value) {
		menuOpen.value = false;
	}
}
function pickReasoning(level) {
	activeReasoning.value = level;
	reasoningMenuOpen.value = false;
	emit('update:reasoning', level);
	// 持久化到 programming_assistant.selectedReasoningLevel；Codex 回合会转成 -c model_reasoning_effort。
	postToHost({
		type: 'select-programming-reasoning',
		requestId: `composer-reasoning-${Date.now()}-${++requestCounter}`,
		reasoningLevel: level,
	});
}

function onDocClick(e) {
	if (rootRef.value && !rootRef.value.contains(e.target)) closePanel();
}
function onKeydown(e) {
	if (e.key === 'Escape') closePanel();
}
onMounted(() => {
	document.addEventListener('click', onDocClick);
	document.addEventListener('keydown', onKeydown);
	window.chrome?.webview?.addEventListener('message', onHostMessage);
	requestActiveSource();
});
onBeforeUnmount(() => {
	document.removeEventListener('click', onDocClick);
	document.removeEventListener('keydown', onKeydown);
	window.chrome?.webview?.removeEventListener('message', onHostMessage);
});

watch(isDirect, () => {
	loaded.value = false;
	menuOpen.value = false;
	reasoningMenuOpen.value = false;
	requestActiveSource();
});
</script>

<template>
	<div ref="rootRef" class="model-wrap">
		<button
			class="composer-model"
			type="button"
			:aria-expanded="open ? 'true' : 'false'"
			aria-haspopup="true"
			title="模型选择"
			@click.stop="togglePanel"
		>
			<span class="model-badge" :class="{ 'model-badge--brand': !isDirect && !!selectedAgent?.iconSrc }" aria-hidden="true">
				<img v-if="!isDirect && selectedAgent?.iconSrc" class="model-badge-img" :src="selectedAgent.iconSrc" alt="" />
				<Bot v-else :size="12" :stroke-width="2" />
			</span>
			<span class="model-name">{{ modelLabel }}</span>
			<ChevronDown class="model-caret" :size="13" :stroke-width="2" aria-hidden="true" />
		</button>

		<!-- 设置弹出面板：默认贴按钮下方展开；下方空间不足时自动翻转到按钮上方 -->
		<div v-show="open" ref="popoverRef" class="model-popover" :class="`model-popover--${placement}`" role="dialog"
			aria-label="模型与代理设置">
			<!-- 模式 -->
			<div class="pop-section">
				<div class="pop-label">模式</div>
				<div class="seg" role="radiogroup" aria-label="执行模式">
					<button
						type="button"
						:aria-pressed="!isDirect ? 'true' : 'false'"
						@click="pickMode('cli')"
					>本地 CLI</button>
					<button
						type="button"
						:aria-pressed="isDirect ? 'true' : 'false'"
						@click="pickMode('direct')"
					>提供商</button>
				</div>
			</div>

			<!-- 代理 -->
			<div v-if="!isDirect" class="pop-section">
				<div class="pop-label">代理</div>
				<div v-if="!loaded" class="agent-hint">正在读取本地 CLI 配置…</div>
				<div v-else-if="loadError" class="agent-hint agent-hint--error">{{ loadError }}</div>
				<div v-else-if="!detectedAgents.length" class="agent-hint">
					未检测到本地 CLI，请安装 Claude Code / Codex CLI / OpenCode 后在「设置 → 编程助手」重新扫描。
				</div>
				<div class="agent-list" role="radiogroup" aria-label="代理">
					<button
						v-for="agent in detectedAgents"
						:key="agent.id"
						type="button"
						class="agent-item"
						role="radio"
						:aria-checked="selectedCliId === agent.id ? 'true' : 'false'"
						@click="pickAgent(agent)"
					>
						<span
							class="agent-glyph"
							:style="{ background: agent.iconBackground || agent.tint, color: agent.ink }"
							aria-hidden="true"
						>
							<img v-if="agent.iconSrc" class="agent-glyph-img" :src="agent.iconSrc" alt="" />
							<Terminal v-else :size="13" :stroke-width="2" />
						</span>
						<span class="agent-name">{{ agent.name }}</span>
						<Check class="agent-check" :size="14" :stroke-width="2.4" aria-hidden="true" />
					</button>
				</div>
			</div>

			<!-- 模型 -->
			<div class="pop-section">
				<div class="pop-label">模型</div>
				<div v-if="isDirect && loaded && !directModels.length" class="agent-hint">
					没有已启用模型，请先在「设置 → AI 服务商」启用连接与模型。
				</div>
				<div v-else class="model-select">
					<button
						type="button"
						class="model-select-btn"
						:aria-expanded="menuOpen ? 'true' : 'false'"
						aria-haspopup="listbox"
						@click.stop="toggleMenu"
					>
						<span>{{ isDirect ? (selectedDirectModel?.name || '未选择模型') : activeModel }}</span>
						<ChevronRight class="caret" :size="13" :stroke-width="2" aria-hidden="true" />
					</button>
					<div v-if="isDirect" v-show="menuOpen" class="model-menu" role="listbox" aria-label="Direct 模型列表">
						<button
							v-for="directModel in directModels"
							:key="directModel.modelProfileId"
							type="button"
							class="model-opt"
							role="option"
							:aria-selected="activeDirectModelProfileId === directModel.modelProfileId ? 'true' : 'false'"
							@click="pickDirectModel(directModel)"
						>
							<span class="model-opt-copy">
								<strong>{{ directModel.name }}</strong>
								<small>{{ directModel.providerName }} · {{ directModel.model }}</small>
							</span>
							<Check class="tick" :size="14" :stroke-width="2.4" aria-hidden="true" />
						</button>
					</div>
					<div v-else v-show="menuOpen" class="model-menu" role="listbox" aria-label="模型列表">
						<button
							v-for="m in models"
							:key="m"
							type="button"
							class="model-opt"
							role="option"
							:aria-selected="activeModel === m ? 'true' : 'false'"
							@click="pickModel(m)"
						>
							<span>{{ m }}</span>
							<Check class="tick" :size="14" :stroke-width="2.4" aria-hidden="true" />
						</button>
					</div>
				</div>
			</div>

			<!-- 推理等级：仅当选中 CLI 暴露推理档位（如 Codex）时出现 -->
			<div v-if="!isDirect && hasReasoning" class="pop-section">
				<div class="pop-label">推理等级</div>
				<div class="model-select">
					<button
						type="button"
						class="model-select-btn"
						:aria-expanded="reasoningMenuOpen ? 'true' : 'false'"
						aria-haspopup="listbox"
						@click.stop="toggleReasoningMenu"
					>
						<span>{{ activeReasoning }}</span>
						<ChevronRight class="caret" :size="13" :stroke-width="2" aria-hidden="true" />
					</button>
					<div v-show="reasoningMenuOpen" class="model-menu" role="listbox" aria-label="推理等级列表">
						<button
							v-for="level in reasoningLevels"
							:key="level"
							type="button"
							class="model-opt"
							role="option"
							:aria-selected="activeReasoning === level ? 'true' : 'false'"
							@click="pickReasoning(level)"
						>
							<span>{{ level }}</span>
							<Check class="tick" :size="14" :stroke-width="2.4" aria-hidden="true" />
						</button>
					</div>
				</div>
			</div>
		</div>
	</div>
</template>

<style scoped>
.model-wrap {
	position: relative;
	display: inline-flex;
}

/* ===== 模型选择触发按钮 ===== */
.composer-model {
	display: inline-flex;
	align-items: center;
	gap: 7px;
	height: 32px;
	padding: 0 8px;
	border: 1px solid #e5e7eb;
	border-radius: 9px;
	background: #ffffff;
	color: #171a1f;
	font-size: 12.5px;
	font-weight: 550;
	letter-spacing: 0.01em;
	white-space: nowrap;
	cursor: pointer;
	transition: background 0.15s, border-color 0.15s, box-shadow 0.15s;
}
.composer-model:hover { background: #f7f8fa; }
.composer-model[aria-expanded='true'] {
	background: #f7f8fa;
	border-color: #d6dae1;
	box-shadow: 0 0 0 3px rgba(23, 26, 31, 0.04);
}

.model-badge {
	display: inline-grid;
	place-items: center;
	width: 18px;
	height: 18px;
	border-radius: 6px;
	background: var(--accent, #3b5bfd);
	color: #fff;
	flex: none;
}
.model-badge svg { width: 12px; height: 12px; }
/* 选中 CLI 后徽标显示品牌图标：去掉深色底，让图标以自身配色呈现 */
.model-badge--brand { background: transparent; }
.model-badge-img {
	display: block;
	width: 15px;
	height: 15px;
	object-fit: contain;
}

.model-name {
	overflow: hidden;
	text-overflow: ellipsis;
	max-width: 180px;
}
.model-caret {
	width: 13px;
	height: 13px;
	color: #6b7280;
	flex: none;
	transition: transform 0.18s ease;
}
.composer-model[aria-expanded='true'] .model-caret { transform: rotate(180deg); }

/* ===== 设置弹出面板 ===== */
.model-popover {
	position: absolute;
	left: 0;
	width: 236px;
	padding: 10px;
	border: 1px solid #e2e5eb;
	border-radius: 12px;
	background: #ffffff;
	box-shadow: 0 1px 2px rgba(23, 26, 31, 0.05), 0 12px 32px rgba(23, 26, 31, 0.12);
	z-index: 40;
}

.model-popover--down {
	top: calc(100% + 6px);
	transform-origin: top left;
	animation: pop-in-down 0.16s cubic-bezier(0.16, 1, 0.3, 1);
}

.model-popover--up {
	bottom: calc(100% + 6px);
	transform-origin: bottom left;
	animation: pop-in-up 0.16s cubic-bezier(0.16, 1, 0.3, 1);
}

@keyframes pop-in-down {
	from { opacity: 0; transform: translateY(-6px) scale(0.98); }
	to   { opacity: 1; transform: translateY(0) scale(1); }
}

@keyframes pop-in-up {
	from { opacity: 0; transform: translateY(6px) scale(0.98); }
	to   { opacity: 1; transform: translateY(0) scale(1); }
}

@media (prefers-reduced-motion: reduce) {
	.model-popover--down,
	.model-popover--up {
		animation: none;
	}
}

.pop-label {
	font-size: 10.5px;
	font-weight: 600;
	letter-spacing: 0.04em;
	color: #6b7280;
	margin: 2px 2px 6px;
}
.pop-section + .pop-section { margin-top: 11px; }

.agent-hint {
	margin: 0 2px 6px;
	color: #8f9aab;
	font-size: 11.5px;
	line-height: 1.55;
}
.agent-hint--error { color: #c24150; }

/* 模式分段控件 */
.seg {
	display: grid;
	grid-template-columns: 1fr 1fr;
	gap: 3px;
	padding: 2px;
	border: 1px solid #eef0f3;
	border-radius: 9px;
	background: #f7f8fa;
}
.seg button {
	height: 26px;
	border: 0;
	border-radius: 6px;
	background: transparent;
	color: #6b7280;
	font: 550 12px/1 inherit;
	cursor: pointer;
	transition: background 0.15s, color 0.15s, box-shadow 0.15s;
}
.seg button[aria-pressed='true'] {
	background: #ffffff;
	color: #171a1f;
	box-shadow: 0 1px 2px rgba(23, 26, 31, 0.08);
}

/* 代理列表：固定可视高度（约 4 行），超出滚动 */
.agent-list {
	display: grid;
	gap: 5px;
	max-height: 152px;
	overflow-y: auto;
	overscroll-behavior: contain;
	padding-right: 2px;
}
.agent-list::-webkit-scrollbar { width: 6px; }
.agent-list::-webkit-scrollbar-track { background: transparent; }
.agent-list::-webkit-scrollbar-thumb {
	background: #dde1e7;
	border-radius: 99px;
}
.agent-item {
	display: flex;
	align-items: center;
	gap: 8px;
	width: 100%;
	padding: 6px 8px;
	border: 1px solid #eef0f3;
	border-radius: 9px;
	background: #ffffff;
	color: #171a1f;
	font: 550 12.5px/1.2 inherit;
	text-align: left;
	cursor: pointer;
	transition: background 0.15s, border-color 0.15s;
}
.agent-item:hover { background: #f7f8fa; }
.agent-item[aria-checked='true'] {
	background: var(--accent-soft, rgba(59, 91, 253, 0.08));
	border-color: rgba(59, 91, 253, 0.3);
}
.agent-glyph {
	display: inline-grid;
	place-items: center;
	width: 20px;
	height: 20px;
	border-radius: 5px;
	flex: none;
}
.agent-glyph svg { width: 13px; height: 13px; }
.agent-glyph-img {
	display: block;
	width: 14px;
	height: 14px;
	object-fit: contain;
}
.agent-name { flex: 1; min-width: 0; }
.agent-check {
	width: 14px;
	height: 14px;
	color: var(--accent, #3b5bfd);
	display: none;
}
.agent-item[aria-checked='true'] .agent-check { display: block; }

/* 模型下拉 */
.model-select { position: relative; }
.model-select-btn {
	display: flex;
	align-items: center;
	justify-content: space-between;
	gap: 8px;
	width: 100%;
	height: 32px;
	padding: 0 9px 0 10px;
	border: 1px solid #e5e7eb;
	border-radius: 9px;
	background: #ffffff;
	color: #171a1f;
	font: 550 12.5px/1 inherit;
	cursor: pointer;
	transition: background 0.15s, border-color 0.15s;
}
/* 模型名过长时截断为省略号，保持单行不换行 */
.model-select-btn > span {
	flex: 1;
	min-width: 0;
	overflow: hidden;
	text-align: left;
	text-overflow: ellipsis;
	white-space: nowrap;
}
.model-select-btn:hover { background: #f7f8fa; }
/* 菜单在右侧展开，箭头指向右以示意 */
.model-select-btn .caret {
	color: #6b7280;
	flex: none;
}

/* 模型菜单：贴着选择框右侧展开、底边对齐向上生长，避免被窗口下缘遮挡。
   固定 320px 高度上限，模型过多时内部滚动。 */
.model-menu {
	position: absolute;
	left: calc(100% + 8px);
	bottom: 0;
	box-sizing: border-box;
	width: max-content;
	min-width: 170px;
	max-width: 240px;
	max-height: 320px;
	overflow-y: auto;
	overscroll-behavior: contain;
	padding: 4px;
	border: 1px solid #e2e5eb;
	border-radius: 9px;
	background: #ffffff;
	box-shadow: 0 1px 2px rgba(23, 26, 31, 0.05), 0 12px 32px rgba(23, 26, 31, 0.12);
	z-index: 3;
}
.model-menu::-webkit-scrollbar { width: 6px; }
.model-menu::-webkit-scrollbar-track { background: transparent; }
.model-menu::-webkit-scrollbar-thumb {
	background: #dde1e7;
	border-radius: 99px;
}
.model-opt {
	display: flex;
	align-items: center;
	justify-content: space-between;
	gap: 8px;
	width: 100%;
	padding: 6px 8px;
	border: 0;
	border-radius: 6px;
	background: transparent;
	color: #171a1f;
	font: 500 12.5px/1.2 inherit;
	text-align: left;
	cursor: pointer;
}
.model-opt:hover { background: #f7f8fa; }
.model-opt .tick { width: 14px; height: 14px; color: #171a1f; display: none; flex: none; }
.model-opt[aria-selected='true'] { font-weight: 600; }
.model-opt[aria-selected='true'] .tick { display: block; }
.model-opt-copy {
	display: grid;
	min-width: 0;
	gap: 2px;
}
.model-opt-copy strong,
.model-opt-copy small {
	overflow: hidden;
	text-overflow: ellipsis;
	white-space: nowrap;
}
.model-opt-copy strong { font: inherit; }
.model-opt-copy small {
	color: #8a929e;
	font-size: 10.5px;
	font-weight: 500;
}
</style>
