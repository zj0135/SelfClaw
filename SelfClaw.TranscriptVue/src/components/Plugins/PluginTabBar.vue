<script setup>
import { Plus } from 'lucide-vue-next';
import PluginTab from './PluginTab.vue';

defineProps({
	tabs: { type: Array, required: true },
	activeKey: { type: String, default: '' },
	canAdd: { type: Boolean, default: false },
});

defineEmits(['activate', 'close', 'add']);
</script>

<template>
	<div class="tab-bar" role="tablist" aria-label="插件面板">
		<div class="tab-strip">
			<PluginTab v-for="tab in tabs" :key="tab.key" :tab="tab" :active="tab.key === activeKey"
				@activate="$emit('activate', $event)" @close="$emit('close', $event)" />
		</div>
		<button v-if="canAdd" class="tab-add" type="button" aria-label="打开面板" title="打开面板" @click="$emit('add')">
			<Plus :size="14" :stroke-width="2" />
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
	border-bottom: 1px solid var(--border, #e5e7eb);
	background: #fafbfd;
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

.tab-add {
	display: grid;
	width: 26px;
	height: 26px;
	flex: none;
	place-items: center;
	border: 0;
	border-radius: 999px;
	background: transparent;
	color: #6b7280;
	transition: background 0.14s, color 0.14s;
}

.tab-add:hover {
	background: #eef0f4;
	color: var(--accent, #3b5bfd);
}
</style>
