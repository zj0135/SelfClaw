import { nextTick, reactive, readonly } from 'vue';

export function useChatTerminal(panel, bridge) {
	const state = reactive({ isOpen: false, isRunning: false, cwd: '' });
	// replayLast：SideDock 随 ChatView 重挂载（如从设置页返回、WebView 重载）时宿主不会再推，
	// 靠粘性重放拿回最近一次开合状态。
	bridge.on('terminal-state', (payload) => {
		state.isOpen = Boolean(payload.isOpen);
		state.isRunning = Boolean(payload.isRunning);
		state.cwd = payload.cwd || '';
		nextTick(() => { panel.value?.fit?.(); panel.value?.focus?.(); });
	}, { replayLast: true });
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
