<script setup>
import { FitAddon } from '@xterm/addon-fit';
import { Terminal } from '@xterm/xterm';
import '@xterm/xterm/css/xterm.css';
import { nextTick, onMounted, onUnmounted, ref, watch } from 'vue';
import { useAppearance } from '../../composables/useAppearance.js';

const props = defineProps({
	isOpen: {
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

const emit = defineEmits(['ready', 'input', 'resize', 'close', 'restart', 'focus-change']);

const { revision } = useAppearance();

const terminalHostRef = ref(null);
let terminal = null;
let fitAddon = null;
let resizeObserver = null;
let readySent = false;

// xterm 自己解析颜色并画到 canvas，读不到 var()。所以这里把 --term-* 读成真实
// 色串再交给它；主题一变就得整份重读，没有办法只让 CSS 生效。
function readTerminalTheme() {
	const styles = getComputedStyle(document.documentElement);
	const pick = (name) => styles.getPropertyValue(name).trim();

	return {
		background: pick('--term-bg'),
		foreground: pick('--term-fg'),
		cursor: pick('--term-cursor'),
		selectionBackground: pick('--term-selection'),
		black: pick('--term-black'),
		red: pick('--term-red'),
		green: pick('--term-green'),
		yellow: pick('--term-yellow'),
		blue: pick('--term-blue'),
		magenta: pick('--term-magenta'),
		cyan: pick('--term-cyan'),
		white: pick('--term-white'),
		brightBlack: pick('--term-bright-black'),
		brightRed: pick('--term-bright-red'),
		brightGreen: pick('--term-bright-green'),
		brightYellow: pick('--term-bright-yellow'),
		brightBlue: pick('--term-bright-blue'),
		brightMagenta: pick('--term-bright-magenta'),
		brightCyan: pick('--term-bright-cyan'),
		brightWhite: pick('--term-bright-white'),
	};
}

// 终端是等宽内容，跟代码块用同一组字体设置，而不是界面字体。
function readTerminalFont() {
	const styles = getComputedStyle(document.documentElement);
	const family = styles.getPropertyValue('--font-code').trim();
	const size = Number.parseFloat(styles.getPropertyValue('--code-fs'));

	return {
		fontFamily: family || '"Cascadia Mono", "Cascadia Code", Consolas, monospace',
		fontSize: Number.isFinite(size) && size >= 6 ? size : 12,
	};
}

function fitAndNotify() {
	if (!terminal || !fitAddon || !terminalHostRef.value || !props.isOpen) {
		return;
	}

	try {
		fitAddon.fit();
		emit('resize', {
			cols: terminal.cols,
			rows: terminal.rows,
		});
	} catch {
		// The host can be briefly 0x0 while the panel animation is running.
	}
}

function focusTerminal() {
	if (props.isOpen && terminal) {
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

function handleClose() {
	emit('close');
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
	() => props.isOpen,
	(isOpen) => {
		if (!isOpen) {
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
	<section class="terminal-panel" :class="{ open: isOpen }" aria-label="Terminal" @mousedown="focusTerminal"
		@click="focusTerminal" @focusin="handleFocusIn" @focusout="handleFocusOut">
		<header class="terminal-header">
			<div class="terminal-title">
				<span class="terminal-icon" aria-hidden="true">
					<svg viewBox="0 0 20 20">
						<path d="M3.5 4.5h13v11h-13z"></path>
						<path d="M6.2 7.8 8.7 10l-2.5 2.2"></path>
						<path d="M10.2 12.2h3.6"></path>
					</svg>
				</span>
				<span>Terminal</span>
			</div>
			<div class="terminal-cwd" :title="cwd">{{ cwd || 'Desktop' }}</div>
			<div class="terminal-status" :class="{ running: isRunning }"
				:title="isRunning ? 'PowerShell running' : 'PowerShell stopped'"
				:aria-label="isRunning ? 'PowerShell running' : 'PowerShell stopped'">
				<svg v-if="isRunning" viewBox="0 0 20 20" aria-hidden="true">
					<path d="M4.5 10.4 8.1 14 15.5 6"></path>
				</svg>
				<svg v-else viewBox="0 0 20 20" aria-hidden="true">
					<path d="M6 6 14 14"></path>
					<path d="M14 6 6 14"></path>
				</svg>
			</div>
			<button class="terminal-action" type="button" aria-label="Restart terminal" title="Restart"
				@click="handleRestart">
				<svg viewBox="0 0 1024 1024" aria-hidden="true">
					<path fill="currentColor"
						d="M771.776 794.88A384 384 0 0 1 128 512h64a320 320 0 0 0 555.712 216.448H654.72a32 32 0 1 1 0-64h149.056a32 32 0 0 1 32 32v148.928a32 32 0 1 1-64 0v-50.56zM276.288 295.616h92.992a32 32 0 0 1 0 64H220.16a32 32 0 0 1-32-32V178.56a32 32 0 0 1 64 0v50.56A384 384 0 0 1 896.128 512h-64a320 320 0 0 0-555.776-216.384z">
					</path>
				</svg>
			</button>
			<button class="terminal-close" type="button" aria-label="Close terminal" title="Close" @click="handleClose">
				<svg viewBox="0 0 20 20" aria-hidden="true">
					<path d="M6 6 14 14"></path>
					<path d="M14 6 6 14"></path>
				</svg>
			</button>
		</header>
		<div ref="terminalHostRef" class="terminal-host" tabindex="0"></div>
	</section>
</template>

<style scoped>
.terminal-panel {
	min-height: 0;
	display: grid;
	grid-template-rows: 30px minmax(0, 1fr);
	overflow: hidden;
	position: relative;
	border-top: 1px solid var(--border-strong);
	background: var(--panel);
	opacity: 0;
	pointer-events: none;
}

.terminal-panel.open {
	opacity: 1;
	pointer-events: auto;
}

.terminal-header {
	min-width: 0;
	display: grid;
	grid-template-columns: auto minmax(0, 1fr) auto auto auto;
	align-items: center;
	gap: 9px;
	padding: 0 8px;
	border-bottom: 1px solid var(--border);
	background: var(--panel);
	color: var(--text);
	font-size: var(--fs-10);
}

.terminal-title {
	display: inline-flex;
	align-items: center;
	gap: 7px;
	color: var(--text);
	font-size: var(--fs-12);
	font-weight: 650;
}

.terminal-icon {
	width: 15px;
	height: 15px;
	display: inline-grid;
	place-items: center;
	color: var(--text);
}

.terminal-icon svg {
	width: 15px;
	height: 15px;
	fill: none;
	stroke: currentColor;
	stroke-width: 1.45;
	stroke-linecap: round;
	stroke-linejoin: round;
}

.terminal-cwd {
	min-width: 0;
	overflow: hidden;
	color: var(--muted);
	font-family: var(--font-code);
	text-overflow: ellipsis;
	white-space: nowrap;
}

.terminal-status {
	width: 18px;
	height: 18px;
	display: inline-grid;
	place-items: center;
	border-radius: 50%;
	color: var(--danger);
	white-space: nowrap;
}

.terminal-status.running {
	color: var(--success);
}

.terminal-status svg {
	width: 14px;
	height: 14px;
	fill: none;
	stroke: currentColor;
	stroke-width: 2.2;
	stroke-linecap: round;
	stroke-linejoin: round;
}

.terminal-action,
.terminal-close {
	width: 22px;
	height: 22px;
	display: grid;
	place-items: center;
	border: 1px solid var(--border-strong);
	border-radius: 50%;
	background: var(--panel);
	color: var(--text);
	padding: 0;
}

.terminal-action svg,
.terminal-close svg {
	width: 13px;
	height: 13px;
	fill: none;
	stroke: currentColor;
	stroke-width: 1.8;
	stroke-linecap: round;
	stroke-linejoin: round;
}

.terminal-action svg {
	width: 12px;
	height: 12px;
	fill: currentColor;
	stroke: none;
}

.terminal-close svg {
	width: 12px;
	height: 12px;
	stroke-width: 2;
}

.terminal-action:hover,
.terminal-close:hover {
	border-color: var(--border-strong);
	background: var(--panel-muted);
}

.terminal-host {
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
