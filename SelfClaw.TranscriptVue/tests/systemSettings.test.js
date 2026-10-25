import { beforeEach, describe, expect, it, vi } from 'vitest';
import { mount } from '@vue/test-utils';
import SystemSettings from '../src/components/settings/SystemSettings.vue';
import SettingsView from '../src/views/SettingsView.vue';

const { bridge } = vi.hoisted(() => {
	const handlers = new Map();
	return {
		bridge: {
			request: vi.fn(),
			requestLatest: vi.fn(async () => ({ type: 'direct-compression/state', requestId: null, policy: { enabled: true, thresholdRatio: 0.75, absoluteThresholdTokens: 64000 } })),
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

describe('SystemSettings component', () => {
	beforeEach(() => {
		vi.clearAllMocks();
	});

	it('renders 4 top-level groups: 主题, 界面排版, 命令执行工具, 上下文压缩', () => {
		const wrapper = mount(SystemSettings);
		const heads = wrapper.findAll('.group-head');
		expect(heads).toHaveLength(4);

		const labels = heads.map((h) => h.find('.gh-label').text());
		expect(labels).toEqual(['主题', '界面排版', '命令执行工具', '上下文压缩']);
		wrapper.unmount();
	});

	it('defaults to all groups collapsed', () => {
		const wrapper = mount(SystemSettings);
		const heads = wrapper.findAll('.group-head');
		for (const head of heads) {
			expect(head.attributes('aria-expanded')).toBe('false');
		}

		const bodies = wrapper.findAll('.group-body');
		for (const body of bodies) {
			expect(body.isVisible()).toBe(false);
		}
		wrapper.unmount();
	});

	it('nests code block and preview inside typography node', async () => {
		const wrapper = mount(SystemSettings);
		const typographyHead = wrapper.findAll('.group-head')[1];
		expect(typographyHead.text()).toContain('界面排版');

		// Click to expand
		await typographyHead.trigger('click');
		expect(typographyHead.attributes('aria-expanded')).toBe('true');

		const typographyBody = wrapper.findAll('.group-body')[1];
		expect(typographyBody.isVisible()).toBe(true);

		// Contains typography fields
		expect(typographyBody.text()).toContain('界面字体');
		expect(typographyBody.text()).toContain('界面字号');

		// Contains code fields
		expect(typographyBody.text()).toContain('代码块');
		expect(typographyBody.text()).toContain('代码块字体');
		expect(typographyBody.text()).toContain('代码块背景');

		// Contains preview
		expect(typographyBody.text()).toContain('预览');
		expect(typographyBody.find('.pv-title').text()).toContain('界面排版预览');

		wrapper.unmount();
	});

	it('renders the workspace shell group and expands it on click', async () => {
		const wrapper = mount(SystemSettings);
		const shellHead = wrapper.findAll('.group-head')[2];
		expect(shellHead.text()).toContain('命令执行工具');
		expect(shellHead.attributes('aria-expanded')).toBe('false');

		await shellHead.trigger('click');
		expect(shellHead.attributes('aria-expanded')).toBe('true');

		const shellBody = wrapper.findAll('.group-body')[2];
		expect(shellBody.isVisible()).toBe(true);
		expect(shellBody.text()).toContain('run_shell_command');
		wrapper.unmount();
	});

	it('renders context compression without card styling and toggles on click', async () => {
		const wrapper = mount(SystemSettings);
		const compressionHead = wrapper.findAll('.group-head')[3];
		expect(compressionHead.text()).toContain('上下文压缩');
		expect(compressionHead.attributes('aria-expanded')).toBe('false');

		// Click to expand
		await compressionHead.trigger('click');
		expect(compressionHead.attributes('aria-expanded')).toBe('true');

		const compressionBody = wrapper.findAll('.group-body')[3];
		expect(compressionBody.isVisible()).toBe(true);
		expect(compressionBody.text()).toContain('启用上下文压缩');
		expect(compressionBody.text()).toContain('触发比例');

		// Click again to collapse
		await compressionHead.trigger('click');
		await wrapper.vm.$nextTick();
		expect(compressionHead.attributes('aria-expanded')).toBe('false');
		expect(compressionBody.attributes('style')).toContain('display: none');
		wrapper.unmount();
	});
});

describe('SettingsView navigation', () => {
	it('does not contain standalone compression page in sidebar navigation', () => {
		const wrapper = mount(SettingsView);
		const navButtons = wrapper.findAll('.nav-item');
		const labels = navButtons.map((b) => b.find('.ni-label').text());
		expect(labels).not.toContain('上下文压缩');
		expect(labels).toContain('系统设置');
		expect(labels).toContain('AI 服务商');
		expect(labels).toContain('模型管理');
		expect(labels).toContain('编程助手');
		expect(labels).toContain('代理助手');
		wrapper.unmount();
	});
});
