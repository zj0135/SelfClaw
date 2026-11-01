<script setup>
import { FitAddon } from '@xterm/addon-fit';
import { Terminal } from '@xterm/xterm';
import '@xterm/xterm/css/xterm.css';
import { Check, RefreshCw, X } from 'lucide-vue-next';
import { nextTick, onMounted, onUnmounted, ref, watch } from 'vue';
import { useAppearance } from '../../composables/useAppearance.js';
import { readTerminalFont, readTerminalTheme } from '../../renderers/terminalTheme.js';

const props = defineProps({
	// dock 打开且停留在终端 tab 时为 true；fit / ready / 聚焦都以此为准。
	visible: {
		type: Boolean,
		default: false,
	},
	isRunning: {
		type: Boolean,
		default: false,
	},
	cwd: {
		type: String,
		default: '',
	},
});

const emit = defineEmits(['ready', 'input', 'resize', 'restart', 'focus-change']);

const { revision } = useAppearance();

const terminalHostRef = ref(null);
let terminal = null;
let fitAddon = null;
let resizeObserver = null;
let readySent = false;

function fitAndNotify() {
	if (!terminal || !fitAddon || !terminalHostRef.value || !props.visible) {
		return;
	}

	try {
		fitAddon.fit();
		emit('resize', {
			cols: terminal.cols,
			rows: terminal.rows,
		});
	} catch {
		// The host can be briefly 0x0 while the dock animation is running.
	}
}

function focusTerminal() {
	if (props.visible && terminal) {
		terminalHostRef.value?.focus?.({ preventScroll: true });
		terminal.focus();
		emit('focus-change', true);
	}
}

function handleFocusIn() {
	emit('focus-change', true);
}

function handleFocusOut() {
	emit('focus-change', false);
}

function write(data) {
	if (terminal && data) {
		terminal.write(data);
	}
}

function clear() {
	if (terminal) {
		terminal.clear();
	}
}

function handleRestart() {
	clear();
	emit('restart');
}

function sendReady() {
	if (!terminal || readySent) {
		return;
	}

	readySent = true;
	emit('ready', {
		cols: terminal.cols,
		rows: terminal.rows,
	});
}

onMounted(() => {
	const font = readTerminalFont();
	terminal = new Terminal({
		cursorBlink: true,
		cursorStyle: 'bar',
		fontFamily: font.fontFamily,
		fontSize: font.fontSize,
		lineHeight: 1.2,
		scrollback: 8000,
		convertEol: true,
		allowProposedApi: false,
		theme: readTerminalTheme(),
	});

	fitAddon = new FitAddon();
	terminal.loadAddon(fitAddon);
	terminal.open(terminalHostRef.value);
	terminal.onData((data) => emit('input', data));

	resizeObserver = new ResizeObserver(() => {
		nextTick(fitAndNotify);
	});
	resizeObserver.observe(terminalHostRef.value);

	nextTick(() => {
		fitAndNotify();
		sendReady();
		focusTerminal();
	});
});

onUnmounted(() => {
	resizeObserver?.disconnect();
	resizeObserver = null;
	terminal?.dispose();
	terminal = null;
	fitAddon = null;
});

watch(
	() => props.visible,
	(visible) => {
		if (!visible) {
			return;
		}

		nextTick(() => {
			fitAndNotify();
			sendReady();
			focusTerminal();
		});
	}
);

// 外观一变就整份重读。字号变了必须紧跟一次 fit()：xterm 的行列数是按字符尺寸
// 算的，不重新测量的话网格与容器就对不上，右侧或底部会空出一条。
watch(revision, () => {
	if (!terminal) {
		return;
	}

	const font = readTerminalFont();
	terminal.options.theme = readTerminalTheme();
	terminal.options.fontFamily = font.fontFamily;
	terminal.options.fontSize = font.fontSize;
	nextTick(fitAndNotify);
});

defineExpose({
	write,
	clear,
	fit: fitAndNotify,
	focus: focusTerminal,
});
</script>

<template>
	<section class="terminal-pane" aria-label="终端" @mousedown="focusTerminal" @click="focusTerminal"
		@focusin="handleFocusIn" @focusout="handleFocusOut">
		<header class="terminal-toolbar">
			<div class="terminal-cwd" :title="cwd">{{ cwd || 'Desktop' }}</div>
			<div class="terminal-status" :class="{ running: isRunning }" :title="isRunning ? '运行中' : '已停止'"
				:aria-label="isRunning ? '运行中' : '已停止'">
				<Check v-if="isRunning" :size="13" :stroke-width="2.2" />
				<X v-else :size="13" :stroke-width="2.2" />
			</div>
			<button class="terminal-restart" type="button" aria-label="重启终端" title="重启终端" @click="handleRestart">
				<RefreshCw :size="14" />
			</button>
		</header>
		<div ref="terminalHostRef" class="terminal-host" tabindex="0"></div>
	</section>
</template>

<style scoped>
.terminal-pane {
	display: flex;
	flex-direction: column;
	height: 100%;
	min-height: 0;
	overflow: hidden;
	background: var(--panel);
}

/* 高度与内边距对齐子代理 pane 的工具条：切 tab 时底线与右侧刷新键位置不跳。 */
.terminal-toolbar {
	display: grid;
	grid-template-columns: minmax(0, 1fr) auto auto;
	align-items: center;
	gap: 8px;
	flex: 0 0 38px;
	padding: 0 6px 0 8px;
	border-bottom: 1px solid var(--border);
	background: var(--panel);
}

.terminal-cwd {
	min-width: 0;
	overflow: hidden;
	color: var(--muted);
	font-family: var(--font-code);
	font-size: var(--fs-10);
	text-overflow: ellipsis;
	white-space: nowrap;
}

.terminal-status {
	display: inline-grid;
	place-items: center;
	width: 18px;
	height: 18px;
	border-radius: 50%;
	color: var(--danger);
}

.terminal-status.running {
	color: var(--success);
}

/* 与子代理 pane 的刷新键同一套：24×24 无边框方块、灰色、悬停提亮。 */
.terminal-restart {
	display: grid;
	place-items: center;
	flex: none;
	width: 24px;
	height: 24px;
	padding: 0;
	border: 0;
	border-radius: 6px;
	background: transparent;
	color: var(--muted);
}

.terminal-restart:hover {
	background: var(--panel-hover);
	color: var(--text);
}

.terminal-host {
	flex: 1 1 auto;
	min-height: 0;
	padding: 4px 8px 8px;
	overflow: hidden;
	background: var(--term-bg);
}

.terminal-host :deep(.xterm) {
	height: 100%;
}

.terminal-host :deep(.xterm .xterm-viewport) {
	background-color: var(--term-bg);
}

.terminal-host :deep(.xterm .xterm-scrollable-element > .shadow) {
	display: none !important;
	box-shadow: none !important;
}

.terminal-host :deep(.xterm .xterm-viewport::-webkit-scrollbar) {
	width: 10px;
	height: 10px;
}

.terminal-host :deep(.xterm .xterm-viewport::-webkit-scrollbar-track) {
	background: var(--scroll-track);
}

.terminal-host :deep(.xterm .xterm-viewport::-webkit-scrollbar-thumb) {
	background: var(--scroll-thumb);
	border: 2px solid var(--term-bg);
	border-radius: 999px;
}
</style>
