<script setup>
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import { Check, ChevronRight, Layers, Terminal } from 'lucide-vue-next';
import { useComposerModelSelection } from '../../../composables/useComposerModelSelection.js';
import { useHostBridge } from '../../../composables/hostBridge.js';
import { defaultCliModel as defaultModel } from '../../../composables/useProgrammingAssistantSelection.js';

const props = defineProps({
	executionMode: {
		type: String,
		default: 'cli',
	},
	align: {
		type: String,
		default: 'start',
	},
});

const emit = defineEmits(['update:agent', 'update:model', 'update:reasoning']);

const {
	selectedMode,
	isDirect,
	loaded,
	loadError,
	models,
	reasoningLevels,
	hasReasoning,
	pending,
	detectedAgents,
	selectedCliId,
	selectedAgent,
	activeModel,
	activeReasoning,
	directModels,
	activeDirectModelProfileId,
	selectedDirectModel,
	requestActiveSource,
	pickMode,
	pickAgent,
	pickDirectModel: saveDirectModel,
	pickModel: saveModel,
	pickReasoning: saveReasoning,
} = useComposerModelSelection(() => props.executionMode, emit);

const bridge = useHostBridge();
const modelConfigurations = ref({});

async function loadModelConfigurations() {
	try {
		const payload = await bridge.requestLatest('composer-model-configs', 'ai-providers/list-model-configurations');
		const configs = {};
		for (const cfg of payload?.configurations || []) {
			if (cfg?.model) configs[cfg.model] = cfg;
		}
		modelConfigurations.value = configs;
	} catch {
		// 保持现有配置
	}
}

const open = ref(false);
const rootRef = ref(null);
const popoverRef = ref(null);
const placement = ref('down');
const estimatedPopoverHeight = 280;

const activeFlyoutModel = ref(null);
const flyoutTop = ref(0);
const flyoutBottom = ref(0);
let flyoutLeaveTimer = null;

const DIRECT_REASONING_OPTIONS = [
	{ id: '', label: 'Default' },
	{ id: 'none', label: 'None' },
	{ id: 'minimal', label: 'Minimal' },
	{ id: 'low', label: 'Low' },
	{ id: 'medium', label: 'Medium' },
	{ id: 'high', label: 'High' },
	{ id: 'xhigh', label: 'X-High' },
	{ id: 'max', label: 'Max' },
];

function formatReasoning(level) {
	if (!level) return '';
	const map = {
		none: 'None',
		minimal: 'Minimal',
		low: 'Low',
		medium: 'Medium',
		high: 'High',
		xhigh: 'X-High',
		max: 'Max',
	};
	return map[level.toLowerCase()] || level;
}

function getModelReasoning(directModel) {
	if (!directModel?.model) return '';
	return modelConfigurations.value[directModel.model]?.reasoningEffort || '';
}

async function setDirectModelReasoning(directModel, level) {
	const currentCfg = modelConfigurations.value[directModel.model] || {};
	const configuration = {
		...currentCfg,
		model: directModel.model,
		name: directModel.name || directModel.model,
		sampling: {
			temperature: 0.7,
			temperatureEnabled: false,
			topP: 0.7,
			topPEnabled: false,
			...(currentCfg.sampling || {}),
		},
		reasoningEffort: level || null,
	};
	modelConfigurations.value[directModel.model] = configuration;
	try {
		await bridge.request('ai-providers/save-model-configuration', { configuration });
	} catch (e) {
		console.warn('保存模型思考等级失败:', e);
	}
	await saveDirectModel(directModel);
	activeFlyoutModel.value = null;
}

async function pickCliReasoningAndSelect(model, level) {
	await saveModel(model);
	await saveReasoning(level);
	activeFlyoutModel.value = null;
}

function updatePlacement() {
	const rect = rootRef.value?.getBoundingClientRect();
	if (!rect) return;

	const popoverHeight = popoverRef.value?.offsetHeight || estimatedPopoverHeight;
	const spaceBelow = window.innerHeight - rect.bottom;
	const spaceAbove = rect.top;
	placement.value = spaceBelow < popoverHeight + 16 && spaceAbove > spaceBelow ? 'up' : 'down';
}

const modelLabel = computed(() => {
	if (!loaded.value) return '加载中…';
	if (isDirect.value) {
		return selectedDirectModel.value?.name || '未选择提供商模型';
	}
	if (!selectedAgent.value) return '未选择 CLI';
	return activeModel.value === defaultModel ? selectedAgent.value.name : activeModel.value;
});

const triggerTitle = computed(() => {
	if (isDirect.value && selectedDirectModel.value) {
		const reasoning = getModelReasoning(selectedDirectModel.value);
		const reasoningText = reasoning ? ` (${formatReasoning(reasoning)})` : '';
		return `模型选择：${selectedDirectModel.value.providerName} · ${selectedDirectModel.value.name}${reasoningText}`;
	}
	return `模型选择：${modelLabel.value}`;
});

function togglePanel() {
	open.value = !open.value;
	if (!open.value) {
		activeFlyoutModel.value = null;
		return;
	}

	updatePlacement();
	nextTick(updatePlacement);

	if (!loaded.value) {
		requestActiveSource();
	}
	if (isDirect.value) {
		loadModelConfigurations();
	}
}

function closePanel() {
	open.value = false;
	activeFlyoutModel.value = null;
}

async function pickDirectModel(model) {
	await saveDirectModel(model);
	closePanel();
}

async function pickModel(model) {
	await saveModel(model);
	closePanel();
}

function onRowHover(model, event) {
	if (flyoutLeaveTimer) {
		clearTimeout(flyoutLeaveTimer);
		flyoutLeaveTimer = null;
	}
	activeFlyoutModel.value = model;
	if (event?.currentTarget && popoverRef.value) {
		const rowRect = event.currentTarget.getBoundingClientRect();
		const popoverRect = popoverRef.value.getBoundingClientRect();
		const estimatedFlyoutHeight = 268;

		if (placement.value === 'up') {
			// 上浮菜单：子菜单底对齐当前行并向上展开，彻底防止被视口底边裁切
			const rawBottom = popoverRect.bottom - rowRect.bottom;
			const spaceAboveInWindow = rowRect.bottom;
			if (spaceAboveInWindow < estimatedFlyoutHeight + 16) {
				const overflow = estimatedFlyoutHeight + 16 - spaceAboveInWindow;
				flyoutBottom.value = Math.max(0, rawBottom - overflow);
			} else {
				flyoutBottom.value = Math.max(0, rawBottom);
			}
		} else {
			// 下沉菜单：子菜单顶对齐当前行并向下展开
			const rawTop = rowRect.top - popoverRect.top;
			const spaceBelowInWindow = window.innerHeight - rowRect.top;
			if (spaceBelowInWindow < estimatedFlyoutHeight + 16) {
				const overflow = estimatedFlyoutHeight + 16 - spaceBelowInWindow;
				flyoutTop.value = Math.max(0, rawTop - overflow);
			} else {
				flyoutTop.value = Math.max(0, rawTop);
			}
		}
	}
}

function onRowLeave() {
	flyoutLeaveTimer = setTimeout(() => {
		activeFlyoutModel.value = null;
		flyoutLeaveTimer = null;
	}, 160);
}

function onFlyoutEnter() {
	if (flyoutLeaveTimer) {
		clearTimeout(flyoutLeaveTimer);
		flyoutLeaveTimer = null;
	}
}

function onFlyoutLeave() {
	onRowLeave();
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
	requestActiveSource();
	loadModelConfigurations();
});

onBeforeUnmount(() => {
	if (flyoutLeaveTimer) clearTimeout(flyoutLeaveTimer);
	document.removeEventListener('click', onDocClick);
	document.removeEventListener('keydown', onKeydown);
});

watch(isDirect, () => {
	activeFlyoutModel.value = null;
	requestActiveSource();
	if (isDirect.value) loadModelConfigurations();
});
</script>

<template>
	<div ref="rootRef" class="model-wrap">
		<!-- 触发按钮：模型架构样式 Layers 图标 + 模型名 -->
		<button
			class="composer-model"
			type="button"
			:aria-expanded="open ? 'true' : 'false'"
			aria-haspopup="true"
			:title="triggerTitle"
			@click.stop="togglePanel"
		>
			<span class="model-badge" aria-hidden="true">
				<img v-if="!isDirect && selectedAgent?.iconSrc" class="model-badge-img" :src="selectedAgent.iconSrc" alt="" />
				<Layers v-else :size="15" :stroke-width="1.8" />
			</span>
			<span class="model-name">{{ modelLabel }}</span>
		</button>

		<!-- 弹出面板：严格对标图二/图三，直接展示模型列表，无多余嵌套大卡片 -->
		<div
			v-show="open"
			ref="popoverRef"
			:aria-busy="pending"
			class="model-popover"
			:class="[`model-popover--${placement}`, `model-popover--${props.align}`]"
			role="dialog"
			aria-label="模型选择"
		>
			<!-- 模式切换：轻量胶囊 -->
			<div class="pop-header">
				<div class="seg" role="tablist" aria-label="执行模式">
					<button type="button" class="seg-btn" :aria-pressed="!isDirect ? 'true' : 'false'" @click="pickMode('cli')">本地 CLI</button>
					<button type="button" class="seg-btn" :aria-pressed="isDirect ? 'true' : 'false'" @click="pickMode('direct')">提供商</button>
				</div>
			</div>

			<p v-if="loadError" class="model-hint model-hint--error" role="alert">{{ loadError }}</p>

			<!-- 提供商直接模式模型列表（图三风格） -->
			<div v-if="isDirect" class="models-list" role="listbox" aria-label="提供商模型列表">
				<div v-if="loaded && !directModels.length" class="model-hint">没有已启用模型，请在「设置 → AI 服务商」启用连接与模型。</div>
				<div
					v-for="dm in directModels"
					:key="dm.modelProfileId"
					class="model-row"
					:class="{
						active: activeDirectModelProfileId === dm.modelProfileId,
						hovered: activeFlyoutModel?.modelProfileId === dm.modelProfileId,
					}"
					role="option"
					:aria-selected="activeDirectModelProfileId === dm.modelProfileId ? 'true' : 'false'"
					@mouseenter="onRowHover(dm, $event)"
					@mouseleave="onRowLeave"
					@click="pickDirectModel(dm)"
				>
					<div class="model-row-left">
						<span class="model-row-title">{{ dm.name }}</span>
						<span v-if="dm.providerName" class="model-row-desc">{{ dm.providerName }} · {{ dm.model }}</span>
					</div>

					<div class="model-row-right">
						<!-- 思考等级标签 + 箭头（对标图三：Gemini 3.8 Flash High >） -->
						<span v-if="getModelReasoning(dm)" class="reasoning-tag">
							{{ formatReasoning(getModelReasoning(dm)) }}
						</span>
						<ChevronRight :size="12" class="reasoning-arrow" aria-hidden="true" />
						<Check
							class="model-row-check"
							:class="{ visible: activeDirectModelProfileId === dm.modelProfileId }"
							:size="14"
							:stroke-width="2.4"
							aria-hidden="true"
						/>
					</div>
				</div>
			</div>

			<!-- CLI 模式模型列表 -->
			<div v-else class="models-list" role="listbox" aria-label="CLI 代理与模型">
				<div v-if="detectedAgents.length > 1" class="cli-agents-bar" role="tablist" aria-label="CLI 工具">
					<button
						v-for="agent in detectedAgents"
						:key="agent.id"
						type="button"
						class="cli-agent-chip"
						:class="{ active: selectedCliId === agent.id }"
						@click="pickAgent(agent)"
					>
						{{ agent.name }}
					</button>
				</div>
				<div
					v-for="m in models"
					:key="m"
					class="model-row"
					:class="{
						active: activeModel === m,
						hovered: activeFlyoutModel === m,
					}"
					role="option"
					:aria-selected="activeModel === m ? 'true' : 'false'"
					@mouseenter="onRowHover(m, $event)"
					@mouseleave="onRowLeave"
					@click="pickModel(m)"
				>
					<div class="model-row-left">
						<span class="model-row-title">{{ m }}</span>
					</div>
					<div class="model-row-right">
						<span v-if="activeModel === m && activeReasoning" class="reasoning-tag">
							{{ formatReasoning(activeReasoning) }}
						</span>
						<ChevronRight v-if="reasoningLevels.length" :size="12" class="reasoning-arrow" aria-hidden="true" />
						<Check class="model-row-check" :class="{ visible: activeModel === m }" :size="14" :stroke-width="2.4" aria-hidden="true" />
					</div>
				</div>
			</div>

			<!-- 悬停飞出思考等级子菜单（图三右侧风格：Low / Medium / High） -->
			<div
				v-show="activeFlyoutModel"
				class="thinking-flyout"
				:style="placement === 'up' ? { bottom: `${flyoutBottom}px` } : { top: `${flyoutTop}px` }"
				role="listbox"
				aria-label="思考等级选择"
				@mouseenter="onFlyoutEnter"
				@mouseleave="onFlyoutLeave"
			>
				<div class="thinking-flyout-head">思考等级</div>
				<template v-if="isDirect && activeFlyoutModel?.modelProfileId">
					<button
						v-for="opt in DIRECT_REASONING_OPTIONS"
						:key="opt.id"
						type="button"
						class="thinking-item"
						:class="{ active: (getModelReasoning(activeFlyoutModel) || '') === opt.id }"
						@click.stop="setDirectModelReasoning(activeFlyoutModel, opt.id)"
					>
						<span>{{ opt.label }}</span>
						<Check
							v-if="(getModelReasoning(activeFlyoutModel) || '') === opt.id"
							:size="13"
							:stroke-width="2.4"
							class="thinking-check"
							aria-hidden="true"
						/>
					</button>
				</template>
				<template v-else-if="!isDirect && reasoningLevels.length">
					<button
						v-for="level in reasoningLevels"
						:key="level"
						type="button"
						class="thinking-item"
						:class="{ active: activeReasoning === level }"
						@click.stop="pickCliReasoningAndSelect(activeFlyoutModel, level)"
					>
						<span>{{ formatReasoning(level) }}</span>
						<Check v-if="activeReasoning === level" :size="13" :stroke-width="2.4" class="thinking-check" aria-hidden="true" />
					</button>
				</template>
			</div>
		</div>
	</div>
</template>

<style scoped>
.model-wrap {
	position: relative;
	display: inline-flex;
	align-items: center;
}

/* ===== 触发按钮 ===== */
.composer-model {
	display: inline-flex;
	align-items: center;
	gap: 6px;
	height: 28px;
	min-width: 0;
	padding: 0 8px;
	border: 0;
	border-radius: 8px;
	background: transparent;
	color: var(--text-soft);
	font-size: var(--fs-125);
	font-weight: 500;
	letter-spacing: 0.01em;
	white-space: nowrap;
	cursor: pointer;
	transition:
		background 0.15s,
		color 0.15s;
}

.composer-model:hover,
.composer-model[aria-expanded='true'] {
	background: var(--panel-muted);
	color: var(--text);
}

.model-badge {
	display: inline-grid;
	place-items: center;
	width: 15px;
	height: 15px;
	color: var(--muted);
	flex: none;
	transition: color 0.15s;
}

.model-badge svg {
	width: 15px;
	height: 15px;
}

.composer-model:hover .model-badge,
.composer-model[aria-expanded='true'] .model-badge {
	color: var(--text);
}

.model-badge-img {
	display: block;
	width: 15px;
	height: 15px;
	object-fit: contain;
}

.model-name {
	overflow: hidden;
	text-overflow: ellipsis;
	max-width: 168px;
}

/* ===== 主下拉面板（图二/图三简洁风格） ===== */
.model-popover {
	position: absolute;
	left: 0;
	width: 256px;
	padding: 6px;
	border: 1px solid var(--border);
	border-radius: 12px;
	background: var(--panel);
	box-shadow:
		0 4px 16px rgba(var(--shadow-ink), 0.08),
		0 16px 36px rgba(var(--shadow-ink), 0.16);
	z-index: 50;
}

.model-popover--down {
	top: calc(100% + 7px);
	transform-origin: top left;
	animation: pop-in-down 0.16s cubic-bezier(0.16, 1, 0.3, 1);
}

.model-popover--up {
	bottom: calc(100% + 7px);
	transform-origin: bottom left;
	animation: pop-in-up 0.16s cubic-bezier(0.16, 1, 0.3, 1);
}

.model-popover--end {
	left: auto;
	right: 0;
}

.model-popover--end.model-popover--down {
	transform-origin: top right;
}

.model-popover--end.model-popover--up {
	transform-origin: bottom right;
}

@keyframes pop-in-down {
	from {
		opacity: 0;
		transform: translateY(-5px) scale(0.98);
	}
	to {
		opacity: 1;
		transform: translateY(0) scale(1);
	}
}

@keyframes pop-in-up {
	from {
		opacity: 0;
		transform: translateY(5px) scale(0.98);
	}
	to {
		opacity: 1;
		transform: translateY(0) scale(1);
	}
}

@media (prefers-reduced-motion: reduce) {
	.model-popover--down,
	.model-popover--up {
		animation: none;
	}
}

/* 顶部模式切换 */
.pop-header {
	margin-bottom: 5px;
	padding: 2px 2px 4px;
}

.seg {
	display: grid;
	grid-template-columns: 1fr 1fr;
	gap: 2px;
	padding: 2px;
	border: 1px solid var(--border);
	border-radius: 8px;
	background: var(--bg-canvas);
}

.seg-btn {
	height: 24px;
	border: 0;
	border-radius: 6px;
	background: transparent;
	color: var(--muted);
	font: 500 11.5px/1 inherit;
	cursor: pointer;
	transition:
		background 0.15s,
		color 0.15s,
		box-shadow 0.15s;
}

.seg-btn[aria-pressed='true'] {
	background: var(--panel);
	color: var(--text-strong);
	box-shadow: 0 1px 3px rgba(var(--shadow-ink), 0.1);
}

.model-hint {
	margin: 6px 8px;
	color: var(--muted-soft);
	font-size: 11.5px;
	line-height: 1.5;
}

.model-hint--error {
	color: var(--err-text);
}

/* CLI 工具切换条 */
.cli-agents-bar {
	display: flex;
	gap: 4px;
	padding: 0 4px 6px;
	border-bottom: 1px solid var(--border);
	margin-bottom: 4px;
}

.cli-agent-chip {
	padding: 2px 7px;
	border: 0;
	border-radius: 5px;
	background: transparent;
	color: var(--muted);
	font-size: 11px;
	cursor: pointer;
}

.cli-agent-chip.active {
	background: var(--panel-soft);
	color: var(--text);
	font-weight: 600;
}

/* ===== 模型列表（图三设计） ===== */
.models-list {
	display: grid;
	gap: 2px;
	max-height: 260px;
	overflow-y: auto;
	padding: 2px 0;
}

.models-list::-webkit-scrollbar {
	width: 5px;
}

.models-list::-webkit-scrollbar-track {
	background: transparent;
}

.models-list::-webkit-scrollbar-thumb {
	background: var(--border-strong);
	border-radius: 99px;
}

/* 单个模型行 */
.model-row {
	display: flex;
	align-items: center;
	justify-content: space-between;
	gap: 8px;
	width: 100%;
	padding: 7px 9px;
	border: 0;
	border-radius: 8px;
	background: transparent;
	color: var(--text);
	text-align: left;
	cursor: pointer;
	transition: background 0.12s ease;
}

.model-row:hover,
.model-row.hovered {
	background: var(--panel-soft);
}

.model-row.active {
	background: var(--panel-soft);
}

.model-row-left {
	display: grid;
	gap: 1px;
	min-width: 0;
	flex: 1;
}

.model-row-title {
	font-size: 12.5px;
	color: var(--text);
	overflow: hidden;
	text-overflow: ellipsis;
	white-space: nowrap;
	line-height: 1.25;
}

.model-row-desc {
	font-size: 10.5px;
	color: var(--muted);
	overflow: hidden;
	text-overflow: ellipsis;
	white-space: nowrap;
	line-height: 1.35;
}

.model-row-right {
	display: flex;
	align-items: center;
	gap: 5px;
	flex: none;
}

.reasoning-tag {
	font-size: 11px;
	font-weight: 550;
	color: var(--muted);
	padding: 1px 5px;
	border-radius: 4px;
	background: color-mix(in srgb, var(--text) 6%, transparent);
}

.reasoning-arrow {
	color: var(--muted-soft);
	transition:
		transform 0.14s ease,
		color 0.14s ease;
}

.model-row:hover .reasoning-arrow,
.model-row.hovered .reasoning-arrow {
	color: var(--text);
	transform: translateX(1px);
}

.model-row-check {
	color: var(--text-strong);
	visibility: hidden;
	flex: none;
}

.model-row-check.visible {
	visibility: visible;
}

/* ===== 悬停展开的思考等级子菜单（图三右侧飞出菜单） ===== */
.thinking-flyout {
	position: absolute;
	width: 136px;
	max-height: min(290px, calc(100vh - 32px));
	overflow-y: auto;
	padding: 5px;
	border: 1px solid var(--border);
	border-radius: 10px;
	background: var(--panel);
	box-shadow:
		0 4px 16px rgba(var(--shadow-ink), 0.08),
		0 16px 32px rgba(var(--shadow-ink), 0.14);
	z-index: 60;
}

.thinking-flyout::-webkit-scrollbar {
	width: 4px;
}

.thinking-flyout::-webkit-scrollbar-thumb {
	background: var(--border-strong);
	border-radius: 99px;
}

/* 当整个 popover 靠右对齐时，向左侧展开（防溢出） */
.model-popover--end .thinking-flyout {
	left: auto;
	right: calc(100% + 6px);
	transform-origin: top right;
	animation: flyout-left 0.14s cubic-bezier(0.16, 1, 0.3, 1);
}

.model-popover--up.model-popover--end .thinking-flyout {
	transform-origin: bottom right;
}

/* 当靠左对齐时，向右侧展开 */
.model-popover--start .thinking-flyout {
	left: calc(100% + 6px);
	right: auto;
	transform-origin: top left;
	animation: flyout-right 0.14s cubic-bezier(0.16, 1, 0.3, 1);
}

.model-popover--up.model-popover--start .thinking-flyout {
	transform-origin: bottom left;
}

@keyframes flyout-left {
	from {
		opacity: 0;
		transform: translateX(4px) scale(0.98);
	}
	to {
		opacity: 1;
		transform: translateX(0) scale(1);
	}
}

@keyframes flyout-right {
	from {
		opacity: 0;
		transform: translateX(-4px) scale(0.98);
	}
	to {
		opacity: 1;
		transform: translateX(0) scale(1);
	}
}

.thinking-flyout-head {
	font-size: 10.5px;
	font-weight: 650;
	letter-spacing: 0.03em;
	text-transform: uppercase;
	color: var(--muted);
	padding: 3px 7px 4px;
}

.thinking-item {
	display: flex;
	align-items: center;
	justify-content: space-between;
	width: 100%;
	height: 28px;
	padding: 0 8px;
	border: 0;
	border-radius: 6px;
	background: transparent;
	color: var(--text);
	font-size: 12.5px;
	cursor: pointer;
	transition: background 0.12s ease;
	text-align: left;
}

.thinking-item:hover {
	background: var(--panel-soft);
}

.thinking-item.active {
	background: var(--panel-soft);
	color: var(--text-strong);
	font-weight: 600;
}

.thinking-check {
	color: var(--text-strong);
	flex: none;
}
</style>
