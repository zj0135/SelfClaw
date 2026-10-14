import { beforeEach, expect, it, vi } from 'vitest';

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

import { useExtensionSettings } from '../src/composables/useExtensionSettings.js';

const registryState = () => ({ revision: 1, activeAgentId: null, agents: [], plugins: [], skills: [], mcpServers: [], panels: [] });

beforeEach(() => {
	bridge.request.mockReset();
	bridge.requestLatest.mockReset();
	bridge.on.mockReturnValue(() => {});
	bridge.requestLatest.mockResolvedValue({ state: registryState() });
});

it('imports a package without a timeout and refreshes the registry', async () => {
	const settings = useExtensionSettings();
	const response = { ok: true, cancelled: false, package: { id: 'office' }, revision: 2 };
	bridge.request.mockResolvedValueOnce(response);

	const result = await settings.importPackage('plugin');

	expect(bridge.request).toHaveBeenCalledWith('extensions/import-package', { kind: 'plugin' }, { timeout: 0 });
	expect(result).toBe(response);
	expect(bridge.requestLatest).toHaveBeenCalledWith('extensions/get-state', 'extensions/get-state');
});

it('returns null when the package import is cancelled', async () => {
	const settings = useExtensionSettings();
	bridge.request.mockResolvedValueOnce({ ok: false, cancelled: true });

	expect(await settings.importPackage('plugin')).toBeNull();
});
