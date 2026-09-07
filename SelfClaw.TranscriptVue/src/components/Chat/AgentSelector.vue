<script setup>
import { ref, computed, nextTick, onMounted, onBeforeUnmount, watch } from 'vue';
import { ChevronDown, Check, Bot } from 'lucide-vue-next';
import { useHostBridge, isSuperseded } from '../../composables/hostBridge.js';

/**
 * 代理切换器（药丸按钮 + 弹出列表）
 *
 * 列出 `agents/*.md` 中定义的 Desktop Agent（直连/CLI 两类）。当前选中态来自
 * transcript 推送的 selectedAgentId/selectedAgentName，切换则经 select-agent
 * shell intent 回写到 MainWindowViewModel：选定值只对新会话生效，已有会话
 * 的能力/skill/mcp/提示词仍由该会话创建时绑定的 agent id 决定（不可变）。
 */

const props = defineProps({
	selectedAgentId: { type: String, default: '' },
	selectedAgentName: { type: String, default: '' },
});

const { requestLatest, post } = useHostBridge();

const agents = ref([]);
const loaded = ref(false);
const loadError = ref('');

const open = ref(false);
const rootRef = ref(null);
const popoverRef = ref(null);

// 弹层方向：与 ModelSelector 同一策略，输入栏在窗口底部，下方空间不足时翻向上方。
const placement = ref('down');
const estimatedPopoverHeight = 280;

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

const selectedAgent = computed(() =>
	agents.value.find((agent) => agent.id === props.selectedAgentId) || null);

const label = computed(() => {
	if (!loaded.value) {
		return '加载中…';
	}
	return props.selectedAgentName || selectedAgent.value?.name || '未选择代理';
});

async function requestAgents() {
	try {
		const payload = await requestLatest('composer-agents', 'agents/list-composer-agents');
		agents.value = (Array.isArray(payload.agents) ? payload.agents : [])
			.filter((agent) => agent?.id)
			.map((agent) => ({
				id: agent.id,
				name: agent.name || agent.id,
				mode: agent.mode === 'cli' ? 'cli' : 'direct',
				description: typeof agent.description === 'string' ? agent.description : '',
				isBuiltIn: Boolean(agent.isBuiltIn),
				warnings: Array.isArray(agent.warnings) ? agent.warnings : [],
			}));
		loadError.value = payload.error ? `代理同步失败：${payload.error}` : '';
		loaded.value = true;
	} catch (error) {
		if (isSuperseded(error)) return;
		loadError.value = `代理同步失败：${error?.message || error}`;
		loaded.value = true;
	}
}

function togglePanel() {
	open.value = !open.value;
	if (!open.value) {
		return;
	}

	updatePlacement();
	nextTick(updatePlacement);

	if (!loaded.value) {
		requestAgents();
	}
}

function closePanel() {
	open.value = false;
}

function pickAgent(agent) {
	if (agent.id === props.selectedAgentId) {
		closePanel();
		return;
	}

	post({ type: 'select-agent', agentId: agent.id });
	closePanel();
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

// 代理定义落盘后（agents/get-state 等改动），下一次打开会重新拉取；这里仅在外部选中态
// 变化时关闭弹层，避免选中态回推后列表与按钮错位。
watch(() => props.selectedAgentId, () => {
	if (open.value && !selectedAgent.value) {
		// 当前选中代理不在已加载列表里（列表尚未拉取或已被删除）：关闭弹层，下次打开重拉。
		closePanel();
	}
});
</script>

<template>
	<div ref="rootRef" class="agent-wrap">
		<button class="composer-agent" type="button" :aria-expanded="open ? 'true' : 'false'"
			aria-haspopup="true" title="代理选择" @click.stop="togglePanel">
			<span class="agent-badge" aria-hidden="true">
				<Bot :size="12" :stroke-width="2" />
			</span>
			<span class="agent-name">{{ label }}</span>
			<ChevronDown class="agent-caret" :size="13" :stroke-width="2" aria-hidden="true" />
		</button>

		<div v-show="open" ref="popoverRef" class="agent-popover" :class="`agent-popover--${placement}`"
			role="dialog" aria-label="代理选择">
			<div class="pop-label">代理</div>
			<div v-if="!loaded" class="agent-hint">正在读取代理定义…</div>
			<div v-else-if="loadError" class="agent-hint agent-hint--error">{{ loadError }}</div>
			<div v-else-if="!agents.length" class="agent-hint">
				未发现代理定义，请在「设置 → 代理助手」新建。
			</div>
			<div v-else class="agent-list" role="radiogroup" aria-label="代理">
				<button v-for="agent in agents" :key="agent.id" type="button" class="agent-item" role="radio"
					:aria-checked="selectedAgentId === agent.id ? 'true' : 'false'" :title="agent.description || agent.name"
					@click="pickAgent(agent)">
					<span class="agent-item-glyph" :class="`agent-item-glyph--${agent.mode}`" aria-hidden="true">
						<Bot :size="13" :stroke-width="2" />
					</span>
					<span class="agent-item-copy">
						<span class="agent-item-name">{{ agent.name }}</span>
						<span v-if="agent.description" class="agent-item-desc">{{ agent.description }}</span>
						<span v-else-if="agent.warnings.length" class="agent-item-desc agent-item-desc--error">
							{{ agent.warnings[0] }}
						</span>
					</span>
					<span class="agent-item-mode" :class="`agent-item-mode--${agent.mode}`">
						{{ agent.mode === 'cli' ? 'CLI' : 'Direct' }}
					</span>
					<Check class="agent-item-check" :size="14" :stroke-width="2.4" aria-hidden="true" />
				</button>
			</div>
		</div>
	</div>
</template>

<style scoped>
.agent-wrap {
	position: relative;
	display: inline-flex;
}

.composer-agent {
	display: inline-flex;
	align-items: center;
	gap: 7px;
	height: 32px;
	padding: 0 8px;
	border: 1px solid var(--border);
	border-radius: 9px;
	background: var(--panel);
	color: var(--text);
	font-size: var(--fs-125);
	font-weight: 550;
	letter-spacing: 0.01em;
	white-space: nowrap;
	cursor: pointer;
	transition: background 0.15s, border-color 0.15s, box-shadow 0.15s;
}

.composer-agent:hover {
	background: var(--panel-soft);
}

.composer-agent[aria-expanded='true'] {
	background: var(--panel-soft);
	border-color: var(--border-strong);
	box-shadow: 0 0 0 3px rgba(var(--shadow-ink), 0.04);
}

.agent-badge {
	display: inline-grid;
	place-items: center;
	width: 18px;
	height: 18px;
	border-radius: 6px;
	background: color-mix(in srgb, var(--accent) 14%, transparent);
	color: var(--accent);
	flex: none;
}

.agent-badge svg {
	width: 12px;
	height: 12px;
}

.agent-name {
	overflow: hidden;
	text-overflow: ellipsis;
	max-width: 140px;
}

.agent-caret {
	width: 13px;
	height: 13px;
	color: var(--muted);
	flex: none;
	transition: transform 0.18s ease;
}

.composer-agent[aria-expanded='true'] .agent-caret {
	transform: rotate(180deg);
}

.agent-popover {
	position: absolute;
	left: 0;
	width: 248px;
	padding: 10px;
	border: 1px solid var(--border);
	border-radius: 12px;
	background: var(--panel);
	box-shadow: 0 1px 2px rgba(var(--shadow-ink), 0.05), 0 12px 32px rgba(var(--shadow-ink), 0.12);
	z-index: 40;
}

.agent-popover--down {
	top: calc(100% + 6px);
	transform-origin: top left;
	animation: agent-pop-in-down 0.16s cubic-bezier(0.16, 1, 0.3, 1);
}

.agent-popover--up {
	bottom: calc(100% + 6px);
	transform-origin: bottom left;
	animation: agent-pop-in-up 0.16s cubic-bezier(0.16, 1, 0.3, 1);
}

@keyframes agent-pop-in-down {
	from {
		opacity: 0;
		transform: translateY(-6px) scale(0.98);
	}

	to {
		opacity: 1;
		transform: translateY(0) scale(1);
	}
}

@keyframes agent-pop-in-up {
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

	.agent-popover--down,
	.agent-popover--up {
		animation: none;
	}
}

.pop-label {
	font-size: var(--fs-105);
	font-weight: 600;
	letter-spacing: 0.04em;
	color: var(--muted);
	margin: 2px 2px 6px;
}

.agent-hint {
	margin: 0 2px 6px;
	color: var(--muted-soft);
	font-size: var(--fs-115);
	line-height: 1.55;
}

.agent-hint--error {
	color: var(--err-text);
}

.agent-list {
	display: grid;
	gap: 5px;
	max-height: 224px;
	overflow-y: auto;
	overscroll-behavior: contain;
	padding-right: 2px;
}

.agent-list::-webkit-scrollbar {
	width: 6px;
}

.agent-list::-webkit-scrollbar-track {
	background: transparent;
}

.agent-list::-webkit-scrollbar-thumb {
	background: var(--border-strong);
	border-radius: 99px;
}

.agent-item {
	display: flex;
	align-items: center;
	gap: 8px;
	width: 100%;
	padding: 6px 8px;
	border: 1px solid var(--border);
	border-radius: 9px;
	background: var(--panel);
	color: var(--text);
	text-align: left;
	cursor: pointer;
	transition: background 0.15s, border-color 0.15s;
}

.agent-item:hover {
	background: var(--panel-soft);
}

.agent-item[aria-checked='true'] {
	background: var(--accent-soft, color-mix(in srgb, var(--accent) 8%, transparent));
	border-color: color-mix(in srgb, var(--accent) 30%, transparent);
}

.agent-item-glyph {
	display: inline-grid;
	place-items: center;
	width: 20px;
	height: 20px;
	border-radius: 5px;
	flex: none;
	background: var(--panel-soft);
	color: var(--muted);
}

.agent-item-glyph--cli {
	background: color-mix(in srgb, var(--text) 8%, transparent);
	color: var(--text-soft);
}

.agent-item-glyph--direct {
	background: color-mix(in srgb, var(--accent) 14%, transparent);
	color: var(--accent);
}

.agent-item-glyph svg {
	width: 13px;
	height: 13px;
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
	font: 550 12.5px/1.2 inherit;
	color: var(--text);
}

.agent-item-desc {
	overflow: hidden;
	text-overflow: ellipsis;
	white-space: nowrap;
	font-size: var(--fs-105);
	color: var(--muted-soft);
}

.agent-item-desc--error {
	color: var(--err-text);
}

.agent-item-mode {
	flex: none;
	font-size: var(--fs-10);
	font-weight: 600;
	letter-spacing: 0.04em;
	padding: 2px 6px;
	border-radius: 5px;
	background: var(--panel-soft);
	color: var(--muted);
}

.agent-item-mode--cli {
	background: color-mix(in srgb, var(--text) 8%, transparent);
	color: var(--text-soft);
}

.agent-item-mode--direct {
	background: color-mix(in srgb, var(--accent) 14%, transparent);
	color: var(--accent);
}

.agent-item-check {
	width: 14px;
	height: 14px;
	color: var(--accent);
	display: none;
	flex: none;
}

.agent-item[aria-checked='true'] .agent-item-check {
	display: block;
}
</style>
