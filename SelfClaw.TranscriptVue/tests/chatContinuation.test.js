import { afterEach, expect, it } from 'vitest';
import { mount } from '@vue/test-utils';
import ComposerPanel from '../src/components/Chat/ComposerPanel.vue';

const mounted = [];
afterEach(() => mounted.splice(0).forEach((wrapper) => wrapper.unmount()));

function attach(props) {
	// The composer children reach for host bridges of their own; this test only owns the send/stop rule.
	const wrapper = mount(ComposerPanel, {
		props,
		global: {
			stubs: {
				ComposerStatusBar: true, ModelSelector: true, AgentSelector: true,
				PermissionSelector: true, ComposerAddMenu: true,
			},
		},
	});
	mounted.push(wrapper);
	return wrapper;
}

it('offers stop for a foreground turn', () => {
	const wrapper = attach({ busy: true, stoppable: true });

	expect(wrapper.find('.send-btn.stop').exists()).toBe(true);
	expect(wrapper.find('.send-btn.background-busy').exists()).toBe(false);
	expect(wrapper.find('.send-btn:not(.stop):not(.background-busy)').exists()).toBe(false);
});

it('replaces stop with progress while a background continuation cannot be stopped', () => {
	const wrapper = attach({ busy: true, stoppable: false });

	expect(wrapper.find('.send-btn.stop').exists()).toBe(false);
	const busy = wrapper.find('.send-btn.background-busy');
	expect(busy.exists()).toBe(true);
	expect(busy.attributes('title')).toBe('正在处理子代理结果');
});

it('shows the send button when the conversation is idle', () => {
	const wrapper = attach({ busy: false, stoppable: true });

	expect(wrapper.find('.send-btn.stop').exists()).toBe(false);
	expect(wrapper.find('.send-btn.background-busy').exists()).toBe(false);
	expect(wrapper.find('.send-btn').exists()).toBe(true);
});
