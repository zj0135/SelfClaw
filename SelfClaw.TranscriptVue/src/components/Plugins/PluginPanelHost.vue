<script setup>
import { AlertCircle } from 'lucide-vue-next';
import PluginFrame from './PluginFrame.vue';
import PluginTabBar from './PluginTabBar.vue';

defineProps({
	tabs: { type: Array, required: true },
	activeKey: { type: String, default: '' },
	error: { type: String, default: '' },
	canAdd: { type: Boolean, default: false },
});

defineEmits(['activate', 'close', 'add', 'register']);
</script>

<template>
	<section class="plugin-panel-host" aria-label="插件面板">
		<PluginTabBar :tabs="tabs" :active-key="activeKey" :can-add="canAdd" @activate="$emit('activate', $event)"
			@close="$emit('close', $event)" @add="$emit('add')" />
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
	background: #ffffff;
}

.panel-error {
	display: flex;
	align-items: center;
	gap: 7px;
	flex: none;
	padding: 8px 12px;
	background: rgba(220, 69, 69, 0.07);
	color: var(--danger, #dc4545);
	font-size: 11.5px;
}

.frames {
	position: relative;
	min-height: 0;
	flex: 1 1 auto;
}
</style>
