<script setup>
import { computed } from 'vue';
import { ImageOff } from 'lucide-vue-next';
import { buildRenderBlocks } from '../../../renderers/transcript.js';
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
			sourceUrl: attachment.sourceUrl || attachment.dataUrl || '',
		}));
});

const hasContent = computed(() => blocks.value.length > 0);
</script>

<template>
	<div class="message-main">
		<article v-if="hasContent || item.role !== 'user'" class="item" :class="[item.kind, item.role, item.status]">
			<div class="header" :class="item.role === 'user' ? 'user-time-header' : 'assistant-time-header'">
				<span class="message-time">{{ item.timestamp }}</span>
			</div>

			<div v-if="hasContent" class="message-flow">
				<MessageBlocks :item="item" :collapse="collapse" @preview-image="emit('preview-image', $event)" />
			</div>
			<TurnOutcome :outcome="item.turnOutcome" />
		</article>

		<div v-else-if="item.role === 'user'" class="header user-time-header user-time-header-standalone">
			<span class="message-time">{{ item.timestamp }}</span>
		</div>

		<div v-if="attachments.length" class="message-attachments" role="list" aria-label="消息附带图片">
			<figure v-for="(attachment, index) in attachments" :key="index" class="message-attachment" role="listitem"
				@click="attachment.sourceUrl && emit('preview-image', { src: attachment.sourceUrl, alt: attachment.fileName })">
				<img v-if="attachment.sourceUrl" class="message-attachment-image" :src="attachment.sourceUrl"
					:alt="attachment.fileName" loading="lazy" />
				<div v-else class="message-attachment-image missing" aria-hidden="true">
					<ImageOff :size="16" :stroke-width="1.8" />
				</div>
			</figure>
		</div>
	</div>
</template>
