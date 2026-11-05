// 侧栏「归档」分组的渲染契约：默认折叠、标题常驻、展开后沿用工作区文件夹形态，
// 空归档点开给一行提示，且没有「新建归档」这种并不存在的操作。
import { describe, expect, it } from 'vitest';
import { mount } from '@vue/test-utils';
import AppSidebar from '../src/components/sidebar/AppSidebar.vue';
import { buildSidebarGroups } from '../src/renderers/sidebarGroups.js';

const NOW = Date.parse('2026-03-10T12:00:00.000Z');
const DAY = 24 * 60 * 60 * 1000;

function conversation(id, { ago = DAY, workspace = null } = {}) {
	return {
		id,
		title: id,
		timestamp: '2026-03-01 08:00',
		workspaceRootId: workspace?.id ?? null,
		workspaceRootName: workspace?.name ?? null,
		workspaceRootPath: workspace?.path ?? null,
		gitRepositoryId: workspace?.gitId ?? null,
		gitRepositoryName: workspace?.gitName ?? null,
		isManagedWorktree: false,
		isRunning: false,
		updatedAtUtc: new Date(NOW - ago).toISOString(),
	};
}

const repoA = { id: 'root-a', name: 'repo-a', path: 'E:\\repo-a', gitId: 'git-a', gitName: 'repo-a' };

function sidebarItems(conversations) {
	const groups = buildSidebarGroups({ conversations, now: NOW });
	return [
		{ id: 'projects', label: '项目', type: 'group', layout: 'folders', canCreate: true, emptyText: '暂无项目会话', children: groups.projects },
		{ id: 'conversations', label: '对话', type: 'group', layout: 'list', canCreate: true, emptyText: '暂无会话记录', children: groups.conversations },
		{ id: 'archived', label: '归档', type: 'group', layout: 'folders', canCreate: false, emptyText: '暂无归档会话', children: groups.archived },
	];
}

function mountSidebar(items, props = {}) {
	return mount(AppSidebar, { props: { items, activeId: null, collapsed: false, ...props } });
}

function archiveSection(wrapper) {
	return wrapper.findAll('.group')[2];
}

function labelsOf(wrapper) {
	return wrapper.findAll('.context-menu-item .context-menu-label').map((node) => node.text());
}

function menuButton(wrapper, label) {
	return wrapper.findAll('.context-menu-item').find((node) => node.text() === label);
}

describe('归档分组渲染', () => {
	it('默认折叠：标题与文件夹计数常驻，但不挂载任何会话节点', () => {
		const wrapper = mountSidebar(
			sidebarItems([
				conversation('old-a', { ago: 40 * DAY, workspace: repoA }),
				conversation('old-plain', { ago: 40 * DAY }),
			]),
		);

		const archive = archiveSection(wrapper);
		expect(archive.find('.group-title').text()).toBe('归档');
		// 计数沿用项目分组的口径：文件夹数（1 个工作区 + 1 个伪文件夹）。
		expect(archive.find('.group-count').text()).toBe('02');
		expect(archive.classes()).not.toContain('open');
		expect(archive.find('.group-body').exists()).toBe(false);
		expect(wrapper.findAll('.node.kind-chat')).toHaveLength(0);
		wrapper.unmount();
	});

	it('归档没有加号：不存在「新建归档」', () => {
		const wrapper = mountSidebar(sidebarItems([conversation('old-a', { ago: 40 * DAY, workspace: repoA })]));

		expect(wrapper.findAll('.group')[0].find('.group-add').exists()).toBe(true);
		expect(archiveSection(wrapper).find('.group-add').exists()).toBe(false);
		wrapper.unmount();
	});

	it('展开后按工作区文件夹列出会话，未绑定工作区的进伪文件夹', async () => {
		const wrapper = mountSidebar(
			sidebarItems([
				conversation('old-a', { ago: 40 * DAY, workspace: repoA }),
				conversation('old-plain', { ago: 40 * DAY }),
			]),
		);

		await archiveSection(wrapper).get('.group-head').trigger('click');

		const folders = archiveSection(wrapper).findAll('.project-folder .folder-name');
		expect(folders.map((node) => node.text())).toEqual(['repo-a', '未绑定工作区']);
		expect(archiveSection(wrapper).findAll('.node.kind-chat .ntext').map((node) => node.text())).toEqual([]);

		await archiveSection(wrapper).findAll('.project-folder')[0].trigger('click');
		expect(archiveSection(wrapper).findAll('.node.kind-chat .ntext').map((node) => node.text())).toEqual(['old-a']);
		wrapper.unmount();
	});

	it('空归档展开后给一行提示，而不是整块消失', async () => {
		const wrapper = mountSidebar(sidebarItems([conversation('fresh', { ago: DAY, workspace: repoA })]));

		await archiveSection(wrapper).get('.group-head').trigger('click');

		expect(archiveSection(wrapper).get('.empty-group').text()).toBe('暂无归档会话');
		wrapper.unmount();
	});

	it('归档文件夹的右键菜单没有「清空会话列表」', async () => {
		const wrapper = mountSidebar(sidebarItems([conversation('old-a', { ago: 40 * DAY, workspace: repoA })]));

		await archiveSection(wrapper).get('.group-head').trigger('click');
		await archiveSection(wrapper).get('.project-folder').trigger('contextmenu');

		expect(labelsOf(wrapper)).toEqual(['工作目录', '在资源管理器打开']);
		expect(menuButton(wrapper, '清空会话列表')).toBeUndefined();
		wrapper.unmount();
	});

	it('未绑定工作区的伪文件夹不弹空菜单', async () => {
		const wrapper = mountSidebar(sidebarItems([conversation('old-plain', { ago: 40 * DAY })]));

		await archiveSection(wrapper).get('.group-head').trigger('click');
		await archiveSection(wrapper).get('.project-folder').trigger('contextmenu');

		expect(wrapper.find('.context-menu').exists()).toBe(false);
		wrapper.unmount();
	});

	it('归档里的会话照常选中：伪文件夹 id 只用于折叠与分页，不会当成会话', async () => {
		const wrapper = mountSidebar(
			sidebarItems([conversation('old-a', { ago: 40 * DAY, workspace: repoA }), conversation('old-plain', { ago: 40 * DAY })]),
		);

		await archiveSection(wrapper).get('.group-head').trigger('click');
		await archiveSection(wrapper).findAll('.project-folder')[0].trigger('click');
		await archiveSection(wrapper).get('.node.kind-chat').trigger('click');

		expect(wrapper.emitted('select')).toEqual([['old-a']]);
		wrapper.unmount();
	});
});
