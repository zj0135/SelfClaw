import { afterEach, beforeEach, expect, it, vi } from 'vitest';
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

async function mountRail() {
	const { default: DockRail } = await import('../src/components/dock/DockRail.vue');
	const { useSideDock } = await import('../src/composables/useSideDock.js');
	wrapper = mount(DockRail);
	return useSideDock();
}

it('offers one button per tool, opens the dock to that tool, and collapses it on a second click', async () => {
	const dock = await mountRail();
	const buttons = wrapper.findAll('.dock-rail-button');
	expect(buttons.map((button) => button.attributes('data-tool'))).toEqual(['terminal', 'subagents', 'review']);
	expect(buttons.map((button) => button.attributes('aria-label'))).toEqual(['命令行', '子代理', '审查']);

	await buttons[1].trigger('click');
	expect(dock.state).toMatchObject({ open: true, activeTab: 'subagents', tabs: ['subagents'] });
	expect(wrapper.find('.dock-rail-button[data-tool="subagents"]').classes()).toContain('active');

	await buttons[0].trigger('click');
	expect(dock.state).toMatchObject({ open: true, activeTab: 'terminal', tabs: ['subagents', 'terminal'] });
	expect(requestsOf('toggle-terminal')).toHaveLength(1);
	expect(wrapper.find('.dock-rail-button[data-tool="terminal"]').classes()).toContain('active');

	await buttons[0].trigger('click');
	expect(dock.state.open).toBe(false);
	expect(wrapper.findAll('.dock-rail-button.active')).toHaveLength(0);
});

it('shows a quiet glowing dot on the subagent button only while the conversation has subagent tasks', async () => {
	const dock = await mountRail();
	expect(wrapper.find('.dock-rail-dot').exists()).toBe(false);

	dock.setSubagentCounts({ total: 2, running: 1 });
	await flushPromises();
	const dot = wrapper.find('.dock-rail-button[data-tool="subagents"] .dock-rail-dot');
	expect(dot.exists()).toBe(true);
	expect(dot.text()).toBe('');
	expect(dot.classes()).toContain('running');

	dock.setSubagentCounts({ total: 2, running: 0 });
	await flushPromises();
	expect(wrapper.find('.dock-rail-dot').classes()).not.toContain('running');

	dock.setSubagentCounts(null);
	await flushPromises();
	expect(wrapper.find('.dock-rail-dot').exists()).toBe(false);
	expect(wrapper.find('.dock-rail-button[data-tool="terminal"] .dock-rail-dot').exists()).toBe(false);
});
