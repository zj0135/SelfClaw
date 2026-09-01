<script setup>
import { Minus } from 'lucide-vue-next';
import PluginTab from './PluginTab.vue';

defineProps({
	tabs: { type: Array, required: true },
	activeKey: { type: String, default: '' },
});

// 打开面板统一走左侧导航；这里只留隐藏。隐藏不关闭标签，iframe 继续活着。
defineEmits(['activate', 'close', 'hide']);
</script>

<template>
	<div class="tab-bar" role="tablist" aria-label="插件面板">
		<div class="tab-strip">
			<PluginTab v-for="tab in tabs" :key="tab.key" :tab="tab" :active="tab.key === activeKey"
				@activate="$emit('activate', $event)" @close="$emit('close', $event)" />
		</div>
		<button class="tab-hide" type="button" aria-label="隐藏面板" title="隐藏面板" @click="$emit('hide')">
			<Minus :size="13" :stroke-width="2" />
		</button>
	</div>
</template>

<style scoped>
.tab-bar {
	display: flex;
	align-items: center;
	min-width: 0;
	gap: 4px;
	height: 38px;
	flex: none;
	padding: 0 6px;
	border-bottom: 1px solid var(--border);
	background: var(--surface-sidebar);
}

.tab-strip {
	display: flex;
	align-items: center;
	min-width: 0;
	gap: 2px;
	flex: 1 1 auto;
	overflow-x: auto;
	overflow-y: hidden;
	scrollbar-width: none;
}

.tab-strip::-webkit-scrollbar {
	display: none;
}

/* 静息态就画出边框与底色：这颗按钮是右栏唯一的出口，靠 hover 才显形的话找不到它。 */
.tab-hide {
	display: grid;
	width: 22px;
	height: 22px;
	flex: none;
	place-items: center;
	/* 项目没有全局 button reset，UA 默认的 padding（Chromium 是 1px 6px）在这个尺寸下会把
	   字形顶偏。place-items 居中的是 grid 区域而不是 padding 盒内的内容，纠不回来。
	   box-sizing 让 22px 连边框一起算，否则实际盒子是 24px。 */
	padding: 0;
	box-sizing: border-box;
	border: 1px solid var(--border-strong);
	border-radius: 6px;
	background: var(--panel);
	color: var(--muted);
	box-shadow: 0 1px 2px rgba(var(--shadow-ink), 0.05);
	transition: background 0.14s, border-color 0.14s, color 0.14s;
}

.tab-hide:hover {
	border-color: var(--accent);
	background: var(--panel-hover);
	color: var(--accent);
}

.tab-hide:active {
	background: var(--card-border);
}
</style>
