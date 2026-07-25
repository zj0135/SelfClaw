<script setup>
import { Check, X, Minus, LoaderCircle } from 'lucide-vue-next';

// 状态图标：成功绿勾 / 失败红叉 / 取消灰杠 / 进行中转圈。
// 外层 .tool-status-icon.<status> 保留原有 class，圆底与配色仍由全局 CSS 决定；
// 这里只把内部 SVG 换成 lucide 组件。
defineProps({
	status: {
		type: String,
		default: 'completed',
	},
});

const isSpinning = (status) => status === 'running' || status === 'awaitingapproval';
</script>

<template>
	<span v-if="isSpinning(status)" class="tool-status-icon spinning" aria-hidden="true">
		<LoaderCircle :size="11" :stroke-width="2" />
	</span>
	<span v-else-if="status === 'failed'" class="tool-status-icon failed" aria-hidden="true">
		<X :size="11" :stroke-width="1.9" />
	</span>
	<span v-else-if="status === 'cancelled'" class="tool-status-icon cancelled" aria-hidden="true">
		<Minus :size="11" :stroke-width="1.9" />
	</span>
	<span v-else class="tool-status-icon completed" aria-hidden="true">
		<Check :size="11" :stroke-width="1.9" />
	</span>
</template>
