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
/* 回合级 hook 通知：与工具行同构的纯文本行，靠左侧发丝色条标注，不做底色卡片。 */
.notice-block {
	display: flex;
	align-items: baseline;
	gap: 7px;
	padding: 3px 0 3px 10px;
	border-left: 2px solid var(--line-2);
	color: var(--muted);
	font-size: var(--fs-115);
	line-height: 1.55;
	overflow-wrap: anywhere;
}

.notice-icon {
	flex: none;
	transform: translateY(1.5px);
	color: var(--muted-soft);
}

.notice-text {
	white-space: pre-wrap;
}
</style>
