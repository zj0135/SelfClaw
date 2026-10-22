<script setup>
import { AlertCircle } from 'lucide-vue-next';
import PluginFrame from './PluginFrame.vue';
import PluginTabBar from './PluginTabBar.vue';

// 右侧停靠栏：标签栏只管已经打开的 right 视图，加载哪个面板统一走左侧导航的启动器。
// 用 v-show 而不是 v-if 隐藏：收起不该卸载 iframe，否则每次收起都要让插件重新加载并重走握手。
defineProps({
	views: { type: Array, required: true },
	activeKey: { type: String, default: '' },
	error: { type: String, default: '' },
});

defineEmits(['activate', 'close', 'hide', 'register']);
</script>

<template>
	<section class="plugin-dock-host" data-anchor="dock" aria-label="插件面板">
		<PluginTabBar :views="views" :active-key="activeKey" @activate="$emit('activate', $event)"
			@close="$emit('close', $event)" @hide="$emit('hide')" />
		<div v-if="error" class="panel-error">
			<AlertCircle :size="14" />{{ error }}
		</div>
		<div class="frames">
			<div v-for="view in views" :key="view.key" class="frame-slot" :class="{ active: view.key === activeKey }">
				<PluginFrame :view="view" :url="view.url"
					@register="(key, element) => $emit('register', key, element)" />
			</div>
		</div>
	</section>
</template>

<style scoped>
.plugin-dock-host {
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

.frame-slot {
	position: absolute;
	inset: 0;
	display: none;
}

/* 非活动标签保留在 DOM 里（display: none 不卸载文档），切回来不必重新握手。 */
.frame-slot.active {
	display: block;
}
</style>
