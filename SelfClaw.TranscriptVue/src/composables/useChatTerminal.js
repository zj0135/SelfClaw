import { nextTick, reactive, readonly } from 'vue';

export function useChatTerminal(panel, bridge) {
	const state = reactive({ isOpen: false, isRunning: false, cwd: '' });
	bridge.on('terminal-state', (payload) => {
		state.isOpen = Boolean(payload.isOpen);
		state.isRunning = Boolean(payload.isRunning);
		state.cwd = payload.cwd || '';
		nextTick(() => { panel.value?.fit?.(); panel.value?.focus?.(); });
	});
	bridge.on('terminal-output', (payload) => panel.value?.write?.(payload.data || ''));
	bridge.on('terminal-clear', () => panel.value?.clear?.());
	bridge.on('terminal-focus', () => nextTick(() => panel.value?.focus?.()));

	function ready(size) { bridge.post({ type: 'terminal-ready', cols: size.cols, rows: size.rows }); }
	function input(data) { bridge.post({ type: 'terminal-input', data }); }
	function resize(size) { bridge.post({ type: 'terminal-resize', cols: size.cols, rows: size.rows }); }
	function close() { bridge.post({ type: 'terminal-close' }); }
	function restart() { bridge.post({ type: 'terminal-restart' }); }
	function setFocused(isFocused) { bridge.post({ type: 'terminal-focus-change', isFocused }); }
	return { state: readonly(state), ready, input, resize, close, restart, setFocused };
}
