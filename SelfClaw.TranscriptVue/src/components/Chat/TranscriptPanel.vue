<script setup>
import { ref } from 'vue';
import MessageContent from './transcript/MessageContent.vue';

defineProps({
	items: {
		type: Array,
		default: () => [],
	},
	// 折叠状态载体，透传给每个 MessageContent。
	collapse: {
		type: Object,
		required: true,
	},
	// 回合执行状态：{ label, elapsedText } 或 null。
	turnStatus: {
		type: Object,
		default: null,
	},
	// 「准备中」指示器文案，透传给正在思考的助手消息。
	activityText: {
		type: String,
		default: '',
	},
});

const emit = defineEmits(['scroll', 'preview-image']);
const scrollEl = ref(null);

defineExpose({
	getScrollEl: () => scrollEl.value,
});
</script>

<template>
	<section class="panel transcript-panel">
		<div id="transcript-scroll" ref="scrollEl" class="transcript-scroll" @scroll="emit('scroll', $event)">
			<!-- 每条消息是独立的 keyed 节点：流式更新时只有内容变化的那条会重渲，
			     其余消息的 DOM（文本选区、图片、动画）保持不动。展开折叠块只重渲该块。 -->
			<div
				v-for="item in items"
				:key="item.id"
				class="message-row"
				:class="[item.role, item.status]"
				:data-message-id="item.id"
			>
				<MessageContent
					:item="item"
					:activity-text="activityText"
					:collapse="collapse"
					@preview-image="emit('preview-image', $event)"
				/>
			</div>
			<div v-if="turnStatus" class="turn-status-row" role="status" aria-live="polite">
				<span class="turn-status-dot" aria-hidden="true"></span>
				<span class="turn-status-label">{{ turnStatus.label }}</span>
				<span v-if="turnStatus.elapsedText" class="turn-status-time">{{ turnStatus.elapsedText }}</span>
			</div>
		</div>
	</section>
</template>
