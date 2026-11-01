import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { h } from 'vue';
import { mount, flushPromises } from '@vue/test-utils';
import { installActivityHost } from './fixtures/activityHost.js';

let wrapper;
beforeEach(() => {
	vi.resetModules();
	localStorage.clear();
	installActivityHost();
});
afterEach(() => { wrapper?.unmount(); wrapper = null; });

const requestsOf = (type) => window.activityFixture.requests.filter((request) => request.type === type);

async function loadDock() {
	const { useSideDock } = await import('../src/composables/useSideDock.js');
	return useSideDock();
}

// 两个 pane 用渲染函数桩替代：xterm 不能在 jsdom 里跑，子代理 pane 已有自己的用例。
// __esModule 必须带上：defineAsyncComponent 只在模块"像 ES module"时才解包 .default，
// 否则 vitest 的严格 mock 命名空间会被当成组件本身。
function stubPanes() {
	vi.doMock('../src/components/dock/TerminalPane.vue', () => ({
		__esModule: true,
		default: { name: 'TerminalPane', props: ['visible', 'isRunning', 'cwd'], render: () => h('section', { class: 'terminal-pane' }, 'terminal') },
	}));
	vi.doMock('../src/components/dock/SubagentDockPane.vue', () => ({
		__esModule: true,
		default: { name: 'SubagentDockPane', props: ['parentConversationId', 'visible'], render: () => h('div', { class: 'subagent-pane' }, 'subagents') },
	}));
}

async function mountDock() {
	stubPanes();
	const { default: SideDock } = await import('../src/components/dock/SideDock.vue');
	wrapper = mount(SideDock, { props: { parentConversationId: window.activityFixture.parent } });
	await flushPromises();
	return wrapper;
}

// v-show 落在 .terminal-slot / pane 根元素的内联 display 上；直接断言内联样式，不依赖 VTU isVisible 对祖先的判定。
const inlineStyle = (selector) => wrapper.find(selector).attributes('style') ?? '';

it('opens to the launcher with no tabs, toggles closed, and never touches the host', async () => {
	const dock = await loadDock();
	expect(dock.state).toMatchObject({ open: false, tabs: [], activeTab: null });
	dock.open();
	expect(dock.state).toMatchObject({ open: true, tabs: [], activeTab: null });
	dock.toggle();
	expect(dock.state.open).toBe(false);
	dock.toggle();
	expect(dock.state.open).toBe(true);
	expect(window.activityFixture.requests.filter((request) => request.type.includes('terminal'))).toHaveLength(0);
	dock.activate('diff');
	expect(dock.state.tabs).toEqual([]);
});

it('activate opens a tab, the launcher stays reachable, and closing the last tab falls back to it', async () => {
	const dock = await loadDock();
	dock.activate('subagents');
	expect(dock.state).toMatchObject({ open: true, tabs: ['subagents'], activeTab: 'subagents' });
	dock.showLauncher();
	expect(dock.state).toMatchObject({ open: true, tabs: ['subagents'], activeTab: null });
	dock.activate('subagents');
	expect(dock.state).toMatchObject({ tabs: ['subagents'], activeTab: 'subagents' });
	dock.closeTab('subagents');
	expect(dock.state).toMatchObject({ open: true, tabs: [], activeTab: null });
	dock.closeTab('subagents');
	expect(dock.state.tabs).toEqual([]);
});

it('closing the active tab activates the left neighbour and closing the terminal tab pauses the host terminal', async () => {
	const dock = await loadDock();
	dock.activate('terminal');
	dock.applyTerminalState(true);
	dock.activate('subagents');
	expect(dock.state.tabs).toEqual(['terminal', 'subagents']);
	dock.closeTab('subagents');
	expect(dock.state.activeTab).toBe('terminal');
	expect(requestsOf('terminal-close')).toHaveLength(0);
	dock.activate('subagents');
	dock.closeTab('terminal');
	expect(dock.state).toMatchObject({ tabs: ['subagents'], activeTab: 'subagents', terminalOpen: false });
	expect(requestsOf('terminal-close')).toHaveLength(1);
});

it('terminal through the host: activation toggles once, tab switches keep it, dock close pauses, reopen resumes', async () => {
	const dock = await loadDock();
	dock.activate('terminal');
	expect(dock.state).toMatchObject({ open: true, activeTab: 'terminal', terminalActivated: true });
	expect(requestsOf('toggle-terminal')).toHaveLength(1);
	dock.applyTerminalState(true);
	dock.activate('subagents');
	expect(requestsOf('terminal-close')).toHaveLength(0);
	expect(dock.state.terminalOpen).toBe(true);
	dock.activate('terminal');
	expect(requestsOf('toggle-terminal')).toHaveLength(1);
	dock.close();
	expect(dock.state).toMatchObject({ open: false, terminalOpen: false, tabs: ['terminal', 'subagents'], activeTab: 'terminal' });
	expect(requestsOf('terminal-close')).toHaveLength(1);
	dock.open();
	expect(requestsOf('toggle-terminal')).toHaveLength(2);
	dock.applyTerminalState(true);
	dock.activate('subagents');
	dock.close();
	expect(requestsOf('terminal-close')).toHaveLength(2);
	dock.open();
	expect(requestsOf('toggle-terminal')).toHaveLength(2);
});

it('mirrors host pushes: an open push ensures a terminal tab, only steals focus from a closed dock or the launcher', async () => {
	const dock = await loadDock();
	dock.applyTerminalState(true);
	expect(dock.state).toMatchObject({ open: true, tabs: ['terminal'], activeTab: 'terminal', terminalOpen: true, terminalActivated: true });
	dock.showLauncher();
	dock.applyTerminalState(true);
	expect(dock.state.activeTab).toBe('terminal');
	dock.activate('subagents');
	dock.applyTerminalState(true);
	expect(dock.state).toMatchObject({ tabs: ['terminal', 'subagents'], activeTab: 'subagents' });
	dock.applyTerminalState(false);
	expect(dock.state).toMatchObject({ open: true, activeTab: 'subagents', terminalOpen: false });
});

it('clamps the width, persists it only on commit, and starts every session on the launcher', async () => {
	const dock = await loadDock();
	dock.setWidth(100);
	expect(dock.state.width).toBe(300);
	dock.setWidth(5000);
	expect(dock.state.width).toBe(640);
	dock.setWidth(420.6);
	expect(localStorage.getItem('selfclaw:dock-width')).toBeNull();
	dock.commitWidth();
	expect(localStorage.getItem('selfclaw:dock-width')).toBe('421');
	dock.activate('subagents');
	vi.resetModules();
	const fresh = await loadDock();
	expect(fresh.state).toMatchObject({ width: 421, open: false, tabs: [], activeTab: null });
});

it('falls back to the default width for corrupt storage', async () => {
	localStorage.setItem('selfclaw:dock-width', 'abc');
	const dock = await loadDock();
	expect(dock.state.width).toBe(380);
});

it('SideDock shows the launcher, opens closable tabs, keeps "+" for other tools, and closes through the host', async () => {
	await mountDock();
	const dock = await loadDock();
	expect(wrapper.classes()).not.toContain('open');
	dock.open();
	await flushPromises();
	expect(wrapper.classes()).toContain('open');
	expect(wrapper.find('.dock-strip-title').text()).toBe('功能面板');
	expect(wrapper.findAll('.dock-tab')).toHaveLength(0);
	expect(wrapper.findAll('.dock-launcher-item')).toHaveLength(2);
	expect(wrapper.find('.dock-new-tab').exists()).toBe(false);

	await wrapper.find('.dock-launcher-item[data-tool="terminal"]').trigger('click');
	expect(requestsOf('toggle-terminal')).toHaveLength(1);
	window.activityFixture.terminal(true);
	await flushPromises();
	await vi.waitFor(() => expect(wrapper.find('.terminal-pane').exists()).toBe(true));
	expect(wrapper.findAll('.dock-tab')).toHaveLength(1);
	expect(wrapper.find('.dock-tab.active').attributes('data-tool')).toBe('terminal');
	expect(wrapper.find('.dock-launcher').exists()).toBe(false);
	expect(inlineStyle('.terminal-slot')).not.toContain('display: none');

	await wrapper.find('.dock-new-tab').trigger('click');
	expect(wrapper.find('.dock-launcher').exists()).toBe(true);
	expect(wrapper.find('.dock-launcher-item[data-tool="terminal"] .dock-launcher-action').text()).toBe('切换');
	expect(inlineStyle('.terminal-slot')).toContain('display: none');
	expect(requestsOf('terminal-close')).toHaveLength(0);

	await wrapper.find('.dock-launcher-item[data-tool="subagents"]').trigger('click');
	expect(wrapper.findAll('.dock-tab')).toHaveLength(2);
	expect(wrapper.find('.dock-tab.active').attributes('data-tool')).toBe('subagents');
	expect(inlineStyle('.subagent-pane')).not.toContain('display: none');

	await wrapper.find('.dock-tab[data-tool="terminal"] .dock-tab-close').trigger('click');
	expect(wrapper.findAll('.dock-tab')).toHaveLength(1);
	expect(wrapper.find('.dock-tab.active').attributes('data-tool')).toBe('subagents');
	expect(requestsOf('terminal-close')).toHaveLength(1);

	await wrapper.find('.dock-tab[data-tool="subagents"] .dock-tab-close').trigger('click');
	expect(wrapper.findAll('.dock-tab')).toHaveLength(0);
	expect(wrapper.find('.dock-launcher').exists()).toBe(true);
	expect(wrapper.classes()).toContain('open');

	await wrapper.find('.dock-close').trigger('click');
	expect(wrapper.classes()).not.toContain('open');
	expect(requestsOf('terminal-close')).toHaveLength(1);
});

it('SideDock mirrors a host terminal push into an active terminal tab', async () => {
	await mountDock();
	const dock = await loadDock();
	window.activityFixture.terminal(true);
	await flushPromises();
	expect(wrapper.classes()).toContain('open');
	expect(dock.state).toMatchObject({ tabs: ['terminal'], activeTab: 'terminal' });
	expect(wrapper.find('.dock-tab.active .dock-tab-select').text()).toContain('命令行');
	await vi.waitFor(() => expect(wrapper.find('.terminal-pane').exists()).toBe(true));
});

it('SideDock resizes from its left edge and persists the width on release', async () => {
	await mountDock();
	const dock = await loadDock();
	dock.activate('subagents');
	await flushPromises();
	expect(wrapper.attributes('style')).toContain('width: 380px');
	const handle = wrapper.find('.dock-resizer');
	await handle.trigger('pointerdown', { button: 0, clientX: 800, pointerId: 1 });
	await handle.trigger('pointermove', { clientX: 700, pointerId: 1 });
	expect(dock.state.width).toBe(480);
	expect(wrapper.classes()).toContain('resizing');
	await handle.trigger('pointerup', { pointerId: 1 });
	expect(wrapper.classes()).not.toContain('resizing');
	expect(wrapper.attributes('style')).toContain('width: 480px');
	expect(localStorage.getItem('selfclaw:dock-width')).toBe('480');
});
