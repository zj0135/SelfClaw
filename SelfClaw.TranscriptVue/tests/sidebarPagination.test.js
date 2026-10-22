// 侧栏会话分页契约：每个「对话」分组 / 工作区文件夹最多先放出 10 条会话，
// 底部「显示更多」一次多放一页。分页只针对会话，项目分组里的文件夹不受限。
import { expect, it } from 'vitest';
import { mount } from '@vue/test-utils';
import AppSidebar from '../src/components/sidebar/AppSidebar.vue';

function conversation(id, label, workspaceRootId = null) {
	return { id, label, type: 'conversation', workspaceRootId, isManagedWorktree: false };
}

function many(count, prefix, workspaceRootId = null) {
	return Array.from({ length: count }, (_, index) => conversation(`${prefix}-${index}`, `${prefix} 会话 ${index}`, workspaceRootId));
}

function sidebarItems({ conversations = 0, folders = [], projects = [] } = {}) {
	return [
		{ id: 'projects', label: '项目', type: 'group', children: projects },
		{ id: 'conversations', label: '对话', type: 'group', children: many(conversations, 'c') },
		...folders,
	];
}

function mountSidebar(items, props = {}) {
	return mount(AppSidebar, { props: { items, activeId: null, collapsed: false, ...props } });
}

function labels(wrapper) {
	return wrapper.findAll('.node.kind-chat .ntext').map((node) => node.text());
}

it('shows the first ten conversations of the 对话 group and hides the rest behind 显示更多', () => {
	const wrapper = mountSidebar(sidebarItems({ conversations: 25 }));

	expect(labels(wrapper)).toHaveLength(10);
	expect(labels(wrapper)[0]).toBe('c 会话 0');
	expect(labels(wrapper)[9]).toBe('c 会话 9');
	// 「显示更多 >>」只说还有更多，不暴露具体剩余条数。
	expect(wrapper.get('.show-more').text().replace(/\s+/g, '')).toBe('显示更多>>');
	wrapper.unmount();
});

it('adds another ten conversations per 显示更多 click until none are left', async () => {
	const wrapper = mountSidebar(sidebarItems({ conversations: 25 }));

	await wrapper.get('.show-more').trigger('click');
	expect(labels(wrapper)).toHaveLength(20);

	await wrapper.get('.show-more').trigger('click');
	expect(labels(wrapper)).toHaveLength(25);
	expect(wrapper.find('.show-more').exists()).toBe(false);
	wrapper.unmount();
});

it('does not render 显示更多 when a group fits in one page', () => {
	const wrapper = mountSidebar(sidebarItems({ conversations: 10 }));

	expect(labels(wrapper)).toHaveLength(10);
	expect(wrapper.find('.show-more').exists()).toBe(false);
	wrapper.unmount();
});

// 项目分组里的文件夹本身不参与分页：20 个工作区必须全部出现，只有会话被截断。
it('paginates conversations inside each workspace but never the workspace folders', async () => {
	const projects = Array.from({ length: 20 }, (_, index) => ({
		id: `workspace-${index}`,
		label: `repo-${index}`,
		type: 'folder',
		workspaceRootId: `root-${index}`,
		children: many(14, `w${index}`, `root-${index}`),
	}));
	const wrapper = mountSidebar(sidebarItems({ projects }));

	expect(wrapper.findAll('.project-folder')).toHaveLength(20);

	await wrapper.get('.project-folder').trigger('click');
	expect(labels(wrapper)).toHaveLength(10);
	expect(wrapper.get('.subfolder-body .show-more').text().replace(/\s+/g, '')).toBe('显示更多>>');

	await wrapper.get('.subfolder-body .show-more').trigger('click');
	expect(labels(wrapper)).toHaveLength(14);
	expect(wrapper.find('.subfolder-body .show-more').exists()).toBe(false);
	wrapper.unmount();
});

// 每个分支各自记账：展开第二个工作区时，它仍然从 10 条开始。
it('keeps a separate page count per workspace', async () => {
	const projects = ['a', 'b'].map((key) => ({
		id: `workspace-${key}`,
		label: `repo-${key}`,
		type: 'folder',
		workspaceRootId: `root-${key}`,
		children: many(14, key, `root-${key}`),
	}));
	const wrapper = mountSidebar(sidebarItems({ projects }));

	await wrapper.findAll('.project-folder')[0].trigger('click');
	await wrapper.get('.subfolder-body .show-more').trigger('click');
	expect(labels(wrapper)).toHaveLength(14);

	await wrapper.findAll('.project-folder')[1].trigger('click');
	// 第二个工作区刚展开 → 10 条，加上第一个的 14 条。
	expect(labels(wrapper)).toHaveLength(24);
	wrapper.unmount();
});

// 选中的会话排在第一批之外时必须被放出来，否则侧栏看不到当前选中项。
it('reveals the page that holds the selected conversation', () => {
	const wrapper = mountSidebar(sidebarItems({ conversations: 25 }), { activeId: 'c-17' });

	expect(labels(wrapper)).toHaveLength(20);
	expect(wrapper.findAll('.node.kind-chat').some((node) => node.classes('active'))).toBe(true);
	expect(wrapper.find('.show-more').exists()).toBe(true);
	wrapper.unmount();
});

// 检索结果不截断：关键词已经把范围收窄了，再藏起来会让人以为没搜到。
it('shows every matching conversation while searching', async () => {
	const wrapper = mountSidebar(sidebarItems({ conversations: 25 }));

	await wrapper.get('.search-input').setValue('会话 1');
	// 「会话 1」「会话 10」…「会话 19」共 11 条命中，全部渲染且没有「显示更多」。
	expect(labels(wrapper)).toHaveLength(11);
	expect(wrapper.find('.show-more').exists()).toBe(false);
	wrapper.unmount();
});
