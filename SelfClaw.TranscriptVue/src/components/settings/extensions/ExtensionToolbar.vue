<script setup>
import { PackageOpen, Plus, RefreshCw, Search, X } from 'lucide-vue-next';
defineProps({
	modelValue: { type: String, default: '' },
	category: { type: String, required: true },
	loading: { type: Boolean, default: false },
});
defineEmits(['update:modelValue', 'refresh', 'add-mcp', 'import-package']);
</script>

<template>
	<div class="toolbar">
		<label class="search">
			<Search :size="15" aria-hidden="true" />
			<input :value="modelValue" type="search" placeholder="搜索名称、ID 或描述"
				@input="$emit('update:modelValue', $event.target.value)" />
			<button v-if="modelValue" type="button" class="clear" title="清除搜索" @click="$emit('update:modelValue', '')">
				<X :size="14" aria-hidden="true" />
			</button>
		</label>
		<button type="button" class="icon-button" title="刷新" :disabled="loading" @click="$emit('refresh')">
			<RefreshCw :size="15" :class="{ spin: loading }" aria-hidden="true" />
		</button>
		<button v-if="category !== 'mcpServer'" type="button" class="primary" @click="$emit('import-package')">
			<PackageOpen :size="15" aria-hidden="true" />{{ category === 'plugin' ? '导入插件' : '导入技能' }}
		</button>
		<button v-if="category === 'mcpServer'" type="button" class="primary" @click="$emit('add-mcp')">
			<Plus :size="15" aria-hidden="true" />新增服务器
		</button>
	</div>
</template>

<style scoped>
@import '../../../styles/settings-console.css';

.toolbar {
	display: flex;
	align-items: center;
	gap: 8px;
	padding: 14px 0;
}

.search {
	display: flex;
	align-items: center;
	flex: 1;
	height: 36px;
	gap: 8px;
	padding: 0 10px;
	border: 1px solid var(--sc-line-2);
	border-radius: 6px;
	background: var(--sc-panel);
	color: var(--sc-faint);
}

input {
	min-width: 0;
	flex: 1;
	border: 0;
	outline: 0;
	background: transparent;
	color: var(--sc-text);
	font: inherit;
	font-size: var(--fs-125);
}

button {
	display: inline-flex;
	align-items: center;
	justify-content: center;
	height: 36px;
	border: 1px solid var(--sc-line-2);
	border-radius: 6px;
	background: var(--sc-panel);
	color: var(--sc-soft);
}

.icon-button {
	width: 36px;
	padding: 0;
}

.clear {
	width: 24px;
	height: 24px;
	padding: 0;
	border: 0;
	background: transparent;
}

.primary {
	gap: 7px;
	padding: 0 12px;
	border-color: var(--sc-acid);
	background: var(--sc-acid);
	color: var(--sc-acid-ink);
}

button:disabled {
	cursor: wait;
	opacity: 0.55;
}

.spin {
	animation: sc-spin 0.8s linear infinite;
}
</style>
