<script setup>
import { computed, defineAsyncComponent, markRaw, ref } from 'vue';

const emit = defineEmits(['navigate']);

const activeTarget = ref('sys');

const navGroups = [
	{
		label: '系统',
		items: [
			{
				id: 'sys',
				label: '系统设置',
				icon: 'settings',
			},
		],
	},
	{
		label: 'AI 能力',
		items: [
			{
				id: 'providers',
				label: 'AI 服务商',
				icon: 'server',
			},
			{
				id: 'models',
				label: '模型管理',
				icon: 'layers',
			},
			{
				id: 'coding-assistant',
				label: '编程助手',
				icon: 'code',
			},
		],
	},
	{
		label: '扩展与集成',
		items: [
			{
				id: 'plugins',
				label: '插件',
				icon: 'wrench',
			},
			{
				id: 'mcp',
				label: 'MCP 服务器',
				icon: 'network',
			},
			{
				id: 'pet',
				label: '宠物',
				icon: 'paw',
			},
		],
	},
	{
		label: '关于',
		items: [
			{
				id: 'about',
				label: '关于',
				icon: 'info',
			},
		],
	},
];

const activeComponent = computed(() => {
	return componentMap[activeTarget.value] || null;
});

const componentMap = markRaw({
	sys: defineAsyncComponent(() => import('../components/settings/SystemSettings.vue')),
	providers: defineAsyncComponent(() => import('../components/settings/AIProviders.vue')),
	models: defineAsyncComponent(() => import('../components/settings/ModelManagement.vue')),
	'coding-assistant': defineAsyncComponent(() => import('../components/settings/ProgrammingAssistant.vue')),
	plugins: defineAsyncComponent(() => import('../components/settings/Plugins.vue')),
	mcp: defineAsyncComponent(() => import('../components/settings/MCPServers.vue')),
	pet: defineAsyncComponent(() => import('../components/settings/Pet.vue')),
	about: defineAsyncComponent(() => import('../components/settings/About.vue')),
});

const iconMap = {
	settings: `<svg class="ni-ico" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M12.22 2h-.44a2 2 0 0 0-2 2v.18a2 2 0 0 1-1 1.73l-.43.25a2 2 0 0 1-2 0l-.15-.08a2 2 0 0 0-2.73.73l-.22.38a2 2 0 0 0 .73 2.73l.15.1a2 2 0 0 1 1 1.72v.51a2 2 0 0 1-1 1.74l-.15.09a2 2 0 0 0-.73 2.73l.22.38a2 2 0 0 0 2.73.73l.15-.08a2 2 0 0 1 2 0l.43.25a2 2 0 0 1 1 1.73V20a2 2 0 0 0 2 2h.44a2 2 0 0 0 2-2v-.18a2 2 0 0 1 1-1.73l.43-.25a2 2 0 0 1 2 0l.15.08a2 2 0 0 0 2.73-.73l.22-.39a2 2 0 0 0-.73-2.73l-.15-.08a2 2 0 0 1-1-1.74v-.5a2 2 0 0 1 1-1.74l.15-.09a2 2 0 0 0 .73-2.73l-.22-.38a2 2 0 0 0-2.73-.73l-.15.08a2 2 0 0 1-2 0l-.43-.25a2 2 0 0 1-1-1.73V4a2 2 0 0 0-2-2z"/><circle cx="12" cy="12" r="3"/></svg>`,
	server: `<svg class="ni-ico" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="2" y="2" width="20" height="8" rx="2"/><rect x="2" y="14" width="20" height="8" rx="2"/><line x1="6" y1="6" x2="6.01" y2="6"/><line x1="6" y1="18" x2="6.01" y2="18"/></svg>`,
	layers: `<svg class="ni-ico" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M21 16V8a2 2 0 0 0-1-1.73l-7-4a2 2 0 0 0-2 0l-7 4A2 2 0 0 0 3 8v8a2 2 0 0 0 1 1.73l7 4a2 2 0 0 0 2 0l7-4A2 2 0 0 0 21 16z"/><polyline points="3.27 6.96 12 12.01 20.73 6.96"/><line x1="12" y1="22.08" x2="12" y2="12"/></svg>`,
	wrench: `<svg class="ni-ico" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M14.7 6.3a1 1 0 0 0 0 1.4l1.6 1.6a1 1 0 0 0 1.4 0l3.77-3.77a6 6 0 0 1-7.94 7.94l-6.91 6.91a2.12 2.12 0 0 1-3-3l6.91-6.91a6 6 0 0 1 7.94-7.94l-3.76 3.76z"/></svg>`,
	network: `<svg class="ni-ico" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="6" cy="6" r="2.5"/><circle cx="6" cy="18" r="2.5"/><circle cx="18" cy="12" r="2.5"/><path d="M8.5 6H13a2.5 2.5 0 0 1 2.5 2.5v1"/><path d="M8.5 18H13a2.5 2.5 0 0 0 2.5-2.5v-1"/></svg>`,
	info: `<svg class="ni-ico" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="10"/><path d="M12 16v-4"/><path d="M12 8h.01"/></svg>`,
	code: `<svg class="ni-ico" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><polyline points="16 18 22 12 16 6"/><polyline points="8 6 2 12 8 18"/></svg>`,
	paw: `<svg class="ni-ico" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="11" cy="4" r="2"/><circle cx="18" cy="8" r="2"/><circle cx="4" cy="8" r="2"/><path d="M12 22c-4 0-7-2-7-5 0-2 1.5-3.5 3-4l4 2 4-2c1.5.5 3 2 3 4 0 3-3 5-7 5z"/></svg>`,
};

function getIcon(id) {
	return iconMap[id] || '';
}

function selectItem(id) {
	activeTarget.value = id;
	emit('navigate', id);
}
</script>

<template>
	<div class="settings-layout">
		<!-- Left: sidebar -->
		<aside class="settings-sidebar">
			<div class="sb-head">
				<h1>设置</h1>
				<p>偏好与模型设置</p>
			</div>

			<nav class="sb-nav">
				<div v-for="group in navGroups" :key="group.label" class="sb-group">
					<div class="sb-label">{{ group.label }}</div>
					<button v-for="item in group.items" :key="item.id" class="nav-item"
						:class="{ active: activeTarget === item.id }" @click="selectItem(item.id)">
						<span class="ni-ico-wrap" aria-hidden="true" v-html="getIcon(item.icon)"></span>
						{{ item.label }}
					</button>
				</div>
			</nav>
		</aside>

		<!-- Right: content panel -->
		<main class="settings-content">
			<component :is="activeComponent" />
		</main>
	</div>
</template>

<style scoped>
.settings-layout {
	display: flex;
	width: 100%;
	height: 100%;
	background: var(--bg);
	overflow: hidden;
}

.settings-sidebar {
	display: flex;
	flex-direction: column;
	width: 264px;
	min-width: 264px;
	height: 100%;
	border-right: 1px solid var(--border);
	overflow: hidden;
	font-family: var(--font-ui);
}

.settings-content {
	flex: 1;
	min-width: 0;
	height: 100%;
	overflow-y: auto;
	background: var(--bg);
}

.sb-head {
	padding: 20px 20px 14px;
}

.sb-head h1 {
	margin: 0;
	font-family: var(--font-display, var(--font-ui));
	font-size: 19px;
	font-weight: 650;
	letter-spacing: -0.01em;
	line-height: 1.3;
	color: var(--text);
}

.sb-head p {
	margin: 2px 0 0;
	font-size: 12.5px;
	color: var(--muted);
	line-height: 1.4;
}

.sb-nav {
	flex: 1;
	overflow-y: auto;
	padding: 2px 12px 14px;
}

.sb-nav::-webkit-scrollbar {
	width: 9px;
}

.sb-nav::-webkit-scrollbar-thumb {
	background: #d7dae1;
	border-radius: 9px;
	border: 2px solid var(--panel-soft, #f7f8fa);
}

.sb-group {
	margin-top: 14px;
}

.sb-group:first-child {
	margin-top: 0;
}

.sb-label {
	font-size: 11px;
	font-weight: 600;
	letter-spacing: 0.07em;
	text-transform: uppercase;
	color: var(--muted-soft, #8a929e);
	padding: 6px 10px;
	user-select: none;
}

.nav-item {
	display: flex;
	align-items: center;
	gap: 11px;
	width: 100%;
	padding: 8px 10px;
	border-radius: 8px;
	border: 0;
	background: transparent;
	color: var(--muted-soft, #8a929e);
	font-size: 13.5px;
	font-weight: 500;
	font-family: inherit;
	cursor: pointer;
	text-align: left;
	line-height: 1.2;
	transition: background 0.14s cubic-bezier(0.2, 0.7, 0.3, 1),
		color 0.14s cubic-bezier(0.2, 0.7, 0.3, 1);
}

.nav-item:hover {
	background: rgba(23, 26, 31, 0.05);
	color: var(--text);
}

.nav-item.active {
	background: var(--pill, #1f232b);
	color: var(--pill-fg, #ffffff);
	font-weight: 550;
}

.nav-item.active .ni-ico {
	color: var(--pill-fg, #ffffff);
}

.ni-ico {
	width: 17px;
	height: 17px;
	flex: none;
	color: var(--muted);
	pointer-events: none;
}

.ni-ico-wrap {
	display: inline-flex;
	align-items: center;
	justify-content: center;
	width: 17px;
	height: 17px;
	flex: 0 0 auto;
}

.nav-item:hover .ni-ico {
	color: var(--muted-soft, #8a929e);
}


/* Focus visible for keyboard navigation */
.nav-item:focus-visible {
	outline: 2px solid var(--accent);
	outline-offset: 2px;
}

/* Reduced motion for vestibular-sensitive users */
@media (prefers-reduced-motion: reduce) {
	.nav-item {
		transition-duration: 0.001ms !important;
	}
}
</style>
