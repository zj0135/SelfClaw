<script setup>
defineProps({ draft: { type: Object, required: true } });
const prices = [
	['priceInPerMTok', '输入 token'],
	['priceOutPerMTok', '输出 token'],
	['priceCacheReadPerMTok', '缓存读取'],
	['priceCacheWritePerMTok', '缓存写入'],
];
</script>

<template>
	<div class="fields">
		<label class="full"><span>显示名称</span><input v-model.trim="draft.name" required maxlength="256" /></label>
		<div class="pricing-heading full"><span>计费</span><small>USD / 1M tokens</small></div>
		<label v-for="[key, label] in prices" :key="key"
			><span>{{ label }}</span>
			<div class="currency">
				<span>$</span><input v-model.number="draft[key]" type="number" min="0" step="any" placeholder="未设置" :aria-label="label" /></div
		></label>
		<label class="full"><span>备注</span><textarea v-model="draft.description" rows="3" maxlength="4000" /></label>
	</div>
</template>

<style scoped>
@import '../../styles/model-configuration-fields.css';
.pricing-heading {
	display: flex;
	justify-content: space-between;
	align-items: center;
	padding-top: 6px;
}
.currency {
	position: relative;
}
.currency > span {
	position: absolute;
	left: 12px;
	top: 11px;
	color: var(--sc-mute);
}
.currency input {
	padding-left: 28px;
}
</style>
