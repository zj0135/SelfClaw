<script setup>
import { Inbox } from 'lucide-vue-next';
import ExtensionListItem from './ExtensionListItem.vue';
defineProps({
	items: { type: Array, required: true },
	selectedId: { type: String, default: null },
	kind: { type: String, required: true },
	isPending: { type: Function, required: true },
});
defineEmits(['select', 'toggle']);
</script>

<template>
	<div v-if="items.length" class="list">
		<ExtensionListItem
			v-for="item in items"
			:key="item.id"
			:item="item"
			:selected="item.id === selectedId"
			:pending="isPending(kind, item.id)"
			@select="$emit('select', item)"
			@toggle="$emit('toggle', item, $event)"
		/>
	</div>
	<div v-else class="empty">
		<Inbox :size="22" aria-hidden="true" />
		<strong>暂无匹配项</strong>
		<span>此分类还没有已保存的扩展。</span>
	</div>
</template>

<style scoped>
@import '../../../styles/settings-console.css';
.list { min-height: 0; }
.empty { display: grid; place-items: center; align-content: center; min-height: 280px; gap: 8px; color: var(--sc-faint); text-align: center; }
.empty strong { color: var(--sc-soft); font-size: 13px; }
.empty span { font-size: 12px; }
</style>
