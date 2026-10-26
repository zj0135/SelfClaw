import { beforeEach, describe, expect, it, vi } from 'vitest';
import { mount } from '@vue/test-utils';

// 宿主桥按真实协议回包：get/save 都返回 { type: 'workspace-tools/state', requestId, shellKind, shells }，
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

import WorkspaceTools from '../src/components/settings/tools/WorkspaceTools.vue';

const wait = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

const SHELLS = [
	{ kind: 'WindowsPowerShell', available: true, resolvedPath: 'C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe' },
	{ kind: 'PowerShellSeven', available: true, resolvedPath: 'C:\\Program Files\\PowerShell\\7\\pwsh.exe' },
	{ kind: 'CommandPrompt', available: true, resolvedPath: 'C:\\Windows\\System32\\cmd.exe' },
	{ kind: 'GitBash', available: false, resolvedPath: '' },
];

const stateOf = (shellKind, shells = SHELLS) => ({ type: 'workspace-tools/state', requestId: null, shellKind, shells });

// 每用例一个假宿主：shellKind 是可变的真值，save 直接接受合法 kind。
function installHost({ shellKind = 'WindowsPowerShell', saveError = '' } = {}) {
	let current = shellKind;
	bridge.requestLatest.mockImplementation(async (key, type, payload) => {
		if (type === 'workspace-tools/get') return stateOf(current);
		if (type === 'workspace-tools/save') {
			if (saveError) throw new Error(saveError);
			current = payload.shellKind;
			return stateOf(current);
		}
		throw new Error(`unexpected message ${type}`);
	});
	return () => current;
}

function saveCalls() {
	return bridge.requestLatest.mock.calls.filter(([, type]) => type === 'workspace-tools/save');
}

beforeEach(() => {
	vi.clearAllMocks();
	bridge.reset();
});

describe('Workspace tools settings group', () => {
	it('renders the four shells with availability and the active one marked in select dropdown', async () => {
		installHost({ shellKind: 'PowerShellSeven' });
		const wrapper = mount(WorkspaceTools);
		await wait(20);

		const select = wrapper.find('select.shell-select');
		expect(select.exists()).toBe(true);
		expect(select.element.value).toBe('PowerShellSeven');

		const options = select.findAll('option');
		expect(options).toHaveLength(4);
		expect(options[1].element.value).toBe('PowerShellSeven');
		// 未检测到的 Git Bash 禁用且给出明确文案。
		expect(options[3].attributes('data-available')).toBe('false');
		expect(options[3].attributes('disabled')).toBeDefined();
		expect(options[3].text()).toContain('未检测到');
		wrapper.unmount();
	});

	it('selecting an available shell saves immediately and follows the echo', async () => {
		const current = installHost();
		const wrapper = mount(WorkspaceTools);
		await wait(20);

		const select = wrapper.find('select.shell-select');
		await select.setValue('CommandPrompt');
		await wait(20);

		expect(saveCalls()).toHaveLength(1);
		expect(saveCalls()[0][2]).toEqual({ shellKind: 'CommandPrompt' });
		expect(current()).toBe('CommandPrompt');
		expect(wrapper.find('select.shell-select').element.value).toBe('CommandPrompt');
		wrapper.unmount();
	});

	it('selecting the current or unavailable shell does not save', async () => {
		installHost();
		const wrapper = mount(WorkspaceTools);
		await wait(20);

		const select = wrapper.find('select.shell-select');
		await select.setValue('WindowsPowerShell');
		await wait(20);
		expect(saveCalls()).toHaveLength(0);

		select.element.value = 'GitBash';
		await select.trigger('change');
		await wait(20);
		expect(saveCalls()).toHaveLength(0);

		wrapper.unmount();
	});

	it('applies host pushes', async () => {
		installHost();
		const wrapper = mount(WorkspaceTools);
		await wait(20);

		bridge.push('workspace-tools/state', stateOf('GitBash', SHELLS.map((shell) => ({ ...shell, available: true }))));
		await wait(20);
		expect(wrapper.find('select.shell-select').element.value).toBe('GitBash');
		wrapper.unmount();
	});

	it('surfaces a host error instead of faking success', async () => {
		installHost({ saveError: '设置文件被占用' });
		const wrapper = mount(WorkspaceTools);
		await wait(20);

		const select = wrapper.find('select.shell-select');
		await select.setValue('CommandPrompt');
		await wait(20);

		expect(wrapper.get('[role="alert"]').text()).toContain('设置文件被占用');
		// 保存失败后选中态仍留在原 shell，不假装成功。
		expect(wrapper.find('select.shell-select').element.value).toBe('WindowsPowerShell');
		wrapper.unmount();
	});
});
