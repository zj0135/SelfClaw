<script setup>
import { ref, watch } from 'vue';

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

const emit = defineEmits(['select', 'action']);

const expandedGroups = ref(new Set());

function isGroup(item) {
	return item.type === 'group';
}

function isExpanded(group) {
	return expandedGroups.value.has(group.id);
}

function toggleGroup(group) {
	const next = new Set(expandedGroups.value);
	if (next.has(group.id)) {
		next.delete(group.id);
	} else {
		next.add(group.id);
	}

	expandedGroups.value = next;
}

function onItemClick(item) {
	if (isGroup(item)) {
		toggleGroup(item);
		return;
	}

	if (item.type === 'action') {
		emit('action', item.id);
		return;
	}

	emit('select', item.id);
}

function expandGroups(items, activeId) {
	for (const item of items) {
		if (isGroup(item)) {
			expandedGroups.value.add(item.id);
			if (item.children?.some((child) => child.id === activeId)) {
				expandedGroups.value.add(item.id);
			}
		}
	}
}

watch(
	() => [props.items, props.activeId],
	([items]) => {
		expandGroups(items, props.activeId);
	},
	{ immediate: true, deep: true }
);

const iconMap = {
	'new-chat': '<svg viewBox="0 0 20 20" aria-hidden="true"><path d="M10 4v12M4 10h12" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round"/></svg>',
	'search': '<svg viewBox="0 0 20 20" aria-hidden="true"><circle cx="9" cy="9" r="5.5" fill="none" stroke="currentColor" stroke-width="1.8"/><path d="M14 14l3 3" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round"/></svg>',
	'plugins': '<svg viewBox="0 0 20 20" aria-hidden="true"><path d="M6.5 3.5h3v4h-3zM10.5 8.5h4v3h-4zM3.5 12.5h3v4h-3z" fill="none" stroke="currentColor" stroke-width="1.6"/></svg>',
	'extensions': '<svg viewBox="0 0 20 20" aria-hidden="true"><path d="M10 3v4M10 13v4M3 10h4M13 10h4" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round"/></svg>',
	'automation': '<svg viewBox="0 0 20 20" aria-hidden="true"><path d="M5.5 12.5a3 3 0 1 1 0-5 3 3 0 0 1 0 5zm9 0a3 3 0 1 1 0-5 3 3 0 0 1 0 5z" fill="none" stroke="currentColor" stroke-width="1.6"/><path d="M8.5 10h3" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round"/></svg>',
};

function getIcon(item) {
	if (item.icon) {
		return item.icon;
	}

	return iconMap[item.id] || '';
}
</script>

<template>
	<aside class="app-sidebar">
		<nav class="app-sidebar-nav" aria-label="主导航">
			<ul class="app-sidebar-list">
				<template v-for="item in items" :key="item.id">
					<li class="app-sidebar-item" :class="{ group: isGroup(item) }">
						<button
							class="app-sidebar-button"
							:class="{ active: activeId === item.id }"
							type="button"
							@click="onItemClick(item)"
						>
							<span
								v-if="getIcon(item)"
								class="app-sidebar-icon"
								aria-hidden="true"
								v-html="getIcon(item)"
							></span>
							<span class="app-sidebar-label">{{ item.label }}</span>
							<span
								v-if="isGroup(item)"
								class="app-sidebar-chevron"
								:class="{ open: isExpanded(item) }"
								aria-hidden="true"
							>›</span>
						</button>
						<ul v-if="isGroup(item) && isExpanded(item)" class="app-sidebar-children">
							<li v-for="child in item.children" :key="child.id" class="app-sidebar-item">
								<button
									class="app-sidebar-button app-sidebar-child-button"
									:class="{ active: activeId === child.id }"
									type="button"
									@click="onItemClick(child)"
								>
									<span class="app-sidebar-label">{{ child.label }}</span>
								</button>
							</li>
						</ul>
					</li>
				</template>
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
	color: inherit;
}

.app-sidebar-icon svg {
	width: 100%;
	height: 100%;
}

.app-sidebar-label {
	min-width: 0;
	overflow: hidden;
	text-overflow: ellipsis;
	white-space: nowrap;
}

.app-sidebar-chevron {
	margin-left: auto;
	color: #6b7280;
	font-size: 16px;
	line-height: 1;
	transition: transform 120ms ease;
}

.app-sidebar-chevron.open {
	transform: rotate(90deg);
}

.app-sidebar-children {
	list-style: none;
	margin: 0;
	padding: 2px 0 2px 22px;
	display: flex;
	flex-direction: column;
	gap: 2px;
}

.app-sidebar-child-button {
	padding: 7px 10px;
	font-size: 12.5px;
}

.app-sidebar-child-button .app-sidebar-label {
	font-weight: 400;
}
</style>
