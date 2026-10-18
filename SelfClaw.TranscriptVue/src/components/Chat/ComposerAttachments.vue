<script setup>
import { ImageOff, X } from 'lucide-vue-next';
import { formatAttachmentSize } from '../../renderers/transcript.js';

// 待发送图片条：只负责呈现宿主回的引用与转发用户意图，落盘/丢弃都在 composable 里。
const props = defineProps({
	items: { type: Array, default: () => [] },
	error: { type: String, default: '' },
});
const emit = defineEmits(['remove', 'preview-image']);

function preview(item) {
	if (!item?.previewUrl) {
		return;
	}

	emit('preview-image', { src: item.previewUrl, alt: item.fileName });
}
</script>

<template>
	<div v-if="props.items.length || props.error" class="attachments">
		<ul class="attachment-strip" role="list" aria-label="待发送图片">
			<TransitionGroup name="chip">
				<li v-for="item in props.items" :key="item.attachmentId" class="attachment-chip">
					<button v-if="item.previewUrl" class="chip-thumb" type="button"
						:aria-label="`预览 ${item.fileName}`" :title="item.fileName" @click="preview(item)">
						<img :src="item.previewUrl" :alt="item.fileName" loading="lazy" />
					</button>
					<span v-else class="chip-thumb missing" :title="`${item.fileName}（预览不可用）`" aria-hidden="true">
						<ImageOff :size="17" :stroke-width="1.7" />
					</span>
					<span class="chip-copy">
						<span class="chip-name" :title="item.fileName">{{ item.fileName }}</span>
						<span class="chip-size">{{ formatAttachmentSize(item.byteLength) }}</span>
					</span>
					<button class="chip-remove" type="button" :aria-label="`移除 ${item.fileName}`" title="移除图片"
						@click="emit('remove', item.attachmentId)">
						<X :size="12" :stroke-width="2.4" aria-hidden="true" />
					</button>
				</li>
			</TransitionGroup>
		</ul>
		<p v-if="props.error" class="attachment-error" role="alert">{{ props.error }}</p>
	</div>
</template>

<style scoped>
.attachments {
	display: flex;
	flex-direction: column;
	gap: 4px;
	margin: 0 -2px 8px;
}

.attachment-strip {
	display: flex;
	align-items: stretch;
	gap: 8px;
	margin: 0;
	padding: 2px 2px 4px;
	list-style: none;
	overflow-x: auto;
	scrollbar-width: thin;
	scroll-snap-type: x proximity;
}

.attachment-chip {
	position: relative;
	display: inline-flex;
	align-items: center;
	gap: 8px;
	flex: none;
	max-width: 216px;
	padding: 5px 6px 5px 5px;
	border: 1px solid color-mix(in srgb, var(--text) 11%, transparent);
	border-radius: 12px;
	background: var(--panel-muted);
	scroll-snap-align: start;
	transition: border-color 0.16s, background 0.16s, transform 0.16s cubic-bezier(0.34, 1.4, 0.64, 1);
}

.attachment-chip:hover {
	border-color: color-mix(in srgb, var(--accent) 32%, transparent);
	background: color-mix(in srgb, var(--accent) 6%, var(--panel-muted));
	transform: translateY(-1px);
}

.chip-thumb {
	display: inline-grid;
	place-items: center;
	width: 40px;
	height: 40px;
	flex: none;
	padding: 0;
	overflow: hidden;
	border: 0;
	border-radius: 8px;
	background: color-mix(in srgb, var(--text) 7%, transparent);
	color: var(--muted);
	cursor: pointer;
}

.chip-thumb img {
	width: 100%;
	height: 100%;
	object-fit: cover;
	display: block;
}

.chip-thumb.missing {
	cursor: default;
	background:
		repeating-linear-gradient(135deg,
			color-mix(in srgb, var(--text) 7%, transparent) 0 6px,
			color-mix(in srgb, var(--text) 3%, transparent) 6px 12px);
}

.chip-copy {
	display: flex;
	flex-direction: column;
	gap: 1px;
	min-width: 0;
}

.chip-name {
	overflow: hidden;
	max-width: 132px;
	color: var(--text-soft);
	font-size: var(--fs-115);
	font-weight: 550;
	line-height: 1.3;
	text-overflow: ellipsis;
	white-space: nowrap;
}

.chip-size {
	color: var(--muted-soft);
	font-size: var(--fs-10);
	line-height: 1.2;
	letter-spacing: 0.01em;
}

.chip-remove {
	display: inline-grid;
	place-items: center;
	width: 20px;
	height: 20px;
	flex: none;
	margin-left: 2px;
	padding: 0;
	border: 0;
	border-radius: 50%;
	background: transparent;
	color: var(--muted-soft);
	cursor: pointer;
	transition: background 0.14s, color 0.14s;
}

.chip-remove:hover {
	background: color-mix(in srgb, var(--err-text) 16%, transparent);
	color: var(--err-text);
}

.attachment-error {
	margin: 0 2px;
	color: var(--err-text);
	font-size: var(--fs-11);
	line-height: 1.35;
}

.chip-enter-active,
.chip-leave-active {
	transition: opacity 0.16s ease, transform 0.16s cubic-bezier(0.34, 1.4, 0.64, 1);
}

.chip-enter-from,
.chip-leave-to {
	opacity: 0;
	transform: scale(0.94);
}

@media (prefers-reduced-motion: reduce) {

	.chip-enter-active,
	.chip-leave-active,
	.attachment-chip {
		transition: none;
	}
}
</style>
