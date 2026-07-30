<script setup>
import { computed, defineAsyncComponent, markRaw, ref } from 'vue';
import {
	Settings2,
	Server,
	Layers,
	Code2,
	Puzzle,
	PawPrint,
	Info,
	ArrowUpRight,
} from 'lucide-vue-next';

const emit = defineEmits(['navigate']);

const activeTarget = ref('about');

const navGroups = [
	{
		label: '系统',
		en: 'SYSTEM',
		items: [
			{ id: 'sys', label: '系统设置', en: 'GENERAL', icon: Settings2 },
		],
	},
	{
		label: 'AI',
		en: 'INTELLIGENCE',
		items: [
			{ id: 'providers', label: 'AI 服务商', en: 'PROVIDERS', icon: Server },
			{ id: 'models', label: '模型管理', en: 'MODELS', icon: Layers },
			{ id: 'coding-assistant', label: '编程助手', en: 'ASSISTANT', icon: Code2 },
		],
	},
	{
		label: '扩展',
		en: 'EXTENSIONS',
		items: [
			{ id: 'plugins', label: '插件', en: 'PLUGINS', icon: Puzzle },
			{ id: 'pet', label: '宠物', en: 'COMPANION', icon: PawPrint },
		],
	},
	{
		label: '关于',
		en: 'INFO',
		items: [
			{ id: 'about', label: '关于', en: 'ABOUT', icon: Info },
		],
	},
];

// Flat, stable index used for the mono numeric label on each nav row.
let navSeq = 0;
for (const group of navGroups) {
	for (const item of group.items) {
		item.index = String(++navSeq).padStart(2, '0');
	}
}

const componentMap = markRaw({
	sys: defineAsyncComponent(() => import('../components/settings/SystemSettings.vue')),
	providers: defineAsyncComponent(() => import('../components/settings/AIProviders.vue')),
	models: defineAsyncComponent(() => import('../components/settings/ModelManagement.vue')),
	'coding-assistant': defineAsyncComponent(() => import('../components/settings/ProgrammingAssistant.vue')),
	plugins: defineAsyncComponent(() => import('../components/settings/extensions/ExtensionSettingsPanel.vue')),
	pet: defineAsyncComponent(() => import('../components/settings/Pet.vue')),
	about: defineAsyncComponent(() => import('../components/settings/About.vue')),
});

const activeComponent = computed(() => componentMap[activeTarget.value] || null);

const activeItem = computed(() => {
	for (const group of navGroups) {
		const hit = group.items.find((item) => item.id === activeTarget.value);
		if (hit) return hit;
	}
	return null;
});

function selectItem(id) {
	activeTarget.value = id;
	emit('navigate', id);
}
</script>

<template>
	<div class="settings-layout sc-root">
		<!-- Left: sidebar -->
		<aside class="settings-sidebar">
			<div class="sb-head sc-rise" style="--i: 0">
				<div class="sb-kicker">SELFCLAW · CONTROL DECK</div>
				<h1>设置</h1>
				<p>偏好、模型与扩展的控制舱</p>
			</div>

			<nav class="sb-nav">
				<div v-for="(group, gi) in navGroups" :key="group.label" class="sb-group sc-rise" :style="{ '--i': gi + 1 }">
					<div class="sb-label">
						<span>{{ group.label }}</span>
						<span class="sb-label-en">{{ group.en }}</span>
					</div>
					<button
						v-for="item in group.items"
						:key="item.id"
						class="nav-item"
						:class="{ active: activeTarget === item.id }"
						@click="selectItem(item.id)"
					>
						<span class="ni-index">{{ item.index }}</span>
						<component :is="item.icon" :size="16" :stroke-width="1.9" class="ni-ico" aria-hidden="true" />
						<span class="ni-label">{{ item.label }}</span>
						<ArrowUpRight :size="13" :stroke-width="2" class="ni-arrow" aria-hidden="true" />
					</button>
				</div>
			</nav>

			<div class="sb-foot">
				<span class="sb-foot-dot" aria-hidden="true"></span>
				<span class="sb-foot-text">{{ activeItem ? activeItem.en : 'READY' }}</span>
			</div>
		</aside>

		<!-- Right: content panel -->
		<main class="settings-content">
			<component :is="activeComponent" />
		</main>
	</div>
</template>

<style scoped>
@import '../components/settings/settings-console.css';

.settings-layout {
	display: flex;
	width: 100%;
	height: 100%;
	overflow: hidden;
	background: var(--sc-bg);
	color: var(--sc-text);
	font-family: var(--sc-sans);
}

.settings-sidebar {
	display: flex;
	flex-direction: column;
	width: 272px;
	min-width: 272px;
	height: 100%;
	border-right: 1px solid var(--sc-line);
	background: var(--sc-panel);
	overflow: hidden;
}

.settings-content {
	flex: 1;
	min-width: 0;
	height: 100%;
	overflow-y: auto;
	background: var(--sc-bg);
}

.sb-head {
	padding: 30px 24px 20px;
	border-bottom: 1px solid var(--sc-line);
}

.sb-kicker {
	margin-bottom: 14px;
	color: var(--sc-faint);
	font-family: var(--sc-mono);
	font-size: 10px;
	font-weight: 600;
	letter-spacing: 0.22em;
}

.sb-head h1 {
	margin: 0;
	font-family: var(--sc-display);
	font-size: 34px;
	font-weight: 650;
	letter-spacing: 0.02em;
	line-height: 1.1;
}

.sb-head p {
	margin: 8px 0 0;
	color: var(--sc-mute);
	font-size: 12.5px;
	line-height: 1.5;
}

.sb-nav {
	flex: 1;
	overflow-y: auto;
	padding: 10px 14px 16px;
}

.sb-nav::-webkit-scrollbar {
	width: 9px;
}

.sb-nav::-webkit-scrollbar-thumb {
	background: var(--sc-raise);
	border: 2px solid var(--sc-panel);
	border-radius: 9px;
}

.sb-group {
	margin-top: 20px;
}

.sb-group:first-child {
	margin-top: 6px;
}

.sb-label {
	display: flex;
	align-items: baseline;
	justify-content: space-between;
	padding: 6px 10px 8px;
	color: var(--sc-mute);
	font-size: 11px;
	font-weight: 600;
	letter-spacing: 0.06em;
	user-select: none;
}

.sb-label-en {
	color: var(--sc-faint);
	font-family: var(--sc-mono);
	font-size: 9px;
	font-weight: 500;
	letter-spacing: 0.18em;
}

.nav-item {
	position: relative;
	display: flex;
	align-items: center;
	gap: 10px;
	width: 100%;
	padding: 9px 10px;
	border: 1px solid transparent;
	border-radius: 9px;
	background: transparent;
	color: var(--sc-mute);
	font-family: inherit;
	font-size: 13.5px;
	font-weight: 500;
	text-align: left;
	line-height: 1.2;
	cursor: pointer;
	transition:
		background 0.16s var(--sc-ease-out),
		border-color 0.16s var(--sc-ease-out),
		color 0.16s var(--sc-ease-out),
		transform 0.16s var(--sc-ease-out);
}

.nav-item:hover {
	background: var(--sc-hover);
	color: var(--sc-text);
	transform: translateX(2px);
}

.ni-index {
	color: var(--sc-faint);
	font-family: var(--sc-mono);
	font-size: 10px;
	font-weight: 500;
	letter-spacing: 0.08em;
	transition: color 0.16s;
}

.ni-ico {
	flex: none;
	transition: color 0.16s, transform 0.2s var(--sc-ease-spring);
}

.ni-label {
	flex: 1;
	min-width: 0;
	overflow: hidden;
	text-overflow: ellipsis;
	white-space: nowrap;
}

.ni-arrow {
	flex: none;
	opacity: 0;
	transform: translate(-4px, 4px);
	transition:
		opacity 0.16s,
		transform 0.2s var(--sc-ease-spring);
}

.nav-item:hover .ni-arrow {
	opacity: 0.6;
	transform: none;
}

.nav-item.active {
	border-color: var(--sc-line-2);
	background: var(--sc-raise);
	color: var(--sc-text);
}

.nav-item.active::before {
	position: absolute;
	top: 50%;
	left: -1px;
	width: 2px;
	height: 18px;
	transform: translateY(-50%);
	border-radius: 2px;
	background: var(--sc-acid);
	box-shadow: 0 0 12px rgba(59, 91, 253, 0.45);
	content: '';
}

.nav-item.active .ni-index {
	color: var(--sc-acid);
}

.nav-item.active .ni-ico {
	color: var(--sc-acid);
}

.nav-item.active .ni-arrow {
	opacity: 1;
	transform: none;
	color: var(--sc-mute);
}

.nav-item:focus-visible {
	outline: 2px solid var(--sc-acid);
	outline-offset: 2px;
}

.sb-foot {
	display: flex;
	align-items: center;
	gap: 8px;
	padding: 14px 24px;
	border-top: 1px solid var(--sc-line);
}

.sb-foot-dot {
	width: 6px;
	height: 6px;
	border-radius: 50%;
	background: var(--sc-acid);
	box-shadow: 0 0 10px rgba(59, 91, 253, 0.5);
	animation: sc-blink 2.4s ease-in-out infinite;
}

.sb-foot-text {
	color: var(--sc-faint);
	font-family: var(--sc-mono);
	font-size: 10px;
	font-weight: 500;
	letter-spacing: 0.22em;
}
</style>
