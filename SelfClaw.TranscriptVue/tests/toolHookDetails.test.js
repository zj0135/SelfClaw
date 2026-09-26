import { expect, it } from 'vitest';
import { mount } from '@vue/test-utils';
import ToolHookDetails from '../src/components/Chat/transcript/ToolHookDetails.vue';

function emptyHook(overrides = {}) {
	return {
		effectiveArgumentsText: null,
		argumentsModifiedBy: [],
		approvalRequiredBy: [],
		blockedBy: null,
		blockReason: null,
		feedback: [],
		ignoredFailures: [],
		originalArgumentsText: null,
		...overrides,
	};
}

it('renders nothing when a tool run recorded no hook intervention', () => {
	const wrapper = mount(ToolHookDetails, { props: { hook: emptyHook() } });
	expect(wrapper.find('.tool-hook').exists()).toBe(false);
});

it('renders a block reason with the blocking hook source', () => {
	const wrapper = mount(ToolHookDetails, {
		props: { hook: emptyHook({ blockedBy: 'shell-guard/deny-dangerous', blockReason: 'Destructive command.' }) },
	});
	expect(wrapper.get('.hook-note.blocked').text()).toContain('shell-guard/deny-dangerous');
	expect(wrapper.get('.hook-note-reason').text()).toBe('Destructive command.');
});

it('renders the original and effective arguments next to their modifiers', () => {
	const wrapper = mount(ToolHookDetails, {
		props: {
			hook: emptyHook({
				originalArgumentsText: '{\n  "command": "rm -rf ."\n}',
				effectiveArgumentsText: '{\n  "command": "ls"\n}',
				argumentsModifiedBy: ['shell-guard/rewrite'],
			}),
		},
	});
	const columns = wrapper.findAll('.hook-arguments-column');
	expect(columns).toHaveLength(2);
	expect(columns[0].text()).toContain('原始参数');
	expect(columns[0].text()).toContain('rm -rf .');
	expect(columns[1].text()).toContain('实际执行参数');
	expect(columns[1].text()).toContain('ls');
	expect(wrapper.get('.hook-modifiers').text()).toContain('shell-guard/rewrite');
});

it('renders approval requesters, feedback, and ignored failures independently', () => {
	const approval = mount(ToolHookDetails, {
		props: { hook: emptyHook({ approvalRequiredBy: ['a/ask'] }) },
	});
	expect(approval.get('.hook-note.approval').text()).toContain('a/ask');

	const feedback = mount(ToolHookDetails, {
		props: { hook: emptyHook({ feedback: [{ source: 'lint/check', text: 'Quote the path.' }] }) },
	});
	expect(feedback.get('.hook-list.feedback').text()).toContain('lint/check');
	expect(feedback.get('.hook-list.feedback').text()).toContain('Quote the path.');

	const ignored = mount(ToolHookDetails, {
		props: { hook: emptyHook({ ignoredFailures: [{ source: 'audit/run', text: 'timedOut' }] }) },
	});
	expect(ignored.get('.hook-list.ignored').text()).toContain('audit/run');
	expect(ignored.get('.hook-list.ignored').text()).toContain('timedOut');
	expect(ignored.get('.hook-list.ignored').text()).toContain('已忽略');
});

it('renders injected plugin text as plain text, never as markup', () => {
	const wrapper = mount(ToolHookDetails, {
		props: { hook: emptyHook({ feedback: [{ source: 'x/y', text: '<img src=x onerror=alert(1)>' }] }) },
	});
	expect(wrapper.find('.hook-list.feedback img').exists()).toBe(false);
	expect(wrapper.get('.hook-text').text()).toBe('<img src=x onerror=alert(1)>');
});
