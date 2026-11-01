<script setup>
import { computed, defineAsyncComponent, onUnmounted, ref, watch } from 'vue';
import { Activity, FileDiff, Plus, SquareTerminal, X } from 'lucide-vue-next';
import ReviewPane from './ReviewPane.vue';
import SubagentDockPane from './SubagentDockPane.vue';
import { useChatTerminal } from '../../composables/useChatTerminal.js';
import { useHostBridge } from '../../composables/hostBridge.js';
import { useSideDock } from '../../composables/useSideDock.js';

// xterm 体积大，且终端 pane 首次激活前不应起 ConPTY：保持懒加载 + terminalActivated 门。
const TerminalPane = defineAsyncComponent(() => import('./TerminalPane.vue'));

defineProps({
	parentConversationId: { type: String, default: null },
	workspaceRootId: { type: String, default: null },
});
const emit = defineEmits(['preview-image']);

const bridge = useHostBridge();
const dock = useSideDock();
const state = dock.state;
const terminalPaneRef = ref(null);
const terminal = useChatTerminal(terminalPaneRef, bridge);
const resizing = ref(false);
let dragStartX = 0;
let dragStartWidth = 0;

const TOOL_ICONS = { terminal: SquareTerminal, subagents: Activity, review: FileDiff };
const openTools = computed(() => state.tabs.map((id) => dock.tools.find((tool) => tool.id === id)).filter(Boolean));
const launcherVisible = computed(() => state.open && state.activeTab === null);
const terminalVisible = computed(() => state.open && state.activeTab === 'terminal');
const subagentVisible = computed(() => state.open && state.activeTab === 'subagents');
const reviewVisible = computed(() => state.open && state.activeTab === 'review');

// 宽度动画（240ms）期间几何一直在变：pane 的「可见」要等动画结束再给，否则详情滚动位置
// 恢复、xterm fit 都发生在中间尺寸上，随后被回流带偏。display 切换本身不等。
const SETTLE_MS = 300;
const settled = ref(true);
let settleTimer = null;
const terminalReady = computed(() => terminalVisible.value && settled.value);
const subagentReady = computed(() => subagentVisible.value && settled.value);
const reviewReady = computed(() => reviewVisible.value && settled.value);

function finishSettling() {
	window.clearTimeout(settleTimer);
	settleTimer = null;
	settled.value = true;
}

watch(() => state.open, () => {
	settled.value = false;
	window.clearTimeout(settleTimer);
	settleTimer = window.setTimeout(finishSettling, SETTLE_MS);
});

function onTransitionEnd(event) {
	if (event.target === event.currentTarget && event.propertyName === 'width') finishSettling();
}

onUnmounted(() => window.clearTimeout(settleTimer));

// 宿主 IsOpen 是唯一真值：推送到即镜像（T3/T4），前端只在用户主动关闭时先行复位。
watch(() => terminal.state.isOpen, (isOpen) => dock.applyTerminalState(isOpen), { immediate: true });

function onDockPointerDown(event) {
	if (event.target instanceof Element && event.target.closest('.terminal-pane')) return;
	terminal.setFocused(false);
}

function onResizePointerDown(event) {
	if (event.button !== 0) return;
	event.preventDefault();
	resizing.value = true;
	dragStartX = event.clientX;
	dragStartWidth = state.width;
	event.currentTarget.setPointerCapture?.(event.pointerId);
}

function onResizePointerMove(event) {
	if (!resizing.value) return;
	dock.setWidth(dragStartWidth + (dragStartX - event.clientX));
}

function onResizePointerUp(event) {
	if (!resizing.value) return;
	resizing.value = false;
	event.currentTarget.releasePointerCapture?.(event.pointerId);
	dock.commitWidth();
}

defineExpose({ blurTerminal: () => terminal.setFocused(false) });
</script>

<template>
	<aside class="side-dock" :class="{ open: state.open, resizing }"
		:style="{ width: state.open ? `${state.width}px` : '0px' }" aria-label="功能面板" @pointerdown="onDockPointerDown"
		@transitionend="onTransitionEnd">
		<div class="dock-resizer" role="separator" aria-label="调整面板宽度" @pointerdown="onResizePointerDown"
			@pointermove="onResizePointerMove" @pointerup="onResizePointerUp" @pointercancel="onResizePointerUp"></div>
		<header class="dock-tabs" role="tablist" aria-label="面板工具">
			<span v-if="!openTools.length" class="dock-strip-title">功能面板</span>
			<!-- tab 本体与关闭键是两个并列按钮：按钮不能嵌套，关闭也不该触发切换。 -->
			<div v-for="tool in openTools" :key="tool.id" class="dock-tab" :class="{ active: state.activeTab === tool.id }"
				:data-tool="tool.id">
				<button class="dock-tab-select" type="button" role="tab" :aria-selected="state.activeTab === tool.id"
					@click="dock.activate(tool.id)">
					<component :is="TOOL_ICONS[tool.id]" :size="14" :stroke-width="1.8" />
					<span>{{ tool.label }}</span>
				</button>
				<button class="dock-tab-close" type="button" :title="`关闭${tool.label}`" :aria-label="`关闭${tool.label}`"
					@click="dock.closeTab(tool.id)">
					<X :size="12" :stroke-width="2" />
				</button>
			</div>
			<button v-if="openTools.length && state.activeTab !== null" class="dock-new-tab" type="button" title="打开其他工具"
				aria-label="打开其他工具" @click="dock.showLauncher()">
				<Plus :size="14" :stroke-width="1.8" />
			</button>
			<button class="dock-close" type="button" title="收起面板" aria-label="收起面板" @click="dock.close()">
				<X :size="14" :stroke-width="1.8" />
			</button>
		</header>
		<div class="dock-body">
			<div v-if="launcherVisible" class="dock-launcher" aria-label="工具列表">
				<button v-for="tool in dock.tools" :key="tool.id" class="dock-launcher-item" type="button" :data-tool="tool.id"
					@click="dock.activate(tool.id)">
					<span class="dock-launcher-icon"><component :is="TOOL_ICONS[tool.id]" :size="18" :stroke-width="1.7" /></span>
					<span class="dock-launcher-text">
						<strong>{{ tool.label }}</strong>
						<small>{{ tool.description }}</small>
					</span>
					<span class="dock-launcher-action">{{ state.tabs.includes(tool.id) ? '切换' : '打开' }}</span>
				</button>
			</div>
			<SubagentDockPane v-show="subagentVisible" :parent-conversation-id="parentConversationId"
				:visible="subagentReady" @preview-image="emit('preview-image', $event)" />
			<ReviewPane v-show="reviewVisible" :visible="reviewReady" :workspace-root-id="workspaceRootId" />
			<!-- v-show 放在普通容器上：指令挂在异步组件上要穿过 AsyncWrapper 与内层组件两级根节点才落到元素，不可靠。 -->
			<div v-if="state.terminalActivated" v-show="terminalVisible" class="terminal-slot">
				<TerminalPane ref="terminalPaneRef" :visible="terminalReady" :is-running="terminal.state.isRunning"
					:cwd="terminal.state.cwd" @ready="terminal.ready" @input="terminal.input" @resize="terminal.resize"
					@restart="terminal.restart" @focus-change="terminal.setFocused" />
			</div>
		</div>
	</aside>
</template>

<style scoped>
@import '../../styles/side-dock.css';
</style>
