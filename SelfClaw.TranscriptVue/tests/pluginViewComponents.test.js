import { afterEach, expect, it, vi } from 'vitest';
import { mount } from '@vue/test-utils';
import PluginDockHost from '../src/components/Plugins/PluginDockHost.vue';
import PluginFloatingLayer from '../src/components/Plugins/PluginFloatingLayer.vue';
import PluginLauncher from '../src/components/Plugins/PluginLauncher.vue';
import WindowControls from '../src/components/Chat/WindowControls.vue';

const ORIGIN = 'https://plugin.plugin.selfclaw.local';
const mounted = [];

afterEach(() => mounted.splice(0).forEach((wrapper) => wrapper.unmount()));

const view = (key, slot) => ({
    key, pluginId: key.split('/')[0], viewId: key.split('/')[1], title: `标题 ${key}`, icon: 'layers', slot,
    origin: ORIGIN, url: `${ORIGIN}/${key}.html`, defaultWidth: slot === 'right' ? 380 : null,
    enabled: true, status: 'ready', permissions: [], networkOrigins: [],
});

function attach(component, props) {
    const wrapper = mount(component, { props, global: { stubs: { iframe: true } } });
    mounted.push(wrapper);
    return wrapper;
}

it('lists docked and floating views in their own launcher sections', async () => {
    const wrapper = attach(PluginLauncher, {
        open: true,
        views: [view('plugin/dock', 'right'), view('plugin/hud', 'floating')],
        openKeys: ['plugin/hud'],
    });

    const sections = wrapper.findAll('.view-scroll section');
    expect(sections).toHaveLength(2);
    expect(sections[0].text()).toContain('右侧面板');
    expect(sections[0].text()).toContain('plugin/dock');
    expect(sections[1].text()).toContain('悬浮视图');
    expect(sections[1].text()).toContain('plugin/hud');

    // 关闭入口只出现在已打开的条目上：悬浮视图没有标签栏，这是宿主侧保证的关闭路径。
    const rows = wrapper.findAll('.view-list li');
    expect(rows).toHaveLength(2);
    expect(rows[0].find('.view-close').exists()).toBe(false);
    await rows[1].find('.view-close').trigger('click');
    expect(wrapper.emitted('close-view')[0]).toEqual(['plugin/hud']);

    await rows[1].find('.view-main').trigger('click');
    expect(wrapper.emitted('select')[0]).toEqual(['plugin/hud']);
});

it('leaves every floating frame click-through except the armed one', async () => {
    const wrapper = attach(PluginFloatingLayer, {
        views: [view('plugin/hud', 'floating'), view('plugin/other', 'floating')],
        armedKey: 'plugin/other',
    });

    const slots = wrapper.findAll('.float-slot');
    expect(slots).toHaveLength(2);
    expect(slots[0].attributes('style')).toContain('pointer-events: none');
    expect(slots[1].attributes('style')).toContain('pointer-events: auto');
    expect(wrapper.findAll('iframe').length).toBe(2);
});

// 收起必须是“不画”而不是“不布局”：display: none 会把帧的视口变成 0×0，插件的几何与 anchors 全失真。
it('conceals the layer without removing it from layout', () => {
    const visible = attach(PluginFloatingLayer, { views: [view('plugin/hud', 'floating')] });
    expect(visible.find('.plugin-floating-layer').classes()).not.toContain('concealed');

    const concealed = attach(PluginFloatingLayer, { views: [view('plugin/hud', 'floating')], hidden: true });
    expect(concealed.find('.plugin-floating-layer').classes()).toContain('concealed');
    // 帧仍然在 DOM 里（收起不是关闭）。
    expect(concealed.findAll('iframe').length).toBe(1);
});

it('keeps docked frames mounted and shows only the active one', async () => {
    const wrapper = attach(PluginDockHost, {
        views: [view('plugin/one', 'right'), view('plugin/two', 'right')],
        activeKey: 'plugin/two',
    });

    const slots = wrapper.findAll('.frame-slot');
    expect(slots).toHaveLength(2);
    expect(slots[0].classes()).not.toContain('active');
    expect(slots[1].classes()).toContain('active');
    // 非活动标签仍然在 DOM 里：display: none 不会卸载文档，切回来不必重新握手。
    expect(wrapper.findAll('iframe').length).toBe(2);

    await wrapper.findAll('.tab .tab-main')[0].trigger('click');
    expect(wrapper.emitted('activate')[0]).toEqual(['plugin/one']);
    expect(wrapper.find('.plugin-dock-host').attributes('data-anchor')).toBe('dock');
});

// 标题栏两颗插件按钮现在只是显隐开关：没东西可显隐时用禁用态表达，不再退回启动器
// （打开视图只剩左侧「插件」一个入口），亮起只代表「确有视图且正在显示」。
it('toggles docked and floating visibility without opening the launcher', async () => {
	const empty = attach(WindowControls, {});
	const [panel, floating] = empty.findAll('.tool-button').slice(1);
	expect(panel.attributes('disabled')).toBeDefined();
	expect(panel.attributes('title')).toBe('插件面板：暂无已打开的视图');
	expect(floating.attributes('disabled')).toBeDefined();
	expect(floating.attributes('title')).toBe('悬浮视图：暂无已打开的视图');
	await panel.trigger('click');
	expect(empty.emitted('action')).toBeUndefined();

	const shown = attach(WindowControls, { panelAvailable: true, panelVisible: true, floatingAvailable: true });
	const [docked, floats] = shown.findAll('.tool-button').slice(1);
	expect(docked.attributes('title')).toBe('隐藏插件面板');
	expect(docked.classes()).toContain('on');
	expect(floats.attributes('title')).toBe('隐藏悬浮视图');
	expect(floats.classes()).toContain('on');
	await docked.trigger('click');
	await floats.trigger('click');
	expect(shown.emitted('action')).toEqual([['toggle-panel'], ['toggle-floating']]);

	const hidden = attach(WindowControls, { panelAvailable: true, floatingAvailable: true, floatingVisible: false });
	const [collapsed, restored] = hidden.findAll('.tool-button').slice(1);
	expect(collapsed.attributes('title')).toBe('显示插件面板');
	expect(collapsed.classes()).not.toContain('on');
	expect(restored.attributes('title')).toBe('显示悬浮视图');
	expect(restored.classes()).not.toContain('on');
});
