import { expect, it } from 'vitest';
import {
	describePluginPermission,
	hasHookPermissions,
	isSensitivePluginPermission,
} from '../src/renderers/pluginPermissions.js';

it('describes every hooks permission the host enforces', () => {
	expect(describePluginPermission('hooks.run')).toContain('回合开始');
	expect(describePluginPermission('hooks.tool')).toContain('工具调用');
	expect(describePluginPermission('hooks.http')).toContain('请求头');
	expect(describePluginPermission('hooks.http.body')).toContain('请求体');
});

it('expands a network.fetch origin and echoes unknown tokens verbatim', () => {
	expect(describePluginPermission('network.fetch:https://api.example.com')).toContain('https://api.example.com');
	expect(describePluginPermission('workspace.read')).toBe('workspace.read');
});

it('flags the request-body permission as sensitive and detects any hooks grant', () => {
	expect(isSensitivePluginPermission('hooks.http.body')).toBe(true);
	expect(isSensitivePluginPermission('hooks.tool')).toBe(false);
	expect(hasHookPermissions(['ui.panel', 'hooks.tool'])).toBe(true);
	expect(hasHookPermissions(['ui.panel'])).toBe(false);
});
