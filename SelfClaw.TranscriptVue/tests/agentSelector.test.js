import { afterEach, expect, it, vi } from 'vitest';
import { flushPromises, mount } from '@vue/test-utils';

const { bridge } = vi.hoisted(() => ({
	bridge: { request: vi.fn(), requestLatest: vi.fn(), on: vi.fn(() => () => {}), post: vi.fn(), hasHost: () => true },
}));
vi.mock('../src/composables/hostBridge.js', () => ({ hostBridge: bridge, useHostBridge: () => bridge, isSuperseded: () => false }));

import AgentSelector from '../src/components/Chat/AgentSelector.vue';

const mounted = [];
afterEach(() => {
	mounted.splice(0).forEach((wrapper) => wrapper.unmount());
	bridge.requestLatest.mockReset();
});

// 代理列表是按需拉取的，但选中名由 transcript 推送，标签不应等列表加载完才显示。
it('shows the host-pushed selected agent name without waiting for the lazy list fetch', () => {
	const wrapper = mount(AgentSelector, { props: { selectedAgentId: 'build', selectedAgentName: 'Build Agent' } });
	mounted.push(wrapper);
	expect(wrapper.get('.agent-name').text()).toBe('Build Agent');
	expect(bridge.requestLatest).not.toHaveBeenCalled();
});

it('falls back to the loading placeholder only when no name is known, then resolves it from the list', async () => {
	bridge.requestLatest.mockResolvedValue({ agents: [{ id: 'build', name: 'Build Agent', mode: 'direct' }] });
	const wrapper = mount(AgentSelector, { props: { selectedAgentId: 'build' } });
	mounted.push(wrapper);
	expect(wrapper.get('.agent-name').text()).toBe('加载中…');

	await wrapper.get('.composer-agent').trigger('click');
	await flushPromises();
	expect(wrapper.get('.agent-name').text()).toBe('Build Agent');
});
