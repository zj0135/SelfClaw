import { afterEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount } from '@vue/test-utils';

const { bridge } = vi.hoisted(() => ({
	bridge: {
		request: vi.fn(),
		requestLatest: vi.fn(),
		on: vi.fn(() => () => {}),
		post: vi.fn(),
		hasHost: () => true,
	},
}));

vi.mock('../src/composables/hostBridge.js', () => ({
	hostBridge: bridge,
	useHostBridge: () => bridge,
	isSuperseded: () => false,
}));

import ComposerAddMenu from '../src/components/Chat/ComposerAddMenu.vue';

const mounted = [];
afterEach(() => {
	mounted.splice(0).forEach((wrapper) => wrapper.unmount());
	bridge.requestLatest.mockReset();
	bridge.post.mockReset();
});

describe('ComposerAddMenu agent and skill flyouts', () => {
	it('displays the agent item labeled "代理助手 · build" right above "功能设置"', async () => {
		const wrapper = mount(ComposerAddMenu, {
			props: {
				skillsEnabled: true,
				selectedAgentId: 'build',
				selectedAgentName: 'build',
			},
		});
		mounted.push(wrapper);

		// Click the '+' trigger to open menu
		await wrapper.get('.add-trigger').trigger('click');

		const items = wrapper.findAll('.add-item');
		const itemTexts = items.map((i) => i.text().replace(/\s+/g, ' ').trim());

		// 顺序：添加图片、插入技能、代理助手 · build、功能设置
		expect(itemTexts[0]).toContain('添加图片');
		expect(itemTexts[1]).toContain('插入技能');
		expect(itemTexts[2]).toContain('代理助手 · build');
		expect(itemTexts[3]).toContain('功能设置');
	});

	it('opens the agent flyout on hover and selects an agent on click', async () => {
		bridge.requestLatest.mockResolvedValue({
			agents: [
				{ id: 'build', name: 'build', mode: 'direct' },
				{ id: 'code-review', name: 'Code Reviewer', mode: 'cli' },
			],
		});

		const wrapper = mount(ComposerAddMenu, {
			props: {
				skillsEnabled: true,
				selectedAgentId: 'build',
				selectedAgentName: 'build',
			},
		});
		mounted.push(wrapper);

		await wrapper.get('.add-trigger').trigger('click');

		// Hover over agent item
		const agentRow = wrapper.findAll('.add-item').find((i) => i.text().includes('代理助手'));
		await agentRow.trigger('mouseenter');
		await flushPromises();

		expect(bridge.requestLatest).toHaveBeenCalledWith('composer-agents', 'agents/list-composer-agents');
		expect(wrapper.find('.agents-flyout').exists()).toBe(true);

		const agentButtons = wrapper.findAll('.agent-flyout-item');
		expect(agentButtons).toHaveLength(2);

		// Click the second agent
		await agentButtons[1].trigger('click');
		expect(bridge.post).toHaveBeenCalledWith({ type: 'select-agent', agentId: 'code-review' });

		// Menu closes
		expect(wrapper.find('.add-menu').isVisible()).toBe(false);
	});

	it('opens the skill flyout on hover and emits select-skill on click', async () => {
		bridge.requestLatest.mockResolvedValue({
			skills: [
				{ id: 'git-commit', name: 'Git Commit', description: 'Auto commits' },
			],
		});

		const wrapper = mount(ComposerAddMenu, {
			props: {
				skillsEnabled: true,
				selectedAgentId: 'build',
				selectedAgentName: 'build',
			},
		});
		mounted.push(wrapper);

		await wrapper.get('.add-trigger').trigger('click');

		// Hover over skills item
		const skillRow = wrapper.findAll('.add-item').find((i) => i.text().includes('插入技能'));
		await skillRow.trigger('mouseenter');
		await flushPromises();

		expect(bridge.requestLatest).toHaveBeenCalledWith('effective-skills', 'extensions/list-effective-skills', { agentId: 'build' });
		expect(wrapper.find('.skills-flyout').exists()).toBe(true);

		const skillButton = wrapper.find('.skill-flyout-item');
		expect(skillButton.exists()).toBe(true);
		expect(skillButton.text()).toContain('Git Commit');

		await skillButton.trigger('click');
		expect(wrapper.emitted('select-skill')).toEqual([['git-commit']]);

		// Menu closes
		expect(wrapper.find('.add-menu').isVisible()).toBe(false);
	});
});
