<script setup>
defineProps({ draft: { type: Object, required: true } });
const levels = ['none', 'minimal', 'low', 'medium', 'high', 'xhigh', 'max'];
</script>

<template>
	<div class="fields">
		<div class="sampling full">
			<label class="sampling-label"><input v-model="draft.sampling.temperatureEnabled" type="checkbox" />温度 <span>Temperature</span></label>
			<div class="sampling-value">
				<input
					v-model.number="draft.sampling.temperature"
					type="range"
					min="0"
					max="2"
					step="0.01"
					aria-label="温度滑块"
					:disabled="!draft.sampling.temperatureEnabled"
				/>
				<input
					v-model.number="draft.sampling.temperature"
					type="number"
					min="0"
					max="2"
					step="0.01"
					required
					aria-label="温度"
					:disabled="!draft.sampling.temperatureEnabled"
				/>
			</div>
		</div>
		<div class="sampling full">
			<label class="sampling-label"><input v-model="draft.sampling.topPEnabled" type="checkbox" />核采样 <span>Top P</span></label>
			<div class="sampling-value">
				<input
					v-model.number="draft.sampling.topP"
					type="range"
					min="0"
					max="1"
					step="0.01"
					aria-label="Top P 滑块"
					:disabled="!draft.sampling.topPEnabled"
				/>
				<input
					v-model.number="draft.sampling.topP"
					type="number"
					min="0"
					max="1"
					step="0.01"
					required
					aria-label="Top P"
					:disabled="!draft.sampling.topPEnabled"
				/>
			</div>
		</div>
		<label
			><span>扩展思考等级</span
			><select v-model="draft.reasoningEffort">
				<option value="">提供商默认</option>
				<option v-for="level in levels" :key="level" :value="level">{{ level }}</option>
			</select></label
		>
		<label
			><span>多模态 · 图像输入</span
			><select v-model="draft.isMultimodal">
				<option :value="null">未标记</option>
				<option :value="true">支持</option>
				<option :value="false">不支持</option>
			</select></label
		>
		<label
			><span>上下文长度 <small>tokens</small></span
			><input v-model.number="draft.contextLength" type="number" min="1" max="2147483647" step="1" placeholder="未设置"
		/></label>
		<label
			><span>最大输出 <small>tokens</small></span
			><input v-model.number="draft.maxOutputTokens" type="number" min="1" max="2147483647" step="1" placeholder="未设置"
		/></label>
	</div>
</template>

<style scoped>
@import '../../styles/model-configuration-fields.css';
.sampling {
	display: grid;
	gap: 12px;
	padding-bottom: 18px;
	border-bottom: 1px solid var(--sc-line);
}
.sampling-label {
	display: flex;
	align-items: center;
	gap: 8px;
}
.sampling-label span {
	margin-left: auto;
	color: var(--sc-mute);
	font-size: 12px;
}
.sampling-value {
	display: grid;
	grid-template-columns: minmax(0, 1fr) 84px;
	gap: 20px;
	align-items: center;
}
input[type='checkbox'] {
	width: 16px;
	height: 16px;
	margin: 0;
	accent-color: var(--sc-acid);
}
input[type='range'] {
	width: 100%;
	height: 22px;
	padding: 0;
	accent-color: var(--sc-acid);
	border: 0;
}
</style>
