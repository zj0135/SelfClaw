<script setup>
import { Webhook } from 'lucide-vue-next';

defineProps({
	segment: { type: Object, required: true },
});
</script>

<template>
	<div class="notice-block" role="note">
		<Webhook class="notice-icon" :size="12" :stroke-width="1.8" aria-hidden="true" />
		<span class="notice-text">{{ segment.text }}</span>
	</div>
</template>

<style scoped>
.notice-block {
	display: flex;
	align-items: baseline;
	gap: 6px;
	padding: 5px 8px;
	border-radius: 6px;
	background: var(--panel-soft);
	color: var(--muted);
	font-size: var(--fs-12);
	line-height: 1.5;
	overflow-wrap: anywhere;
}

.notice-icon {
	flex: none;
	transform: translateY(2px);
	color: var(--muted-soft);
}

.notice-text {
	white-space: pre-wrap;
}
</style>
