<script setup>
import { ref, computed, onMounted, onBeforeUnmount } from 'vue';
import claudeIcon from '../../../assets/agents-icons/claude.svg';
import codexIcon from '../../../assets/agents-icons/codex.svg';
import opencodeIcon from '../../../assets/agents-icons/opencode.svg';

/**
 * 模型选择器（药丸按钮 + 设置弹出面板）
 * 「代理」列表与设置页的「编程助手」共享同一后端状态：
 * 打开时通过 get-programming-assistant-settings 读取扫描结果，
 * 选中代理通过 select-programming-cli 持久化，下一次发送回合即生效。
 */

const emit = defineEmits(['update:mode', 'update:agent', 'update:model']);

// 模式：本地 CLI / 自带 Key（自带 Key 尚未接入，仅样式占位）
const mode = ref('local');

const defaultModel = 'Default (CLI config)';

// 已知 CLI 的展示图标：与设置页「编程助手」共用同一组 SVG 资源；未知 id 走 fallback 线条图形。
const agentPresentation = {
	claude: { iconSrc: claudeIcon, iconBackground: '#ffffff' },
	codex: { iconSrc: codexIcon, iconBackground: '#ffffff' },
	opencode: { iconSrc: opencodeIcon, iconBackground: '#ffffff' },
};

const detectedAgents = ref([]);
const selectedCliId = ref('');
const loaded = ref(false);
const loadError = ref('');
let requestCounter = 0;

const selectedAgent = computed(() => detectedAgents.value.find((agent) => agent.id === selectedCliId.value) || null);

// 模型下拉：来自选中 CLI 的模型列表（当前后端只提供 Default）。
const models = computed(() => selectedAgent.value?.models?.length ? selectedAgent.value.models : [defaultModel]);
const activeModel = ref(defaultModel);

// 展开状态
const open = ref(false);
const menuOpen = ref(false);
const rootRef = ref(null);

const modelLabel = computed(() => {
	if (!loaded.value) {
		return '加载中…';
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

function applySettings(payload) {
	const rawTools = Array.isArray(payload?.tools) ? payload.tools : [];
	detectedAgents.value = rawTools
		.filter((tool) => tool?.id)
		.map((tool) => ({
			id: tool.id,
			name: tool.name || tool.id,
			models: Array.isArray(tool.models) ? tool.models.filter((m) => typeof m === 'string' && m.trim()) : [],
			...(agentPresentation[tool.id] || { glyph: 'open', tint: '#eef0f3', ink: '#171a1f' }),
		}));

	const normalized = typeof payload?.selectedCliId === 'string' ? payload.selectedCliId.trim().toLowerCase() : '';
	selectedCliId.value = detectedAgents.value.some((agent) => agent.id === normalized) ? normalized : '';
	if (!models.value.includes(activeModel.value)) {
		activeModel.value = models.value[0] || defaultModel;
	}
	loadError.value = payload?.error ? `CLI 设置同步失败：${payload.error}` : '';
	loaded.value = true;
}

function onHostMessage(event) {
	const payload = event?.data;
	if (payload?.type === 'programming-assistant-settings') {
		applySettings(payload);
	}
}

function togglePanel() {
	open.value = !open.value;
	if (!open.value) {
		menuOpen.value = false;
		return;
	}

	if (!loaded.value) {
		// 自愈：挂载时的请求若丢失（宿主未就绪等），打开面板时再拉一次配置。
		requestSettings();
	}
}
function closePanel() {
	open.value = false;
	menuOpen.value = false;
}

function pickMode(m) {
	mode.value = m;
	emit('update:mode', m);
}

function pickAgent(agent) {
	if (agent.id === selectedCliId.value) {
		return;
	}

	selectedCliId.value = agent.id;
	activeModel.value = agent.models?.[0] || defaultModel;
	emit('update:agent', agent.id);
	postToHost({
		type: 'select-programming-cli',
		requestId: `composer-cli-${Date.now()}-${++requestCounter}`,
		cliId: agent.id,
	});
}

function toggleMenu() {
	menuOpen.value = !menuOpen.value;
}
function pickModel(m) {
	activeModel.value = m;
	menuOpen.value = false;
	emit('update:model', m);
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
	requestSettings();
});
onBeforeUnmount(() => {
	document.removeEventListener('click', onDocClick);
	document.removeEventListener('keydown', onKeydown);
	window.chrome?.webview?.removeEventListener('message', onHostMessage);
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
			<span class="model-badge" :class="{ 'model-badge--brand': !!selectedAgent?.iconSrc }" aria-hidden="true">
				<img v-if="selectedAgent?.iconSrc" class="model-badge-img" :src="selectedAgent.iconSrc" alt="" />
				<svg v-else viewBox="0 0 24 24" fill="currentColor">
					<path d="M12 2.6a3.1 3.1 0 0 1 2.83 1.84 3.1 3.1 0 0 1 3.9 3.9 3.1 3.1 0 0 1 0 5.32 3.1 3.1 0 0 1-3.9 3.9 3.1 3.1 0 0 1-5.66 0 3.1 3.1 0 0 1-3.9-3.9 3.1 3.1 0 0 1 0-5.32 3.1 3.1 0 0 1 3.9-3.9A3.1 3.1 0 0 1 12 2.6Zm0 3.4a6 6 0 1 0 0 12 6 6 0 0 0 0-12Z" />
				</svg>
			</span>
			<span class="model-name">{{ modelLabel }}</span>
			<svg class="model-caret" viewBox="0 0 20 20" fill="none" stroke="currentColor"
				stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
				<path d="M6 8l4 4 4-4" />
			</svg>
		</button>

		<!-- 设置弹出面板：固定贴着触发按钮下方展开 -->
		<div v-show="open" class="model-popover" role="dialog" aria-label="模型与代理设置">
			<!-- 模式 -->
			<div class="pop-section">
				<div class="pop-label">模式</div>
				<div class="seg" role="group" aria-label="模式">
					<button type="button" :aria-pressed="mode === 'local' ? 'true' : 'false'" @click="pickMode('local')">本地 CLI</button>
					<button type="button" :aria-pressed="mode === 'key' ? 'true' : 'false'" @click="pickMode('key')">自带 Key</button>
				</div>
			</div>

			<!-- 代理 -->
			<div class="pop-section">
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
							<svg v-else viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="4" y="4" width="16" height="16" rx="2" /></svg>
						</span>
						<span class="agent-name">{{ agent.name }}</span>
						<svg class="agent-check" viewBox="0 0 20 20" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M4 10.5l4 4 8-9" /></svg>
					</button>
				</div>
			</div>

			<!-- 模型 -->
			<div class="pop-section">
				<div class="pop-label">模型</div>
				<div class="model-select">
					<button
						type="button"
						class="model-select-btn"
						:aria-expanded="menuOpen ? 'true' : 'false'"
						aria-haspopup="listbox"
						@click.stop="toggleMenu"
					>
						<span>{{ activeModel }}</span>
						<svg class="caret" viewBox="0 0 20 20" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M6 8l4 4 4-4" /></svg>
					</button>
					<div v-show="menuOpen" class="model-menu" role="listbox" aria-label="模型列表">
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
							<svg class="tick" viewBox="0 0 20 20" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M4 10.5l4 4 8-9" /></svg>
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
	background: #171a1f;
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
	top: calc(100% + 6px);
	width: 236px;
	padding: 10px;
	border: 1px solid #e2e5eb;
	border-radius: 12px;
	background: #ffffff;
	box-shadow: 0 1px 2px rgba(23, 26, 31, 0.05), 0 12px 32px rgba(23, 26, 31, 0.12);
	transform-origin: top left;
	z-index: 40;
	animation: pop-in-down 0.16s cubic-bezier(0.16, 1, 0.3, 1);
}
@keyframes pop-in-down {
	from { opacity: 0; transform: translateY(-6px) scale(0.98); }
	to   { opacity: 1; transform: translateY(0) scale(1); }
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
	background: #fbeee9;
	border-color: #f0d2c6;
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
	color: #b1502f;
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
.model-select-btn:hover { background: #f7f8fa; }
/* 菜单在右侧展开，箭头指向右以示意 */
.model-select-btn .caret {
	width: 13px;
	height: 13px;
	color: #6b7280;
	flex: none;
	transform: rotate(-90deg);
}

/* 模型菜单：贴着选择框右侧展开、底边对齐向上生长，避免被窗口下缘遮挡 */
.model-menu {
	position: absolute;
	left: calc(100% + 8px);
	bottom: 0;
	width: max-content;
	min-width: 170px;
	max-width: 240px;
	padding: 4px;
	border: 1px solid #e2e5eb;
	border-radius: 9px;
	background: #ffffff;
	box-shadow: 0 1px 2px rgba(23, 26, 31, 0.05), 0 12px 32px rgba(23, 26, 31, 0.12);
	z-index: 3;
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
</style>
