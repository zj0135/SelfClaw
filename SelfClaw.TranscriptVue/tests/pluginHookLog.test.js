import { expect, it } from 'vitest';
import { mount } from '@vue/test-utils';
import PluginHookLog from '../src/components/settings/extensions/PluginHookLog.vue';

it('shows the empty state and still offers a manual refresh', () => {
	const wrapper = mount(PluginHookLog, { props: { entries: [] } });
	expect(wrapper.get('.empty').text()).toBe('暂无执行记录（重启后清空）');
	expect(wrapper.find('.rows').exists()).toBe(false);
	wrapper.get('header button').trigger('click');
	expect(wrapper.emitted('refresh')).toHaveLength(1);
});

it('renders outcome, duration, exit code and detail for each entry', () => {
	const wrapper = mount(PluginHookLog, {
		props: {
			entries: [
				{
					timestampUtc: '2026-09-25T08:00:00.000Z',
					pluginId: 'shell-guard',
					hookId: 'deny-dangerous',
					event: 'toolExecuting',
					turnId: null,
					outcome: 'blocked',
					durationMs: 1500,
					exitCode: 0,
					detail: 'Blocked rm -rf.',
					stderrTail: null,
				},
			],
		},
	});
	const entry = wrapper.get('.entry');
	expect(entry.get('.entry-hook').text()).toBe('deny-dangerous');
	expect(entry.get('.entry-event').text()).toBe('toolExecuting');
	expect(entry.get('.entry-outcome').text()).toBe('blocked');
	expect(entry.get('.entry-outcome').classes()).toContain('blocked');
	expect(entry.get('.entry-duration').text()).toBe('1.5s');
	expect(entry.get('.entry-detail').text()).toBe('Blocked rm -rf.');
	expect(entry.find('.stderr-toggle').exists()).toBe(false);
});

it('collapses the stderr tail until the operator expands it', async () => {
	const wrapper = mount(PluginHookLog, {
		props: {
			entries: [
				{
					timestampUtc: '2026-09-25T08:00:00.000Z',
					pluginId: 'shell-guard',
					hookId: 'audit-run',
					event: 'runCompleted',
					turnId: null,
					outcome: 'failed',
					durationMs: 12,
					exitCode: 1,
					detail: null,
					stderrTail: 'boom',
				},
			],
		},
	});
	expect(wrapper.find('.stderr').exists()).toBe(false);
	await wrapper.get('.stderr-toggle').trigger('click');
	expect(wrapper.get('.stderr').text()).toBe('boom');
});
