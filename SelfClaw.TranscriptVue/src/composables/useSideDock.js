import { reactive } from 'vue';
import { hostBridge } from './hostBridge.js';

// 右侧功能面板（SideDock）的唯一状态源。仅聊天视图挂载，模块级单例。
//
// 模型：标题栏一个「面板」开关，对话区右上角一条悬浮快捷栏（每个工具一个图标）；面板内是已打开
// 工具的 tab 条，没有 tab（或点「+」）时显示启动页（工具列表 + 打开）。每个 tab 可单独关闭；
// 关 dock 只是隐藏，tab 保留；启动时什么都不打开。
//
// 终端与宿主的映射——宿主 IsOpen 是唯一真值，前端不做乐观更新；只有用户主动关闭的
// 动作会先行复位 terminalOpen，防止"关后立刻重开"在宿主回包到达前被旧状态误判：
//   T1 命令行 tab 被激活（打开 / 切换 / dock 重开时它正是当前 tab）且宿主未开 → post toggle-terminal；
//   T2 关 dock 或关命令行 tab 且终端开着 → post terminal-close（会话保留，只暂停输出）；
//   T3 宿主推 isOpen=true → 确保命令行 tab 存在；dock 收起则打开并切到它，开在启动页也切到它；
//   T4 宿主推 isOpen=false → 只更新镜像位。

const WIDTH_KEY = 'selfclaw:dock-width';
export const SIDE_DOCK_DEFAULT_WIDTH = 380;
export const SIDE_DOCK_MIN_WIDTH = 300;
export const SIDE_DOCK_MAX_WIDTH = 640;

// 工具注册表：新增工具（如 diff）在这里加一项，并在 SideDock.vue 里多渲染一个 pane。
const TOOLS = [
	{ id: 'terminal', label: '命令行', description: '运行命令并查看输出。' },
	{ id: 'subagents', label: '子代理', description: '查看子代理任务与实时输出。' },
];
const TOOL_IDS = TOOLS.map((tool) => tool.id);

function readStorage(key) {
	try { return window.localStorage.getItem(key); } catch { return null; }
}

function writeStorage(key, value) {
	try { window.localStorage.setItem(key, value); } catch { /* 持久化失败不影响本次会话 */ }
}

export function clampDockWidth(px) {
	const value = Number.isFinite(px) ? Math.round(px) : SIDE_DOCK_DEFAULT_WIDTH;
	return Math.min(SIDE_DOCK_MAX_WIDTH, Math.max(SIDE_DOCK_MIN_WIDTH, value));
}

function readInitialWidth() {
	const stored = Number.parseInt(readStorage(WIDTH_KEY) ?? '', 10);
	return Number.isFinite(stored) ? clampDockWidth(stored) : SIDE_DOCK_DEFAULT_WIDTH;
}

const state = reactive({
	open: false,
	// 已打开的工具 tab（有序）与当前 tab；activeTab 为 null 时显示启动页。
	tabs: [],
	activeTab: null,
	width: readInitialWidth(),
	terminalOpen: false,
	// 终端 pane 首次激活后才挂载：启动时不加载 xterm chunk，也不起 ConPTY。
	terminalActivated: false,
	// 当前会话的子代理任务存在感：只驱动快捷栏上的角点，不显示数字。
	subagents: { total: 0, running: 0 },
});

function ensureTerminalOpen() {
	state.terminalActivated = true;
	if (!state.terminalOpen) hostBridge.post({ type: 'toggle-terminal' });
}

function pauseTerminal() {
	if (!state.terminalOpen) return;
	state.terminalOpen = false;
	hostBridge.post({ type: 'terminal-close' });
}

const dock = {
	state,
	tools: TOOLS,

	open() {
		state.open = true;
		if (state.activeTab === 'terminal') ensureTerminalOpen();
	},

	close() {
		state.open = false;
		pauseTerminal();
	},

	toggle() {
		if (state.open) dock.close();
		else dock.open();
	},

	// 悬浮快捷栏：点某个工具就打开 / 切到它；它已经是当前 tab 时再点一次收起 dock。
	toggleTool(toolId) {
		if (state.open && state.activeTab === toolId) dock.close();
		else dock.activate(toolId);
	},

	// 打开（或切到）一个工具：没有 tab 就新建，顺带把 dock 打开。
	activate(toolId) {
		if (!TOOL_IDS.includes(toolId)) return;
		if (!state.tabs.includes(toolId)) state.tabs.push(toolId);
		state.activeTab = toolId;
		state.open = true;
		if (toolId === 'terminal') ensureTerminalOpen();
	},

	showLauncher() {
		state.activeTab = null;
		state.open = true;
	},

	// 关 tab：当前 tab 被关时退到左邻（没有则右邻，再没有就回启动页）。
	closeTab(toolId) {
		const index = state.tabs.indexOf(toolId);
		if (index < 0) return;
		state.tabs.splice(index, 1);
		if (state.activeTab === toolId) state.activeTab = state.tabs[index - 1] ?? state.tabs[index] ?? null;
		if (toolId === 'terminal') pauseTerminal();
	},

	setWidth(px) { state.width = clampDockWidth(px); },

	commitWidth() { writeStorage(WIDTH_KEY, String(state.width)); },

	// useChatTerminal 的 terminal-state 镜像入口（T3/T4）。
	applyTerminalState(isOpen) {
		state.terminalOpen = isOpen;
		if (!isOpen) return;
		state.terminalActivated = true;
		if (!state.tabs.includes('terminal')) state.tabs.push('terminal');
		if (!state.open) {
			state.open = true;
			state.activeTab = 'terminal';
		} else if (state.activeTab === null) {
			state.activeTab = 'terminal';
		}
	},

	setSubagentCounts(counts) {
		state.subagents = { total: counts?.total ?? 0, running: counts?.running ?? 0 };
	},
};

export function useSideDock() {
	return dock;
}
