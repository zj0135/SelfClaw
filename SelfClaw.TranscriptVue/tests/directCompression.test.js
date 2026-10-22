import { beforeEach, describe, expect, it, vi } from 'vitest';
import { mount } from '@vue/test-utils';

// 宿主桥按真实协议回包：get/save 都返回 { type: 'direct-compression/state', requestId, policy }，
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

import Compression from '../src/components/settings/compression/Compression.vue';

const MODELS = [
	{ modelProfileId: 'm-1', name: 'GPT-5', model: 'gpt-5', providerName: 'OpenAI' },
	{ modelProfileId: 'm-2', name: 'Sonnet', model: 'claude-sonnet', providerName: 'Anthropic' },
];

const stateOf = (policy) => ({ type: 'direct-compression/state', requestId: null, policy });

const wait = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

function hostPolicy(overrides = {}) {
	return { enabled: true, thresholdRatio: 0.75, absoluteThresholdTokens: 64000, modelProfileId: null, ...overrides };
}

// 每用例一个假宿主：policy 是可变的真值，save 会归一化比例（模拟 DirectCompressionSettingsService）。
function installHost({ policy = hostPolicy(), saveError = '', normalizeRatio = null } = {}) {
	let current = { ...policy };
	bridge.requestLatest.mockImplementation(async (key, type, payload) => {
		if (type === 'direct-compression/get') return stateOf(current);
		if (type === 'ai-providers/list-enabled-models') return { models: MODELS, defaultModelProfileId: null };
		if (type === 'direct-compression/save') {
			if (saveError) throw new Error(saveError);
			current = normalizeRatio === null
				? { ...payload.policy }
				: { ...payload.policy, thresholdRatio: normalizeRatio };
			return stateOf(current);
		}
		throw new Error(`unexpected message ${type}`);
	});
	return () => current;
}

function saveCalls() {
	return bridge.requestLatest.mock.calls.filter(([, type]) => type === 'direct-compression/save');
}

beforeEach(() => {
	vi.clearAllMocks();
	bridge.reset();
});

describe('Compression settings page', () => {
	it('renders the policy the host reports, including the compression model', async () => {
		installHost({ policy: hostPolicy({ thresholdRatio: 0.6, absoluteThresholdTokens: 32000, modelProfileId: 'm-2' }) });
		const wrapper = mount(Compression);
		await wait(20);

		expect(wrapper.get('[role="switch"]').attributes('aria-checked')).toBe('true');
		expect(wrapper.get('.percent-input').element.value).toBe('60');
		expect(wrapper.get('.tokens-input').element.value).toBe('32000');
		expect(wrapper.get('.model-select').element.value).toBe('m-2');
		expect(wrapper.find('[role="alert"]').exists()).toBe(false);
		wrapper.unmount();
	});

	it('debounces edits into one save and re-renders the normalized echo', async () => {
		installHost({ normalizeRatio: 0.1 });
		const wrapper = mount(Compression);
		await wait(20);

		await wrapper.get('[role="switch"]').trigger('click');
		// 防抖窗口内不发请求。
		expect(saveCalls()).toHaveLength(0);

		await wait(450);
		const calls = saveCalls();
		expect(calls).toHaveLength(1);
		expect(calls[0][2].policy).toMatchObject({ enabled: false, thresholdRatio: 0.75 });
		// 回显是归一化后的值，界面跟着它走（0.75 → 宿主归一化到 0.1 → 10%）。
		expect(wrapper.get('.percent-input').element.value).toBe('10');
		wrapper.unmount();
	});

	it('applies host pushes unless a local edit is still unconfirmed', async () => {
		installHost({ normalizeRatio: 0.9 });
		const wrapper = mount(Compression);
		await wait(20);

		bridge.push('direct-compression/state', stateOf(hostPolicy({ thresholdRatio: 0.4 })));
		await wait(20);
		expect(wrapper.get('.percent-input').element.value).toBe('40');

		await wrapper.get('[role="switch"]').trigger('click');
		bridge.push('direct-compression/state', stateOf(hostPolicy({ thresholdRatio: 0.9 })));
		await wait(20);
		// 未确认的本地编辑优先，推送不能把开关或比例改回去。
		expect(wrapper.get('.percent-input').element.value).toBe('40');
		expect(wrapper.get('[role="switch"]').attributes('aria-checked')).toBe('false');

		await wait(450);
		expect(wrapper.get('.percent-input').element.value).toBe('90');
		wrapper.unmount();
	});

	it('surfaces a host error and keeps the edited value instead of faking success', async () => {
		installHost({ saveError: '设置文件被占用' });
		const wrapper = mount(Compression);
		await wait(20);

		await wrapper.get('[role="switch"]').trigger('click');
		await wait(450);

		expect(wrapper.get('[role="alert"]').text()).toContain('设置文件被占用');
		expect(wrapper.get('[role="switch"]').attributes('aria-checked')).toBe('false');
		wrapper.unmount();
	});
});
