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

it('imports a plugin folder without a timeout and refreshes the registry', async () => {
	const settings = useExtensionSettings();
	const response = { ok: true, cancelled: false, package: { id: 'hook-examples' }, revision: 2 };
	bridge.request.mockResolvedValueOnce(response);

	const result = await settings.importPluginFolder();

	expect(bridge.request).toHaveBeenCalledWith('extensions/import-plugin-folder', {}, { timeout: 0 });
	expect(result).toBe(response);
	expect(bridge.requestLatest).toHaveBeenCalledWith('extensions/get-state', 'extensions/get-state');
});

it('returns null when the folder picker is cancelled', async () => {
	const settings = useExtensionSettings();
	bridge.request.mockResolvedValueOnce({ ok: false, cancelled: true });

	expect(await settings.importPluginFolder()).toBeNull();
});

it('reports a reload that left the content unchanged', async () => {
	const settings = useExtensionSettings();
	const response = { ok: true, changed: false, package: { id: 'office' }, revision: 3 };
	bridge.request.mockResolvedValueOnce(response);

	const result = await settings.reloadPlugin('office');

	expect(bridge.request).toHaveBeenCalledWith('extensions/reload-plugin', { id: 'office' }, { timeout: 0 });
	expect(result.changed).toBe(false);
});

it('returns hook log entries and surfaces a transport failure', async () => {
	const settings = useExtensionSettings();
	bridge.request.mockResolvedValueOnce({ entries: [{ hookId: 'guard' }] });
	expect(await settings.getHookLog('office')).toEqual([{ hookId: 'guard' }]);

	bridge.request.mockRejectedValueOnce(new Error('host offline'));
	expect(await settings.getHookLog('office')).toBeNull();
	expect(settings.error.value).toBe('host offline');
});
