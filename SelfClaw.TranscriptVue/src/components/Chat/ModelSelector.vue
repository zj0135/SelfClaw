<script setup>
import { ref, reactive, computed, onMounted, onBeforeUnmount } from 'vue';

/**
 * 模型选择器（药丸按钮 + 设置弹出面板）
 * 直接替换 ComposerPanel 中原来的 .composer-model 按钮。
 * 视觉沿用 SelfClaw composer 既有的浅色专业设计令牌。
 */

const emit = defineEmits(['update:mode', 'update:agent', 'update:model']);

// 模式：本地 CLI / 自带 Key
const mode = ref('local');

// 代理列表（对应图3内容）
const agents = reactive([
	{ id: 'AMR',         name: 'AMR',         glyph: 'amr',    tint: '#e9fbe9', ink: '#17a34a', action: '登录' },
	{ id: 'Claude Code', name: 'Claude Code', glyph: 'claude', tint: '#f4ded3', ink: '#b1502f' },
	{ id: 'Codex CLI',   name: 'Codex CLI',   glyph: 'codex',  tint: '#ecebfb', ink: '#5b57d6' },
	{ id: 'OpenCode',    name: 'OpenCode',    glyph: 'open',   tint: '#eef0f3', ink: '#171a1f' },
]);
const activeAgent = ref('Claude Code');

// 模型下拉
const models = ['Default (CLI config)', 'Claude Sonnet 4.6', 'Claude Opus 4.8', 'Kimi K2.6 Code Preview'];
const activeModel = ref('Default (CLI config)');

// 展开状态
const open = ref(false);
const menuOpen = ref(false);
const rootRef = ref(null);

const modelLabel = computed(() => activeModel.value);

function togglePanel() {
	open.value = !open.value;
	if (!open.value) menuOpen.value = false;
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
	// 「登录」动作不切换选中
	activeAgent.value = agent.id;
	emit('update:agent', agent.id);
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
});
onBeforeUnmount(() => {
	document.removeEventListener('click', onDocClick);
	document.removeEventListener('keydown', onKeydown);
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
			<span class="model-badge" aria-hidden="true">
				<svg viewBox="0 0 24 24" fill="currentColor">
					<path d="M12 2.6a3.1 3.1 0 0 1 2.83 1.84 3.1 3.1 0 0 1 3.9 3.9 3.1 3.1 0 0 1 0 5.32 3.1 3.1 0 0 1-3.9 3.9 3.1 3.1 0 0 1-5.66 0 3.1 3.1 0 0 1-3.9-3.9 3.1 3.1 0 0 1 0-5.32 3.1 3.1 0 0 1 3.9-3.9A3.1 3.1 0 0 1 12 2.6Zm0 3.4a6 6 0 1 0 0 12 6 6 0 0 0 0-12Z" />
				</svg>
			</span>
			<span class="model-name">{{ modelLabel }}</span>
			<svg class="model-caret" viewBox="0 0 20 20" fill="none" stroke="currentColor"
				stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
				<path d="M6 8l4 4 4-4" />
			</svg>
		</button>

		<!-- 设置弹出面板：贴着药丸上方，左对齐 -->
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
				<div class="agent-list" role="radiogroup" aria-label="代理">
					<button
						v-for="agent in agents"
						:key="agent.id"
						type="button"
						class="agent-item"
						role="radio"
						:aria-checked="activeAgent === agent.id ? 'true' : 'false'"
						@click="pickAgent(agent)"
					>
						<span class="agent-glyph" :style="{ background: agent.tint, color: agent.ink }" aria-hidden="true">
							<svg v-if="agent.glyph === 'amr'" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M12 3v18M5 8l7-5 7 5M5 8v8l7 5 7-5V8" /></svg>
							<svg v-else-if="agent.glyph === 'claude'" viewBox="0 0 24 24" fill="currentColor"><path d="M12 2 4 20h3.4l1.5-3.6h6.2L16.6 20H20L12 2Zm-2 11 2-4.9 2 4.9h-4Z" /></svg>
							<svg v-else-if="agent.glyph === 'codex'" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M8 8l-4 4 4 4M16 8l4 4-4 4M13 5l-2 14" /></svg>
							<svg v-else viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="4" y="4" width="16" height="16" rx="2" /></svg>
						</span>
						<span class="agent-name">{{ agent.name }}</span>
						<span v-if="agent.action" class="agent-action" @click.stop>{{ agent.action }}</span>
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

/* ===== 模型选择药丸（图2风格） ===== */
.composer-model {
	display: inline-flex;
	align-items: center;
	gap: 8px;
	height: 34px;
	padding: 0 9px 0 10px;
	border: 1px solid #e5e7eb;
	border-radius: 999px;
	background: #ffffff;
	color: #171a1f;
	font-size: 13px;
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
	width: 20px;
	height: 20px;
	border-radius: 50%;
	background: #171a1f;
	color: #fff;
	flex: none;
}
.model-badge svg { width: 13px; height: 13px; }

.model-name {
	overflow: hidden;
	text-overflow: ellipsis;
	max-width: 220px;
}
.model-caret {
	width: 14px;
	height: 14px;
	color: #6b7280;
	flex: none;
	transition: transform 0.18s ease;
}
.composer-model[aria-expanded='true'] .model-caret { transform: rotate(180deg); }

/* ===== 设置弹出面板 ===== */
.model-popover {
	position: absolute;
	bottom: calc(100% + 8px);
	left: 0;
	width: 268px;
	padding: 12px;
	border: 1px solid #e2e5eb;
	border-radius: 14px;
	background: #ffffff;
	box-shadow: 0 1px 2px rgba(23, 26, 31, 0.05), 0 12px 32px rgba(23, 26, 31, 0.12);
	transform-origin: bottom left;
	z-index: 40;
	animation: pop-in 0.16s cubic-bezier(0.16, 1, 0.3, 1);
}
@keyframes pop-in {
	from { opacity: 0; transform: translateY(6px) scale(0.98); }
	to   { opacity: 1; transform: translateY(0) scale(1); }
}

.pop-label {
	font-size: 11px;
	font-weight: 600;
	letter-spacing: 0.04em;
	color: #6b7280;
	margin: 4px 2px 7px;
}
.pop-section + .pop-section { margin-top: 14px; }

/* 模式分段控件 */
.seg {
	display: grid;
	grid-template-columns: 1fr 1fr;
	gap: 4px;
	padding: 3px;
	border: 1px solid #eef0f3;
	border-radius: 10px;
	background: #f7f8fa;
}
.seg button {
	height: 30px;
	border: 0;
	border-radius: 7px;
	background: transparent;
	color: #6b7280;
	font: 550 13px/1 inherit;
	cursor: pointer;
	transition: background 0.15s, color 0.15s, box-shadow 0.15s;
}
.seg button[aria-pressed='true'] {
	background: #ffffff;
	color: #171a1f;
	box-shadow: 0 1px 2px rgba(23, 26, 31, 0.08);
}

/* 代理列表 */
.agent-list { display: grid; gap: 6px; }
.agent-item {
	display: flex;
	align-items: center;
	gap: 10px;
	width: 100%;
	padding: 9px 10px;
	border: 1px solid #eef0f3;
	border-radius: 10px;
	background: #ffffff;
	color: #171a1f;
	font: 550 13px/1.2 inherit;
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
	width: 22px;
	height: 22px;
	border-radius: 6px;
	flex: none;
}
.agent-glyph svg { width: 15px; height: 15px; }
.agent-name { flex: 1; min-width: 0; }
.agent-action {
	font-size: 12px;
	font-weight: 550;
	color: #6b7280;
}
.agent-item[aria-checked='true'] .agent-action { color: #b1502f; }
.agent-check {
	width: 16px;
	height: 16px;
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
	height: 38px;
	padding: 0 10px 0 12px;
	border: 1px solid #e5e7eb;
	border-radius: 10px;
	background: #ffffff;
	color: #171a1f;
	font: 550 13px/1 inherit;
	cursor: pointer;
	transition: background 0.15s, border-color 0.15s;
}
.model-select-btn:hover { background: #f7f8fa; }
.model-select-btn .caret { width: 14px; height: 14px; color: #6b7280; flex: none; }

.model-menu {
	position: absolute;
	left: 0;
	right: 0;
	top: calc(100% + 5px);
	padding: 5px;
	border: 1px solid #e2e5eb;
	border-radius: 10px;
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
	padding: 8px 9px;
	border: 0;
	border-radius: 7px;
	background: transparent;
	color: #171a1f;
	font: 500 13px/1.2 inherit;
	text-align: left;
	cursor: pointer;
}
.model-opt:hover { background: #f7f8fa; }
.model-opt .tick { width: 15px; height: 15px; color: #171a1f; display: none; flex: none; }
.model-opt[aria-selected='true'] { font-weight: 600; }
.model-opt[aria-selected='true'] .tick { display: block; }
</style>
