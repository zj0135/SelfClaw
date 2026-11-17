<script setup>
const props = defineProps({
	title: {
		type: String,
		default: '',
	},
	isMaximized: {
		type: Boolean,
		default: false,
	},
});
</script>

<template>
	<!-- 纯展示层，pointer-events 全关：鼠标事件穿透到下面的 .window-drag-region，
	     标题栏任意位置拖动 / 双击最大化都还是同一套逻辑，不需要第二份拖拽代码。
	     没有标题时整块不渲染（新对话与独立视图），顶栏不会出现空壳。 -->
	<div
		v-if="props.title"
		class="window-title"
		:class="{ 'is-maximized': props.isMaximized }"
		aria-hidden="true"
	>
		<span :key="props.title" class="window-title-text">{{ props.title }}</span>
	</div>
</template>

<style scoped>
/* 左对齐贴着侧栏边界，右边界让开标题栏按钮区（1 个面板按钮 + 3 个窗口按钮）。 */
.window-title {
	position: absolute;
	top: 0;
	bottom: 0;
	left: 20px;
	right: 200px;
	z-index: 115;
	display: flex;
	align-items: center;
	overflow: hidden;
	pointer-events: none;
	-webkit-user-select: none;
	user-select: none;
}

/* 最大化时系统把窗口向上顶出屏幕约 8px，可见高度变成 38px：跟随 WindowControls
   下移 4px，标题与按钮仍在同一条视觉中线上。 */
.window-title.is-maximized {
	transform: translateY(4px);
}

.window-title-text {
	min-width: 0;
	overflow: hidden;
	color: var(--text-soft);
	font-size: var(--fs-125);
	font-weight: 600;
	line-height: 1.2;
	letter-spacing: 0.01em;
	white-space: nowrap;
	text-overflow: ellipsis;
	animation: window-title-in 260ms cubic-bezier(0.22, 1, 0.36, 1) both;
}

@keyframes window-title-in {
	from {
		opacity: 0;
		transform: translateY(3px);
	}

	to {
		opacity: 1;
		transform: none;
	}
}

@media (prefers-reduced-motion: reduce) {
	.window-title-text {
		animation: none;
	}
}
</style>
