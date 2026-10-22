import { computed, ref } from 'vue';

// 判定只看会话标题：用户记得的是自己起的名字，工作区名与时间不参与检索。
function matchesTitle(node, keyword) {
	return typeof node?.label === 'string' && node.label.toLowerCase().includes(keyword);
}

// 工作区文件夹是会话的父级容器：命中就保留整条路径，一个都没命中就连文件夹一起消失。
function filterChildren(children, keyword) {
	return (Array.isArray(children) ? children : [])
		.map((child) => (child.type === 'folder' ? { ...child, children: filterChildren(child.children, keyword) } : child))
		.filter((child) => (child.type === 'folder' ? child.children.length > 0 : matchesTitle(child, keyword)));
}

// 侧栏检索：只过滤「项目」「对话」两个分组里的会话节点，功能入口与设置项原样保留。
export function useSidebarSearch(items) {
	const query = ref('');
	const keyword = computed(() => query.value.trim().toLowerCase());
	const active = computed(() => keyword.value.length > 0);
	const filteredItems = computed(() => {
		if (!active.value) return items.value;

		return items.value
			.map((item) => (item.type === 'group' ? { ...item, children: filterChildren(item.children, keyword.value) } : item))
			.filter((item) => item.type !== 'group' || item.children.length > 0);
	});

	function clear() {
		query.value = '';
	}

	return { query, active, filteredItems, clear };
}
