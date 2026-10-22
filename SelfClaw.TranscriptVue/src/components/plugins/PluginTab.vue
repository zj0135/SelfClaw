<script setup>
import { computed } from 'vue';
import { resolvePluginIcon } from '../../renderers/pluginIcons.js';

const props = defineProps({
	view: { type: Object, required: true },
	active: { type: Boolean, default: false },
});

defineEmits(['activate', 'close']);

const icon = computed(() => resolvePluginIcon(props.view.icon));
</script>

<template>
	<div class="tab" :class="{ active }" role="tab" :aria-selected="active">
		<button class="tab-main" type="button" :title="view.title" @click="$emit('activate', view.key)">
			<span class="tab-icon" aria-hidden="true">
				<component :is="icon" :size="13" :stroke-width="1.8" />
			</span>
			<span class="tab-title">{{ view.title }}</span>
		</button>
		<button class="tab-close" type="button" :aria-label="`关闭 ${view.title}`"
			@click.stop="$emit('close', view.key)">
			<svg viewBox="0 0 12 12" width="11" height="11" aria-hidden="true">
				<path d="M3 3l6 6M9 3l-6 6" stroke="currentColor" stroke-width="1.4" stroke-linecap="round"
					fill="none" />
			</svg>
		</button>
	</div>
</template>

<style scoped>
.tab {
	position: relative;
	display: inline-flex;
	align-items: center;
	max-width: 168px;
	min-width: 0;
	height: 24px;
	flex: 0 1 auto;
	padding-right: 3px;
	border-radius: 999px;
	color: var(--muted);
	transition: background 0.14s, color 0.14s;
}

.tab:hover {
	background: var(--panel-muted);
	color: var(--text);
}

.tab.active {
	background: var(--panel-hover);
	color: var(--text);
}

.tab-main {
	display: inline-flex;
	align-items: center;
	min-width: 0;
	gap: 6px;
	flex: 1 1 auto;
	height: 100%;
	padding: 0 4px 0 9px;
	border: 0;
	background: transparent;
	color: inherit;
	font-size: var(--fs-12);
	font-weight: 560;
}

.tab-icon {
	display: inline-grid;
	flex: none;
	place-items: center;
	color: var(--muted-soft);
}

.tab.active .tab-icon {
	color: var(--accent);
}

.tab-title {
	min-width: 0;
	overflow: hidden;
	text-overflow: ellipsis;
	white-space: nowrap;
}

.tab-close {
	display: grid;
	width: 19px;
	height: 19px;
	flex: none;
	place-items: center;
	border: 0;
	border-radius: 5px;
	background: transparent;
	color: var(--faint);
	opacity: 0;
	transition: background 0.12s, color 0.12s, opacity 0.12s;
}

.tab:hover .tab-close,
.tab.active .tab-close {
	opacity: 1;
}

.tab-close:hover {
	background: var(--border);
	color: var(--text);
}
</style>
