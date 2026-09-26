import { expect, it } from 'vitest';
import { mount } from '@vue/test-utils';
import ExtensionToolbar from '../src/components/settings/extensions/ExtensionToolbar.vue';

it('keeps the folder-install action labelled and title-tagged for icon-only layouts', async () => {
	const wrapper = mount(ExtensionToolbar, { props: { category: 'plugin' } });
	const buttons = wrapper.findAll('.primary');

	expect(buttons).toHaveLength(2);
	expect(buttons[0].get('.label').text()).toBe('导入插件');
	expect(buttons[0].attributes('title')).toBe('导入插件');
	expect(buttons[1].get('.label').text()).toBe('从文件夹安装');
	expect(buttons[1].attributes('title')).toBe('从文件夹安装');

	await buttons[1].trigger('click');
	expect(wrapper.emitted('import-plugin-folder')).toHaveLength(1);
});

it('labels the MCP add button the same way', () => {
	const wrapper = mount(ExtensionToolbar, { props: { category: 'mcpServer' } });
	const button = wrapper.get('.primary');

	expect(button.get('.label').text()).toBe('新增服务器');
	expect(button.attributes('title')).toBe('新增服务器');
});
