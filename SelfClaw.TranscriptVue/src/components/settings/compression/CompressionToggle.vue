<script setup>
import CompressionRow from './CompressionRow.vue';

const props = defineProps({
	enabled: { type: Boolean, default: true },
	index: { type: Number, default: 0 },
});

const emit = defineEmits(['update:enabled']);
</script>

<template>
	<CompressionRow label="启用上下文压缩" :index="index"
		hint="关闭后每回合发送完整历史，转录里也不会再出现上下文压缩分隔项。">
		<button type="button" class="switch" role="switch" :aria-checked="props.enabled ? 'true' : 'false'"
			:aria-label="props.enabled ? '关闭上下文压缩' : '启用上下文压缩'"
			@click="emit('update:enabled', !props.enabled)">
			<span class="track" aria-hidden="true"><span class="thumb" /></span>
			<span class="switch-text">{{ props.enabled ? '已启用' : '已关闭' }}</span>
		</button>
	</CompressionRow>
</template>

<style scoped>
.switch {
	display: inline-flex;
	align-items: center;
	gap: 10px;
	padding: 5px 12px 5px 6px;
	border: 1px solid var(--sc-line-2);
	border-radius: 999px;
	background: var(--sc-panel);
	color: var(--sc-mute);
	font: inherit;
	font-size: var(--fs-12);
	font-weight: 600;
	cursor: pointer;
	transition:
		border-color 0.16s,
		background 0.16s,
		color 0.16s;
}

.switch:hover {
	border-color: var(--sc-faint);
	background: var(--sc-hover);
}

.switch:focus-visible {
	outline: none;
	border-color: var(--sc-acid);
	box-shadow: 0 0 0 3px var(--sc-acid-soft);
}

.switch[aria-checked='true'] {
	border-color: color-mix(in srgb, var(--sc-acid) 45%, transparent);
	background: var(--sc-acid-soft);
	color: var(--sc-acid);
}

.track {
	position: relative;
	width: 34px;
	height: 18px;
	flex: 0 0 auto;
	border-radius: 999px;
	background: var(--sc-line-2);
	transition: background 0.18s var(--sc-ease-out);
}

.switch[aria-checked='true'] .track {
	background: var(--sc-acid);
}

.thumb {
	position: absolute;
	top: 2px;
	left: 2px;
	width: 14px;
	height: 14px;
	border-radius: 50%;
	background: var(--sc-panel);
	box-shadow: 0 1px 3px rgba(var(--shadow-ink), 0.25);
	transition: transform 0.2s var(--sc-ease-spring);
}

.switch[aria-checked='true'] .thumb {
	transform: translateX(16px);
}
</style>
