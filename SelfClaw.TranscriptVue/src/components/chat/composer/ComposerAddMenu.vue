<script setup>
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import {
	BookOpenCheck,
	Bot,
	Check,
	ChevronRight,
	ImagePlus,
	LoaderCircle,
	Plus,
	SlidersHorizontal,
} from 'lucide-vue-next';
import { isSuperseded, useHostBridge } from '../../../composables/hostBridge.js';

// 输入框工具栏最左侧的「+」入口：包含添加图片、插入技能、代理助手选择与功能设置。
// 插入技能与代理选择采用与模型选择一致的 hover 展开侧边子菜单体验。
const props = defineProps({
	// 仅 Direct 模式下可插入技能
	skillsEnabled: { type: Boolean, default: false },
	agentId: { type: String, default: '' },
	agentName: { type: String, default: '' },
	selectedAgentId: { type: String, default: '' },
	selectedAgentName: { type: String, default: '' },
	capabilityRevision: { type: Number, default: 0 },
	// 图片入口要同时满足「Direct 模式」与「所选模型多模态」；禁用原因由上层算好传进来。
	imagesEnabled: { type: Boolean, default: false },
	imagesDisabledReason: { type: String, default: '当前模型不支持图片输入' },
});
const emit = defineEmits(['select-skill', 'add-image']);

const { requestLatest, post } = useHostBridge();

const open = ref(false);
const rootRef = ref(null);
const activeFlyout = ref(null); // 'skills' | 'agents' | null
let flyoutLeaveTimer = null;

// 代理信息与状态
const effectiveAgentId = computed(() => props.selectedAgentId || props.agentId || '');
const effectiveAgentName = computed(() => props.selectedAgentName || props.agentName || '');

const agents = ref([]);
const agentsLoaded = ref(false);
const agentsLoadError = ref('');

async function requestAgents() {
	try {
		const payload = await requestLatest('composer-agents', 'agents/list-composer-agents');
		agents.value = (Array.isArray(payload?.agents) ? payload.agents : [])
			.filter((agent) => agent?.id)
			.map((agent) => ({
				id: agent.id,
				name: agent.name || agent.id,
				mode: agent.mode === 'cli' ? 'cli' : 'direct',
				description: typeof agent.description === 'string' ? agent.description : '',
				isBuiltIn: Boolean(agent.isBuiltIn),
				warnings: Array.isArray(agent.warnings) ? agent.warnings : [],
			}));
		agentsLoadError.value = payload?.error ? `代理同步失败：${payload.error}` : '';
		agentsLoaded.value = true;
	} catch (error) {
		if (isSuperseded(error)) return;
		agentsLoadError.value = `代理同步失败：${error?.message || error}`;
		agentsLoaded.value = true;
	}
}

const selectedAgent = computed(() =>
	agents.value.find((agent) => agent.id === effectiveAgentId.value) || null);

const currentAgentDisplayName = computed(() =>
	effectiveAgentName.value
	|| selectedAgent.value?.name
	|| effectiveAgentId.value
	|| (agentsLoaded.value ? '未选择' : '加载中…'));

const agentMenuLabel = computed(() => `代理助手 · ${currentAgentDisplayName.value}`);

function pickAgent(agent) {
	if (agent.id === effectiveAgentId.value) {
		close();
		return;
	}

	post({ type: 'select-agent', agentId: agent.id });
	close();
}

// 技能信息与状态
const skillsCache = new Map();
const skills = ref([]);
const skillsLoading = ref(false);
const skillsError = ref('');

async function loadSkills() {
	const cacheKey = `${effectiveAgentId.value}:${props.capabilityRevision}`;
	if (skillsCache.has(cacheKey)) {
		skills.value = skillsCache.get(cacheKey);
		skillsError.value = '';
		return;
	}

	skillsLoading.value = true;
	skillsError.value = '';
	try {
		const response = await requestLatest(
			'effective-skills',
			'extensions/list-effective-skills',
			{ agentId: effectiveAgentId.value || null },
		);
		const next = Array.isArray(response?.skills) ? response.skills : [];
		skillsCache.set(cacheKey, next);
		skills.value = next;
	} catch (cause) {
		if (!isSuperseded(cause)) skillsError.value = cause?.message || '无法加载技能。';
	} finally {
		skillsLoading.value = false;
	}
}

function pickSkill(skill) {
	emit('select-skill', skill.id);
	close();
}

// 菜单与 hover flyout 管理
function toggle() {
	open.value = !open.value;
	if (!open.value) {
		closeFlyout();
	}
}

function close() {
	open.value = false;
	closeFlyout();
}

function closeFlyout() {
	activeFlyout.value = null;
	if (flyoutLeaveTimer) {
		clearTimeout(flyoutLeaveTimer);
		flyoutLeaveTimer = null;
	}
}

function onRowHover(flyoutName) {
	if (flyoutLeaveTimer) {
		clearTimeout(flyoutLeaveTimer);
		flyoutLeaveTimer = null;
	}
	activeFlyout.value = flyoutName;
	if (flyoutName === 'skills') {
		loadSkills();
	} else if (flyoutName === 'agents') {
		if (!agentsLoaded.value) {
			requestAgents();
		}
	}
}

function onRowLeave() {
	if (flyoutLeaveTimer) {
		clearTimeout(flyoutLeaveTimer);
	}
	flyoutLeaveTimer = setTimeout(() => {
		activeFlyout.value = null;
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

function openSkills() {
	onRowHover('skills');
}

function openAgents() {
	onRowHover('agents');
}

function openImages() {
	if (!props.imagesEnabled) {
		return;
	}

	close();
	emit('add-image');
}

function onDocumentPointerDown(event) {
	if (open.value && !rootRef.value?.contains(event.target)) {
		close();
	}
}

function onKeydown(event) {
	if (event.key === 'Escape') {
		close();
	}
}

onMounted(() => {
	document.addEventListener('pointerdown', onDocumentPointerDown);
	document.addEventListener('keydown', onKeydown);
});

onBeforeUnmount(() => {
	if (flyoutLeaveTimer) {
		clearTimeout(flyoutLeaveTimer);
		flyoutLeaveTimer = null;
	}
	document.removeEventListener('pointerdown', onDocumentPointerDown);
	document.removeEventListener('keydown', onKeydown);
});

watch(() => effectiveAgentId.value, () => {
	if (activeFlyout.value === 'skills') {
		loadSkills();
	}
});
</script>

<template>
	<div ref="rootRef" class="add-wrap">
		<button
			class="add-trigger"
			type="button"
			title="添加"
			aria-label="添加"
			aria-haspopup="menu"
			:aria-expanded="open ? 'true' : 'false'"
			@click="toggle"
		>
			<Plus :size="17" :stroke-width="1.9" aria-hidden="true" />
		</button>

		<div v-show="open" class="add-menu" role="menu" aria-label="添加">
			<!-- 1. 添加图片 -->
			<button
				class="add-item"
				type="button"
				role="menuitem"
				:disabled="!props.imagesEnabled"
				:title="props.imagesEnabled ? '添加图片' : props.imagesDisabledReason"
				@mouseenter="onRowLeave"
				@click="openImages"
			>
				<div class="add-item-left">
					<ImagePlus :size="15" :stroke-width="1.8" aria-hidden="true" />
					<span>添加图片</span>
				</div>
			</button>

			<!-- 2. 插入技能 (仅 Direct 模式显示) -->
			<button
				v-if="props.skillsEnabled"
				class="add-item"
				:class="{ 'has-flyout-active': activeFlyout === 'skills' }"
				type="button"
				role="menuitem"
				@mouseenter="onRowHover('skills')"
				@mouseleave="onRowLeave"
				@click.stop="openSkills"
			>
				<div class="add-item-left">
					<BookOpenCheck :size="15" :stroke-width="1.8" aria-hidden="true" />
					<span>插入技能</span>
				</div>
				<ChevronRight :size="12" class="menu-arrow" aria-hidden="true" />
			</button>

			<!-- 3. 代理助手 · build (放到功能设置上方) -->
			<button
				class="add-item"
				:class="{ 'has-flyout-active': activeFlyout === 'agents' }"
				type="button"
				role="menuitem"
				@mouseenter="onRowHover('agents')"
				@mouseleave="onRowLeave"
				@click.stop="openAgents"
			>
				<div class="add-item-left">
					<Bot :size="15" :stroke-width="1.8" aria-hidden="true" />
					<span class="agent-label-text">{{ agentMenuLabel }}</span>
				</div>
				<ChevronRight :size="12" class="menu-arrow" aria-hidden="true" />
			</button>

			<!-- 4. 功能设置 -->
			<button
				class="add-item"
				type="button"
				role="menuitem"
				@mouseenter="onRowLeave"
				@click="close"
			>
				<div class="add-item-left">
					<SlidersHorizontal :size="15" :stroke-width="1.8" aria-hidden="true" />
					<span>功能设置</span>
				</div>
			</button>

			<!-- 技能飞出子菜单 -->
			<div
				v-if="props.skillsEnabled && activeFlyout === 'skills'"
				class="flyout-panel skills-flyout"
				role="region"
				aria-label="技能列表"
				@mouseenter="onFlyoutEnter"
				@mouseleave="onFlyoutLeave"
			>
				<div class="flyout-header">
					<div class="flyout-title-wrap">
						<strong>技能</strong>
						<span v-if="effectiveAgentName || effectiveAgentId" class="flyout-subtitle">
							{{ effectiveAgentName || effectiveAgentId }}
						</span>
					</div>
					<LoaderCircle v-if="skillsLoading" :size="13" class="spin" aria-hidden="true" />
				</div>
				<p v-if="skillsError" class="flyout-message flyout-message--error">{{ skillsError }}</p>
				<p v-else-if="!skillsLoading && skills.length === 0" class="flyout-message">当前 Agent 没有可用技能</p>
				<div v-else class="flyout-scroll-list" role="listbox" aria-label="技能">
					<button
						v-for="skill in skills"
						:key="skill.id"
						type="button"
						class="skill-flyout-item"
						@click.stop="pickSkill(skill)"
					>
						<div class="skill-item-head">
							<span class="skill-item-name">{{ skill.name }}</span>
							<code class="skill-item-id">{{ skill.id }}</code>
						</div>
						<small v-if="skill.description" class="skill-item-desc">{{ skill.description }}</small>
					</button>
				</div>
			</div>

			<!-- 代理飞出子菜单 -->
			<div
				v-if="activeFlyout === 'agents'"
				class="flyout-panel agents-flyout"
				role="region"
				aria-label="代理选择"
				@mouseenter="onFlyoutEnter"
				@mouseleave="onFlyoutLeave"
			>
				<div class="flyout-header">
					<div class="flyout-title-wrap">
						<strong>代理</strong>
					</div>
					<LoaderCircle v-if="!agentsLoaded" :size="13" class="spin" aria-hidden="true" />
				</div>
				<p v-if="!agentsLoaded" class="flyout-message">正在读取代理定义…</p>
				<p v-else-if="agentsLoadError" class="flyout-message flyout-message--error">{{ agentsLoadError }}</p>
				<p v-else-if="!agents.length" class="flyout-message">未发现代理定义，请在「设置 → 代理助手」新建。</p>
				<div v-else class="flyout-scroll-list" role="radiogroup" aria-label="代理">
					<button
						v-for="agent in agents"
						:key="agent.id"
						type="button"
						class="agent-flyout-item"
						role="radio"
						:aria-checked="effectiveAgentId === agent.id ? 'true' : 'false'"
						:class="{ active: effectiveAgentId === agent.id }"
						:title="agent.description || agent.name"
						@click.stop="pickAgent(agent)"
					>
						<span class="agent-item-glyph" aria-hidden="true">
							<Bot :size="13" :stroke-width="2" />
						</span>
						<span class="agent-item-copy">
							<span class="agent-item-name">{{ agent.name }}</span>
							<span v-if="agent.description" class="agent-item-desc">{{ agent.description }}</span>
							<span v-else-if="agent.warnings?.length" class="agent-item-desc agent-item-desc--error">
								{{ agent.warnings[0] }}
							</span>
						</span>
						<span class="agent-item-mode">
							{{ agent.mode === 'cli' ? 'CLI' : 'Direct' }}
						</span>
						<Check
							class="agent-item-check"
							:class="{ visible: effectiveAgentId === agent.id }"
							:size="14"
							:stroke-width="2.4"
							aria-hidden="true"
						/>
					</button>
				</div>
			</div>
		</div>
	</div>
</template>

<style scoped>
.add-wrap {
	position: relative;
	display: inline-flex;
}

.add-trigger {
	display: inline-flex;
	align-items: center;
	justify-content: center;
	width: 30px;
	height: 30px;
	padding: 0;
	border: 0;
	border-radius: 8px;
	background: transparent;
	color: var(--muted);
	cursor: pointer;
	transition: background 0.15s, color 0.15s;
}

.add-trigger:hover,
.add-trigger[aria-expanded='true'] {
	background: var(--panel-muted);
	color: var(--text);
}

.add-menu {
	position: absolute;
	left: 0;
	bottom: calc(100% + 6px);
	width: 204px;
	padding: 4px;
	border: 1px solid var(--border);
	border-radius: 10px;
	background: var(--panel);
	box-shadow: 0 1px 2px rgba(var(--shadow-ink), 0.05), 0 12px 32px rgba(var(--shadow-ink), 0.12);
	z-index: 40;
	transform-origin: bottom left;
	animation: add-menu-in 0.16s cubic-bezier(0.16, 1, 0.3, 1);
}

@keyframes add-menu-in {
	from {
		opacity: 0;
		transform: translateY(6px) scale(0.98);
	}

	to {
		opacity: 1;
		transform: translateY(0) scale(1);
	}
}

@media (prefers-reduced-motion: reduce) {
	.add-menu {
		animation: none;
	}
}

.add-item {
	display: flex;
	align-items: center;
	justify-content: space-between;
	gap: 8px;
	width: 100%;
	height: 32px;
	padding: 0 8px;
	border: 0;
	border-radius: 7px;
	background: transparent;
	color: var(--text);
	font-size: var(--fs-125);
	font-weight: 500;
	text-align: left;
	cursor: pointer;
	transition: background 0.12s;
}

.add-item-left {
	display: flex;
	align-items: center;
	gap: 9px;
	min-width: 0;
	flex: 1;
}

.add-item-left svg {
	color: var(--muted);
	flex: none;
}

.agent-label-text {
	overflow: hidden;
	text-overflow: ellipsis;
	white-space: nowrap;
}

.menu-arrow {
	color: var(--muted-soft);
	flex: none;
	transition: transform 0.14s ease, color 0.14s ease;
}

.add-item:hover:not(:disabled),
.add-item.has-flyout-active {
	background: var(--panel-soft);
}

.add-item:hover .menu-arrow,
.add-item.has-flyout-active .menu-arrow {
	color: var(--text);
	transform: translateX(1px);
}

.add-item:disabled {
	opacity: 0.42;
	cursor: default;
}

.add-item:disabled svg {
	color: var(--muted-soft);
}

/* ===== 侧边悬停飞出子菜单面板 ===== */
.flyout-panel {
	position: absolute;
	left: calc(100% + 6px);
	bottom: 0;
	border: 1px solid var(--border);
	border-radius: 10px;
	background: var(--panel);
	box-shadow:
		0 4px 16px rgba(var(--shadow-ink), 0.08),
		0 16px 32px rgba(var(--shadow-ink), 0.14);
	z-index: 60;
	transform-origin: bottom left;
	animation: flyout-right 0.14s cubic-bezier(0.16, 1, 0.3, 1);
	overflow: hidden;
}

/* 跨越 6px 间隙的安全过渡区 */
.flyout-panel::before {
	content: '';
	position: absolute;
	top: 0;
	bottom: 0;
	left: -8px;
	width: 8px;
}

.skills-flyout {
	width: min(290px, calc(100vw - 32px));
}

.agents-flyout {
	width: 256px;
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

@media (prefers-reduced-motion: reduce) {
	.flyout-panel {
		animation: none;
	}
}

.flyout-header {
	display: flex;
	align-items: center;
	justify-content: space-between;
	gap: 8px;
	padding: 8px 10px 7px;
	border-bottom: 1px solid var(--border);
	background: color-mix(in srgb, var(--panel) 92%, var(--bg-canvas));
}

.flyout-title-wrap {
	display: flex;
	flex-direction: column;
	min-width: 0;
}

.flyout-title-wrap strong {
	color: var(--text-strong);
	font-size: var(--fs-12);
	font-weight: 600;
	line-height: 1.25;
}

.flyout-subtitle {
	margin-top: 1px;
	color: var(--muted-soft);
	font-size: var(--fs-10);
	overflow: hidden;
	text-overflow: ellipsis;
	white-space: nowrap;
}

.flyout-scroll-list {
	max-height: 250px;
	overflow-y: auto;
	overscroll-behavior: contain;
	padding: 4px;
	display: grid;
	gap: 2px;
}

.flyout-scroll-list::-webkit-scrollbar {
	width: 4px;
}

.flyout-scroll-list::-webkit-scrollbar-thumb {
	background: var(--border-strong);
	border-radius: 99px;
}

.flyout-message {
	margin: 0;
	padding: 16px 12px;
	color: var(--muted-soft);
	font-size: var(--fs-11);
	text-align: center;
	line-height: 1.45;
}

.flyout-message--error {
	color: var(--err-text);
}

/* 技能列表项 */
.skill-flyout-item {
	display: grid;
	width: 100%;
	gap: 2px;
	padding: 7px 9px;
	border: 0;
	border-radius: 6px;
	background: transparent;
	text-align: left;
	cursor: pointer;
	transition: background 0.12s;
}

.skill-flyout-item:hover {
	background: var(--panel-soft);
}

.skill-item-head {
	display: flex;
	align-items: baseline;
	justify-content: space-between;
	gap: 8px;
	min-width: 0;
}

.skill-item-name {
	overflow: hidden;
	color: var(--text);
	font-size: var(--fs-12);
	font-weight: 550;
	text-overflow: ellipsis;
	white-space: nowrap;
}

.skill-item-id {
	color: var(--muted);
	font-family: var(--font-mono);
	font-size: 9.5px;
	flex: none;
}

.skill-item-desc {
	overflow: hidden;
	color: var(--muted);
	font-size: 10.5px;
	line-height: 1.35;
	text-overflow: ellipsis;
	white-space: nowrap;
}

/* 代理列表项 */
.agent-flyout-item {
	display: flex;
	align-items: center;
	gap: 9px;
	width: 100%;
	padding: 7px 9px;
	border: 0;
	border-radius: 6px;
	background: transparent;
	color: var(--text);
	text-align: left;
	cursor: pointer;
	transition: background 0.12s ease;
}

.agent-flyout-item:hover,
.agent-flyout-item.active {
	background: var(--panel-soft);
}

.agent-item-glyph {
	display: inline-flex;
	align-items: center;
	justify-content: center;
	width: 18px;
	flex: none;
	color: var(--muted);
}

.agent-item-copy {
	flex: 1;
	min-width: 0;
	display: grid;
	gap: 1px;
}

.agent-item-name {
	overflow: hidden;
	text-overflow: ellipsis;
	white-space: nowrap;
	font-size: 12.5px;
	font-weight: 550;
	color: var(--text);
	line-height: 1.25;
}

.agent-item-desc {
	overflow: hidden;
	text-overflow: ellipsis;
	white-space: nowrap;
	font-size: 10.5px;
	color: var(--muted);
	line-height: 1.35;
}

.agent-item-desc--error {
	color: var(--err-text);
}

.agent-item-mode {
	flex: none;
	font-size: 10px;
	font-weight: 500;
	padding: 1px 5px;
	border-radius: 4px;
	color: var(--muted-soft);
	background: color-mix(in srgb, var(--text) 5%, transparent);
}

.agent-item-check {
	color: var(--text-strong);
	visibility: hidden;
	flex: none;
}

.agent-item-check.visible {
	visibility: visible;
}

.spin {
	animation: spin 0.8s linear infinite;
	color: var(--muted);
}

@keyframes spin {
	to {
		transform: rotate(360deg);
	}
}
</style>
