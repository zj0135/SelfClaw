<script setup>
defineProps({
	items: {
		type: Array,
		default: () => [],
	},
	activeId: {
		type: String,
		default: null,
	},
});

const emit = defineEmits(['select']);

function selectItem(item) {
	emit('select', item.id);
}
</script>

<template>
	<aside class="app-sidebar">
		<nav class="app-sidebar-nav" aria-label="主导航">
			<ul class="app-sidebar-list">
				<li v-for="item in items" :key="item.id" class="app-sidebar-item">
					<button
						class="app-sidebar-button"
						:class="{ active: activeId === item.id }"
						type="button"
						@click="selectItem(item)"
					>
						<span v-if="item.icon" class="app-sidebar-icon" aria-hidden="true">{{ item.icon }}</span>
						<span class="app-sidebar-label">{{ item.label }}</span>
					</button>
				</li>
			</ul>
		</nav>
	</aside>
</template>

<style scoped>
.app-sidebar {
	width: 100%;
	height: 100%;
	display: flex;
	flex-direction: column;
	background: #F4F5F7;
	border-right: 1px solid #D9DDE4;
	overflow: hidden;
}

.app-sidebar-nav {
	flex: 1 1 auto;
	padding: 16px 12px;
	overflow-y: auto;
}

.app-sidebar-list {
	list-style: none;
	margin: 0;
	padding: 0;
	display: flex;
	flex-direction: column;
	gap: 4px;
}

.app-sidebar-item {
	margin: 0;
}

.app-sidebar-button {
	width: 100%;
	display: inline-flex;
	align-items: center;
	gap: 10px;
	padding: 9px 12px;
	border: 0;
	border-radius: 8px;
	background: transparent;
	color: #374151;
	font-size: 13px;
	font-weight: 500;
	text-align: left;
	cursor: pointer;
	transition: background 120ms ease, color 120ms ease;
}

.app-sidebar-button:hover {
	background: #E5E7EB;
	color: #111827;
}

.app-sidebar-button.active {
	background: #ffffff;
	color: #111827;
	box-shadow: 0 1px 2px rgba(23, 26, 31, 0.06);
}

.app-sidebar-icon {
	display: inline-flex;
	align-items: center;
	justify-content: center;
	width: 18px;
	height: 18px;
	flex: 0 0 auto;
	font-size: 14px;
	line-height: 1;
}
</style>
