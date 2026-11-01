<script setup>
import { computed, watch } from 'vue';
import { SquareSlash } from 'lucide-vue-next';

// `/` 的指令列表。目前没有任何指令源，`commands` 为空时只呈现空态；有指令源后由上层通过 prop 传入。
const props = defineProps({
	query: { type: String, default: '' },
	commands: { type: Array, default: () => [] },
	activeIndex: { type: Number, default: 0 },
});
const emit = defineEmits(['update:items', 'activate', 'choose']);

const items = computed(() => {
	const query = props.query.trim().toLowerCase();
	const matched = query
		? props.commands.filter((command) => String(command.name || '').toLowerCase().includes(query))
		: props.commands;
	return matched.map((command, index) => ({ ...command, index }));
});
watch(items, (list) => emit('update:items', list), { immediate: true });

const emptyMessage = computed(() => {
	if (!props.commands.length) {
		return '暂无可用指令';
	}

	return items.value.length ? '' : '没有匹配的指令';
});
</script>

<template>
	<div class="command-list" role="listbox" aria-label="指令">
		<p v-if="emptyMessage" class="mention-empty">{{ emptyMessage }}</p>
		<div v-for="item in items" :key="item.name" class="mention-row" :class="{ active: item.index === props.activeIndex }"
			:data-index="item.index" role="option" tabindex="-1" :aria-selected="item.index === props.activeIndex"
			:title="item.description || item.name" @mouseenter="emit('activate', item.index)" @click="emit('choose', item)">
			<span class="mention-row-icon" aria-hidden="true">
				<SquareSlash :size="13" :stroke-width="1.8" />
			</span>
			<span class="mention-row-copy">
				<span class="mention-row-name">/{{ item.name }}</span>
				<span v-if="item.description" class="mention-row-desc">{{ item.description }}</span>
			</span>
		</div>
	</div>
</template>

<style scoped>
@import '../../../../styles/mention-list.css';

.command-list {
	display: grid;
	gap: 1px;
}
</style>
