import { expect, it } from 'vitest';
import { describePluginPermission } from '../src/renderers/pluginPermissions.js';

it('expands a network.fetch origin and echoes unknown tokens verbatim', () => {
	expect(describePluginPermission('network.fetch:https://api.example.com')).toContain('https://api.example.com');
	expect(describePluginPermission('workspace.read')).toBe('workspace.read');
});
