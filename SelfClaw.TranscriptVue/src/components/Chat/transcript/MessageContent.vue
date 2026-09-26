<script setup>
import { computed } from 'vue';
import { buildRenderBlocks, formatAttachmentSize } from '../../../renderers/transcript.js';
import MessageBlocks from './MessageBlocks.vue';

const props = defineProps({
	item: { type: Object, required: true },
	activityText: { type: String, default: '' },
	// 折叠状态的单一载体，由 ChatView 顶层创建后一路传入（见 useTranscriptCollapse）。
	collapse: { type: Object, required: true },
});

const emit = defineEmits(['preview-image']);

const blocks = computed(() => buildRenderBlocks(props.item));

const attachments = computed(() => {
	const list = Array.isArray(props.item.attachments) ? props.item.attachments : [];
	return list
		.filter((attachment) => attachment && String(attachment.mediaType || '').startsWith('image/'))
		.map((attachment) => ({
			fileName: attachment.fileName || 'image',
			size: formatAttachmentSize(attachment.byteLength),
			sourceUrl: attachment.sourceUrl || attachment.dataUrl || '',
		}));
});

const hasContent = computed(() => blocks.value.length > 0 || Boolean(props.item.errorMessage));
const isPreparing = computed(() => props.item.role === 'assistant' && props.item.isThinking && !hasContent.value);
const preparingLabel = computed(() => String(props.activityText || '').trim() || '准备中...');
</script>

<template>
	<div class="message-main">
		<article class="item" :class="[item.kind, item.role, item.status]">
			<div class="header" :class="item.role === 'user' ? 'user-time-header' : 'assistant-time-header'">
				<span class="message-time">{{ item.timestamp }}</span>
			</div>

			<!-- 空态：无可渲染块时，助手思考中显示准备中指示器；否则若有附件只渲染附件。 -->
			<div v-if="isPreparing" class="message-flow">
				<div class="preparing-indicator" role="status">
					<span class="tool-status-icon spinning" aria-hidden="true"></span>
					<span class="shimmer-text">{{ preparingLabel }}</span>
				</div>
			</div>
			<div v-else-if="hasContent || attachments.length" class="message-flow">
				<div v-if="attachments.length" class="message-attachments">
					<figure v-for="(attachment, index) in attachments" :key="index" class="message-attachment">
						<img v-if="attachment.sourceUrl" class="message-attachment-image" :src="attachment.sourceUrl"
							:alt="attachment.fileName" loading="lazy"
							@click="emit('preview-image', { src: attachment.sourceUrl, alt: attachment.fileName })" />
						<div v-else class="message-attachment-image missing" aria-hidden="true"></div>
						<figcaption>
							<span class="message-attachment-name">{{ attachment.fileName }}</span>
							<span class="message-attachment-size">{{ attachment.size }}</span>
						</figcaption>
					</figure>
				</div>

				<MessageBlocks :item="item" :collapse="collapse" @preview-image="emit('preview-image', $event)" />
				<p v-if="item.errorMessage" class="message-error"
					:class="{ 'message-cancelled': item.status === 'cancelled' || item.status === 'truncated', 'message-blocked': item.status === 'blocked' }">{{
						item.errorMessage }}</p>
			</div>
		</article>
	</div>
</template>
