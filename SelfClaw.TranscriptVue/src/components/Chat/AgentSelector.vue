<script setup>
import { ref, computed, nextTick, onMounted, onBeforeUnmount, watch } from 'vue';
import { Check, Bot } from 'lucide-vue-next';
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

// 代理列表是按需拉取的（首次展开面板才请求），但选中代理名由 transcript 每次推送带过来，
// 因此标签不能以列表是否加载为门槛，否则首次渲染与切换会话后会一直停在「加载中…」。
const label = computed(() =>
	props.selectedAgentName
	|| selectedAgent.value?.name
	|| (loaded.value ? '未选择代理' : '加载中…'));

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
			<Bot class="agent-icon" :size="15" :stroke-width="1.8" aria-hidden="true" />
			<span class="agent-name">{{ label }}</span>
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
					<span class="agent-item-glyph" aria-hidden="true">
						<Bot :size="13" :stroke-width="2" />
					</span>
					<span class="agent-item-copy">
						<span class="agent-item-name">{{ agent.name }}</span>
						<span v-if="agent.description" class="agent-item-desc">{{ agent.description }}</span>
						<span v-else-if="agent.warnings.length" class="agent-item-desc agent-item-desc--error">
							{{ agent.warnings[0] }}
						</span>
					</span>
					<span class="agent-item-mode">
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

/* 触发按钮：无边框「图标 + 文字���，仅悬停 / 展开时出现浅底，与工具栏其余图标按钮一致 */
.composer-agent {
	display: inline-flex;
	align-items: center;
	gap: 6px;
	height: 30px;
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
	transition: background 0.15s, color 0.15s;
}

.composer-agent:hover,
.composer-agent[aria-expanded='true'] {
	background: var(--panel-muted);
	color: var(--text);
}

.agent-icon {
	width: 15px;
	height: 15px;
	color: var(--muted);
	flex: none;
	transition: color 0.15s;
}

.composer-agent:hover .agent-icon,
.composer-agent[aria-expanded='true'] .agent-icon {
	color: var(--text);
}

.agent-name {
	overflow: hidden;
	text-overflow: ellipsis;
	max-width: 140px;
}

.agent-popover {
	position: absolute;
	left: 0;
	width: 256px;
	padding: 5px;
	border: 1px solid var(--border);
	border-radius: 12px;
	background: var(--panel);
	box-shadow: 0 4px 16px rgba(var(--shadow-ink), 0.08), 0 16px 36px rgba(var(--shadow-ink), 0.16);
	z-index: 50;
}

.agent-popover--down {
	top: calc(100% + 7px);
	transform-origin: top left;
	animation: agent-pop-in-down 0.16s cubic-bezier(0.16, 1, 0.3, 1);
}

.agent-popover--up {
	bottom: calc(100% + 7px);
	transform-origin: bottom left;
	animation: agent-pop-in-up 0.16s cubic-bezier(0.16, 1, 0.3, 1);
}

@keyframes agent-pop-in-down {
	from {
		opacity: 0;
		transform: translateY(-5px) scale(0.98);
	}
	to {
		opacity: 1;
		transform: translateY(0) scale(1);
	}
}

@keyframes agent-pop-in-up {
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
	.agent-popover--down,
	.agent-popover--up {
		animation: none;
	}
}

.pop-label {
	font-size: 11px;
	font-weight: 650;
	letter-spacing: 0.04em;
	text-transform: uppercase;
	color: var(--muted);
	margin: 5px 8px 4px;
}

.agent-hint {
	margin: 4px 8px 6px;
	color: var(--muted-soft);
	font-size: 11.5px;
	line-height: 1.5;
}

.agent-hint--error {
	color: var(--err-text);
}

.agent-list {
	display: grid;
	gap: 2px;
	max-height: 240px;
	overflow-y: auto;
	overscroll-behavior: contain;
	padding: 2px 0;
}

.agent-list::-webkit-scrollbar {
	width: 5px;
}

.agent-list::-webkit-scrollbar-track {
	background: transparent;
}

.agent-list::-webkit-scrollbar-thumb {
	background: var(--border-strong);
	border-radius: 99px;
}

/* 列表项：对标图二/图三极简行排版 */
.agent-item {
	display: flex;
	align-items: center;
	gap: 9px;
	width: 100%;
	padding: 8px 10px;
	border: 0;
	border-radius: 8px;
	background: transparent;
	color: var(--text);
	text-align: left;
	cursor: pointer;
	transition: background 0.12s ease;
}

.agent-item:hover {
	background: var(--panel-soft);
}

.agent-item[aria-checked='true'] {
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

.agent-item-glyph svg {
	width: 14px;
	height: 14px;
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
	font-size: 13px;
	font-weight: 550;
	color: var(--text);
	line-height: 1.25;
}

.agent-item-desc {
	overflow: hidden;
	text-overflow: ellipsis;
	white-space: nowrap;
	font-size: 11px;
	color: var(--muted);
	line-height: 1.35;
}

.agent-item-desc--error {
	color: var(--err-text);
}

.agent-item-mode {
	flex: none;
	font-size: 10.5px;
	font-weight: 500;
	padding: 1px 5px;
	border-radius: 4px;
	color: var(--muted-soft);
}

.agent-item-check {
	width: 14px;
	height: 14px;
	color: var(--text-strong);
	visibility: hidden;
	flex: none;
}

.agent-item[aria-checked='true'] .agent-item-check {
	visibility: visible;
}
</style>
