import { beforeEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount } from '@vue/test-utils';
import SystemSettings from '../src/components/settings/SystemSettings.vue';
import SettingsView from '../src/views/SettingsView.vue';

const { bridge } = vi.hoisted(() => {
	const handlers = new Map();
	return {
		bridge: {
			request: vi.fn(async (type, payload) => {
				if (type === 'appearance/save') {
					return { type: 'appearance/save/response', settings: payload?.settings || {} };
				}
				return {};
			}),
			requestLatest: vi.fn(async (key, type) => {
				if (type === 'direct-compression/state' || type === 'direct-compression/get') {
					return { type: 'direct-compression/state', requestId: null, policy: { enabled: true, thresholdRatio: 0.75, absoluteThresholdTokens: 64000 } };
				}
				if (type === 'workspace-tools/get' || type === 'workspace-tools/state') {
					return {
						type: 'workspace-tools/state',
						requestId: null,
						shellKind: 'WindowsPowerShell',
						shells: [
							{ kind: 'WindowsPowerShell', available: true, resolvedPath: 'C:\\Windows\\System32\\powershell.exe' },
							{ kind: 'PowerShellSeven', available: true, resolvedPath: 'C:\\Program Files\\PowerShell\\7\\pwsh.exe' },
						],
					};
				}
				if (type === 'ai-providers/list-enabled-models') {
					return { models: [], defaultModelProfileId: null };
				}
				return {};
			}),
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

	it('renders 2 top-level groups: 主题与界面排版, 运行时设置', () => {
		const wrapper = mount(SystemSettings);
		const heads = wrapper.findAll('.group-head');
		expect(heads).toHaveLength(2);

		const labels = heads.map((h) => h.find('.gh-label').text());
		expect(labels).toEqual(['主题与界面排版', '运行时设置']);
		wrapper.unmount();
	});

	it('defaults to all groups expanded', () => {
		const wrapper = mount(SystemSettings);
		const heads = wrapper.findAll('.group-head');
		for (const head of heads) {
			expect(head.attributes('aria-expanded')).toBe('true');
		}

		const bodies = wrapper.findAll('.group-body');
		for (const body of bodies) {
			expect(body.isVisible()).toBe(true);
		}
		wrapper.unmount();
	});

	it('nests theme select dropdown, typography, code block and preview inside appearance node', async () => {
		const wrapper = mount(SystemSettings);
		const appearanceHead = wrapper.findAll('.group-head')[0];
		expect(appearanceHead.text()).toContain('主题与界面排版');
		expect(appearanceHead.attributes('aria-expanded')).toBe('true');

		const appearanceBody = wrapper.findAll('.group-body')[0];
		expect(appearanceBody.isVisible()).toBe(true);

		// Contains theme mode select dropdown instead of cards
		expect(appearanceBody.text()).toContain('主题模式');
		const themeSelect = appearanceBody.find('select.theme-select');
		expect(themeSelect.exists()).toBe(true);
		expect(appearanceBody.findAll('.mode-card')).toHaveLength(0);

		// Contains typography fields
		expect(appearanceBody.text()).toContain('界面字体');
		expect(appearanceBody.text()).toContain('界面字号');

		// Contains code fields
		expect(appearanceBody.text()).toContain('代码块');
		expect(appearanceBody.text()).toContain('代码块字体');
		expect(appearanceBody.text()).toContain('代码块背景');

		// Contains preview
		expect(appearanceBody.text()).toContain('预览');
		expect(appearanceBody.find('.pv-title').text()).toContain('界面排版预览');

		// Click to collapse
		await appearanceHead.trigger('click');
		await wrapper.vm.$nextTick();
		expect(appearanceHead.attributes('aria-expanded')).toBe('false');
		expect(appearanceBody.attributes('style')).toContain('display: none');

		wrapper.unmount();
	});

	it('allows changing theme mode via select dropdown', async () => {
		const wrapper = mount(SystemSettings);
		const themeSelect = wrapper.find('select.theme-select');
		expect(themeSelect.exists()).toBe(true);
		await themeSelect.setValue('dark');
		expect(bridge.request).toHaveBeenCalledWith('appearance/save', expect.objectContaining({
			settings: expect.objectContaining({ mode: 'dark' }),
			resolvedTheme: 'dark',
		}));
		wrapper.unmount();
	});

	it('renders runtime settings with workspace shell dropdown and cohesive compression panel', async () => {
		const wrapper = mount(SystemSettings);
		await flushPromises();

		const runtimeHead = wrapper.findAll('.group-head')[1];
		expect(runtimeHead.text()).toContain('运行时设置');
		expect(runtimeHead.attributes('aria-expanded')).toBe('true');

		const runtimeBody = wrapper.findAll('.group-body')[1];
		expect(runtimeBody.isVisible()).toBe(true);

		// Direct shell select row without standalone subgroup title
		expect(runtimeBody.text()).toContain('默认 Shell');
		const shellSelect = runtimeBody.find('select.shell-select');
		expect(shellSelect.exists()).toBe(true);
		expect(runtimeBody.findAll('.shell-card')).toHaveLength(0);

		// Contains context compression section grouped into cohesive panel
		expect(runtimeBody.text()).toContain('上下文压缩');
		const compPanel = runtimeBody.find('.comp-panel');
		expect(compPanel.exists()).toBe(true);

		// 4 cohesive compression settings inside the panel
		expect(compPanel.text()).toContain('启用上下文压缩');
		expect(compPanel.text()).toContain('触发比例');
		expect(compPanel.text()).toContain('绝对阈值');
		expect(compPanel.text()).toContain('压缩模型');

		// Click to collapse
		await runtimeHead.trigger('click');
		await wrapper.vm.$nextTick();
		expect(runtimeHead.attributes('aria-expanded')).toBe('false');
		expect(runtimeBody.attributes('style')).toContain('display: none');
		wrapper.unmount();
	});
});

describe('SettingsView navigation', () => {
	it('does not contain standalone compression or shell page in sidebar navigation', () => {
		const wrapper = mount(SettingsView);
		const navButtons = wrapper.findAll('.nav-item');
		const labels = navButtons.map((b) => b.find('.ni-label').text());
		expect(labels).not.toContain('上下文压缩');
		expect(labels).not.toContain('命令执行工具');
		expect(labels).not.toContain('运行时设置');
		expect(labels).toContain('系统设置');
		expect(labels).toContain('AI 服务商');
		expect(labels).toContain('模型管理');
		expect(labels).toContain('编程助手');
		expect(labels).toContain('代理助手');
		wrapper.unmount();
	});
});
