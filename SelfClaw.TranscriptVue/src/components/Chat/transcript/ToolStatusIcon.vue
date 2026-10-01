<script setup>
import { Check, X, Minus, LoaderCircle, ShieldX } from 'lucide-vue-next';
import { toolStatusLabel } from '../../../renderers/shared.js';

// 状态图标：成功绿勾 / 失败红叉 / 已拦截盾牌 / 取消灰杠 / 进行中转圈。
// 均为无底色的裸字形：外层 .tool-status-icon.<status> 只决定配色与旋转，图形由 lucide 组件提供。
// 状态文字不再单独占位，改作图标自身的可访问名称。
defineProps({
	status: {
		type: String,
		default: 'completed',
	},
});

const isSpinning = (status) => status === 'running' || status === 'awaitingapproval';
</script>

<template>
	<span v-if="isSpinning(status)" class="tool-status-icon spinning" role="img" :aria-label="toolStatusLabel(status)">
		<LoaderCircle :size="13" :stroke-width="2" />
	</span>
	<span v-else-if="status === 'failed'" class="tool-status-icon failed" role="img" :aria-label="toolStatusLabel(status)">
		<X :size="13" :stroke-width="1.9" />
	</span>
	<span v-else-if="status === 'blocked'" class="tool-status-icon blocked" role="img" :aria-label="toolStatusLabel(status)">
		<ShieldX :size="13" :stroke-width="1.9" />
	</span>
	<span v-else-if="status === 'cancelled'" class="tool-status-icon cancelled" role="img" :aria-label="toolStatusLabel(status)">
		<Minus :size="13" :stroke-width="1.9" />
	</span>
	<span v-else class="tool-status-icon completed" role="img" :aria-label="toolStatusLabel(status)">
		<Check :size="13" :stroke-width="1.9" />
	</span>
</template>
