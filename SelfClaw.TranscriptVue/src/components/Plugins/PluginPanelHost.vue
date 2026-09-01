<script setup>
import { AlertCircle } from 'lucide-vue-next';
import PluginFrame from './PluginFrame.vue';
import PluginTabBar from './PluginTabBar.vue';

defineProps({
	tabs: { type: Array, required: true },
	activeKey: { type: String, default: '' },
	error: { type: String, default: '' },
});

defineEmits(['activate', 'close', 'hide', 'register']);
</script>

<template>
	<section class="plugin-panel-host" aria-label="插件面板">
		<PluginTabBar :tabs="tabs" :active-key="activeKey" @activate="$emit('activate', $event)"
			@close="$emit('close', $event)" @hide="$emit('hide')" />
		<div v-if="error" class="panel-error">
			<AlertCircle :size="14" />{{ error }}
		</div>
		<div class="frames">
			<PluginFrame v-for="tab in tabs" :key="tab.key" :tab="tab" :active="tab.key === activeKey"
				@register="(key, element) => $emit('register', key, element)" />
		</div>
	</section>
</template>

<style scoped>
.plugin-panel-host {
	display: flex;
	flex-direction: column;
	min-width: 0;
	height: 100%;
	overflow: hidden;
	background: var(--panel);
}

.panel-error {
	display: flex;
	align-items: center;
	gap: 7px;
	flex: none;
	padding: 8px 12px;
	background: color-mix(in srgb, var(--danger) 7%, transparent);
	color: var(--danger);
	font-size: var(--fs-115);
}

.frames {
	position: relative;
	min-height: 0;
	flex: 1 1 auto;
}
</style>
