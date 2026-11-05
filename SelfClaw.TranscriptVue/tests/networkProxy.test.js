import { beforeEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount } from '@vue/test-utils';

// 宿主桥按真实协议回包：get/save 都返回 { type: 'network-proxy/state', requestId, mode, host, port }，
// 宿主主动推送同形状但 requestId 为 null。
const { bridge } = vi.hoisted(() => {
	const handlers = new Map();
	return {
		bridge: {
			request: vi.fn(),
			requestLatest: vi.fn(),
			post: vi.fn(),
			hasHost: () => true,
			on: (type, handler) => {
				handlers.set(type, handler);
				return () => handlers.delete(type);
			},
			push: (type, payload) => handlers.get(type)?.(payload),
			reset: () => handlers.clear(),
		},
	};
});

vi.mock('../src/composables/hostBridge.js', () => ({
	hostBridge: bridge,
	useHostBridge: () => bridge,
	isSuperseded: (error) => error?.name === 'SupersededError',
}));

import Network from '../src/components/settings/network/Network.vue';

const stateOf = (mode, host = '', port = 0) => ({ type: 'network-proxy/state', requestId: null, mode, host, port });

// 每用例一个假宿主：出口是可变的真值，save 直接接受合法取值。
function installHost({ mode = 'System', host = '', port = 0, saveError = '' } = {}) {
	let current = { mode, host, port };
	bridge.requestLatest.mockImplementation(async (key, type, payload) => {
		if (type === 'network-proxy/get') return stateOf(current.mode, current.host, current.port);
		if (type === 'network-proxy/save') {
			if (saveError) throw new Error(saveError);
			current = { mode: payload.mode, host: payload.host, port: payload.port };
			return stateOf(current.mode, current.host, current.port);
		}
		throw new Error(`unexpected message ${type}`);
	});
	return () => current;
}

function saveCalls() {
	return bridge.requestLatest.mock.calls.filter(([, type]) => type === 'network-proxy/save');
}

describe('network proxy settings panel', () => {
	beforeEach(() => {
		vi.clearAllMocks();
		bridge.reset();
	});

	it('loads the stored exit and hides the endpoint fields unless custom is selected', async () => {
		installHost({ mode: 'System' });
		const wrapper = mount(Network);
		await flushPromises();

		expect(wrapper.find('select.net-select').element.value).toBe('System');
		expect(wrapper.findAll('input.net-input')).toHaveLength(0);
		wrapper.unmount();
	});

	it('saves the mode as soon as it is picked', async () => {
		const current = installHost({ mode: 'System' });
		const wrapper = mount(Network);
		await flushPromises();

		await wrapper.find('select.net-select').setValue('Direct');
		await flushPromises();

		expect(saveCalls()).toHaveLength(1);
		expect(current().mode).toBe('Direct');
		wrapper.unmount();
	});

	it('shows the single endpoint field to the left of the select for a custom exit and saves a completed endpoint', async () => {
		const current = installHost({ mode: 'Custom', host: '127.0.0.1', port: 7890 });
		const wrapper = mount(Network);
		await flushPromises();

		const inputs = wrapper.findAll('input.net-input');
		expect(inputs).toHaveLength(1);
		expect(inputs[0].attributes('placeholder')).toBe('127.0.0.1:7890');
		expect(inputs[0].element.value).toBe('127.0.0.1:7890');

		await inputs[0].setValue('127.0.0.1:1080');
		await inputs[0].trigger('change');
		await flushPromises();

		expect(saveCalls().at(-1)[2]).toMatchObject({ mode: 'Custom', host: '127.0.0.1', port: 1080 });
		expect(current().port).toBe(1080);
		wrapper.unmount();
	});

	it('does not save an incomplete custom endpoint', async () => {
		installHost({ mode: 'System' });
		const wrapper = mount(Network);
		await flushPromises();

		await wrapper.find('select.net-select').setValue('Custom');
		await flushPromises();

		expect(saveCalls()).toHaveLength(0);
		expect(wrapper.find('input.net-input').exists()).toBe(true);
		expect(wrapper.find('input.net-input').attributes('placeholder')).toBe('127.0.0.1:7890');
		wrapper.unmount();
	});

	it('follows a host push while there is no pending edit', async () => {
		installHost({ mode: 'System' });
		const wrapper = mount(Network);
		await flushPromises();

		bridge.push('network-proxy/state', stateOf('Direct'));
		await flushPromises();

		expect(wrapper.find('select.net-select').element.value).toBe('Direct');
		wrapper.unmount();
	});

	it('surfaces a save failure', async () => {
		installHost({ mode: 'System', saveError: '保存失败' });
		const wrapper = mount(Network);
		await flushPromises();

		await wrapper.find('select.net-select').setValue('Direct');
		await flushPromises();

		expect(wrapper.find('.net-error').text()).toContain('保存失败');
		wrapper.unmount();
	});
});
