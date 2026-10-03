<script setup>
import { computed } from 'vue';
import { buildRenderBlocks, formatAttachmentSize } from '../../../renderers/transcript.js';
import MessageBlocks from './MessageBlocks.vue';
import TurnOutcome from './TurnOutcome.vue';

const props = defineProps({
	item: { type: Object, required: true },
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

const hasContent = computed(() => blocks.value.length > 0);
</script>

<template>
	<div class="message-main">
		<article class="item" :class="[item.kind, item.role, item.status]">
			<div class="header" :class="item.role === 'user' ? 'user-time-header' : 'assistant-time-header'">
				<span class="message-time">{{ item.timestamp }}</span>
			</div>

			<div v-if="hasContent || attachments.length" class="message-flow">
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
			</div>
			<TurnOutcome :outcome="item.turnOutcome" />
		</article>
	</div>
</template>
