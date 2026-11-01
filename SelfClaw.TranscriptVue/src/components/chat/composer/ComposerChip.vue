<script setup>
import { computed } from 'vue';
import { BookOpenCheck, File, Folder, SquareSlash } from 'lucide-vue-next';

// 输入框 chip 的可视内容（图标 + 标签）。宿主 span 由 ComposerEditor 创建并挂上
// contenteditable=false 与 data-chip-*，这里只负责渲染，不参与序列化。
const props = defineProps({
	kind: { type: String, required: true },
	value: { type: String, default: '' },
	label: { type: String, default: '' },
});

const icon = computed(() => {
	if (props.kind === 'skill') {
		return BookOpenCheck;
	}

	if (props.kind === 'command') {
		return SquareSlash;
	}

	return props.value.endsWith('/') ? Folder : File;
});
</script>

<template>
	<span class="chip-body">
		<span class="inline-chip-icon" aria-hidden="true">
			<component :is="icon" :size="13" :stroke-width="1.9" />
		</span>
		<span class="inline-chip-label">{{ props.label }}</span>
	</span>
</template>

<style scoped>
.chip-body {
	display: inline-flex;
	align-items: center;
	gap: 5px;
	min-width: 0;
	animation: chip-pop 0.18s cubic-bezier(0.34, 1.56, 0.64, 1);
}

@keyframes chip-pop {
	from {
		opacity: 0;
		transform: scale(0.9);
	}

	to {
		opacity: 1;
		transform: none;
	}
}

@media (prefers-reduced-motion: reduce) {
	.chip-body {
		animation: none;
	}
}
</style>
