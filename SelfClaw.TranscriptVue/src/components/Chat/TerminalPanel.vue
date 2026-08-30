<script setup>
import { FitAddon } from '@xterm/addon-fit';
import { Terminal } from '@xterm/xterm';
import '@xterm/xterm/css/xterm.css';
import { nextTick, onMounted, onUnmounted, ref, watch } from 'vue';

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

const terminalHostRef = ref(null);
let terminal = null;
let fitAddon = null;
let resizeObserver = null;
let readySent = false;

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
	terminal = new Terminal({
		cursorBlink: true,
		cursorStyle: 'bar',
		fontFamily: '"Cascadia Mono", "Cascadia Code", Consolas, monospace',
		fontSize: 12,
		lineHeight: 1.2,
		scrollback: 8000,
		convertEol: true,
		allowProposedApi: false,
		theme: {
			background: '#ffffff',
			foreground: '#171a1f',
			cursor: '#171a1f',
			selectionBackground: '#dbeafe',
			black: '#171a1f',
			red: '#b42318',
			green: '#1f7a4d',
			yellow: '#9a6400',
			blue: '#315fb8',
			magenta: '#7c3aa8',
			cyan: '#0f766e',
			white: '#f7f8fa',
			brightBlack: '#6b7280',
			brightRed: '#d92d20',
			brightGreen: '#2f855a',
			brightYellow: '#b7791f',
			brightBlue: '#4f73c8',
			brightMagenta: '#9333ea',
			brightCyan: '#0e9488',
			brightWhite: '#ffffff',
		},
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
	border-top: 1px solid #d8dde5;
	background: #ffffff;
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
	border-bottom: 1px solid #e5e7eb;
	background: #ffffff;
	color: #171a1f;
	font-size: 10px;
}

.terminal-title {
	display: inline-flex;
	align-items: center;
	gap: 7px;
	color: #171a1f;
	font-size: 12px;
	font-weight: 650;
}

.terminal-icon {
	width: 15px;
	height: 15px;
	display: inline-grid;
	place-items: center;
	color: #171a1f;
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
	color: #6b7280;
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
	color: #c24150;
	white-space: nowrap;
}

.terminal-status.running {
	color: #2f855a;
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
	border: 1px solid #d8dde5;
	border-radius: 50%;
	background: #ffffff;
	color: #171a1f;
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
	border-color: #c7ced8;
	background: #f1f3f6;
}

.terminal-host {
	min-height: 0;
	padding: 4px 8px 8px;
	overflow: hidden;
	background: #ffffff;
}

.terminal-host :deep(.xterm) {
	height: 100%;
}

.terminal-host :deep(.xterm .xterm-viewport) {
	background-color: #ffffff;
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
	background: rgba(23, 26, 31, 0.04);
}

.terminal-host :deep(.xterm .xterm-viewport::-webkit-scrollbar-thumb) {
	background: rgba(23, 26, 31, 0.14);
	border: 2px solid #ffffff;
	border-radius: 999px;
}
</style>
