<script setup>
import { computed, onMounted, watch } from 'vue';
import { BookOpenCheck } from 'lucide-vue-next';
import { useEffectiveSkills } from '../../../../composables/useEffectiveSkills.js';

// `$` 的技能列表：当前 Agent 绑定的有效技能，与「+」菜单共用 useEffectiveSkills 的缓存。
const props = defineProps({
	query: { type: String, default: '' },
	agentId: { type: String, default: '' },
	capabilityRevision: { type: Number, default: 0 },
	activeIndex: { type: Number, default: 0 },
});
const emit = defineEmits(['update:items', 'activate', 'choose']);

const { skills, loading, error, load } = useEffectiveSkills(() => props.agentId, () => props.capabilityRevision);
onMounted(load);
watch(() => [props.agentId, props.capabilityRevision], load);

const items = computed(() => {
	const query = props.query.trim().toLowerCase();
	const matched = query
		? skills.value.filter((skill) => `${skill.id} ${skill.name || ''}`.toLowerCase().includes(query))
		: skills.value;
	return matched.map((skill, index) => ({ ...skill, index }));
});
watch(items, (list) => emit('update:items', list), { immediate: true });

const emptyMessage = computed(() => {
	if (error.value) {
		return error.value;
	}

	if (loading.value && !skills.value.length) {
		return '正在读取技能…';
	}

	if (!skills.value.length) {
		return '当前 Agent 没有可用技能';
	}

	return items.value.length ? '' : '没有匹配的技能';
});
</script>

<template>
	<div class="skill-list" role="listbox" aria-label="技能">
		<p v-if="emptyMessage" class="mention-empty" :class="{ 'mention-empty--error': Boolean(error) }">{{ emptyMessage }}</p>
		<div v-for="item in items" :key="item.id" class="mention-row" :class="{ active: item.index === props.activeIndex }"
			:data-index="item.index" role="option" tabindex="-1" :aria-selected="item.index === props.activeIndex"
			:title="item.description || item.name || item.id" @mouseenter="emit('activate', item.index)" @click="emit('choose', item)">
			<span class="mention-row-icon" aria-hidden="true">
				<BookOpenCheck :size="13" :stroke-width="1.8" />
			</span>
			<span class="mention-row-copy">
				<span class="mention-row-name">{{ item.name || item.id }}</span>
				<span v-if="item.description" class="mention-row-desc">{{ item.description }}</span>
			</span>
			<code class="mention-row-tag">{{ item.id }}</code>
		</div>
	</div>
</template>

<style scoped>
@import '../../../../styles/mention-list.css';

.skill-list {
	display: grid;
	gap: 1px;
}
</style>
