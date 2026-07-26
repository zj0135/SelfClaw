<script setup>
import { KeyRound, Plus, Trash2 } from 'lucide-vue-next';
defineProps({
	title: { type: String, required: true },
	rows: { type: Array, required: true },
	error: { type: String, default: '' },
	managed: { type: Boolean, default: false },
});
defineEmits(['add', 'remove']);
</script>

<template>
	<section>
		<div class="section-head">
			<div><KeyRound :size="14" /><strong>{{ title }}</strong></div>
			<button v-if="!managed" type="button" title="添加字段" @click="$emit('add')"><Plus :size="14" /></button>
		</div>
		<div v-for="(row, index) in rows" :key="index" class="entry">
			<input v-model="row.key" class="key" placeholder="键名" :disabled="managed" />
			<input
				v-model="row.value"
				:type="row.isSecret ? 'password' : 'text'"
				:placeholder="row.isSecret && row.hasSecret ? '已配置，留空保留' : '值'"
				:disabled="row.clearSecret"
			/>
			<label title="作为密钥保存"><input v-model="row.isSecret" type="checkbox" :disabled="managed" />密钥</label>
			<label v-if="row.isSecret && row.hasSecret" title="保存时删除已配置的密钥">
				<input v-model="row.clearSecret" type="checkbox" />清除
			</label>
			<button v-if="!managed" type="button" class="remove" title="删除字段" @click="$emit('remove', index)"><Trash2 :size="14" /></button>
		</div>
		<p v-if="error" class="error">{{ error }}</p>
	</section>
</template>

<style scoped>
@import '../settings-console.css';
section { display: grid; gap: 8px; }
.section-head { display: flex; align-items: center; justify-content: space-between; }
.section-head div { display: flex; align-items: center; gap: 7px; color: var(--sc-soft); }
strong { font-size: 11.5px; }
button { display: inline-grid; place-items: center; width: 28px; height: 28px; padding: 0; border: 1px solid var(--sc-line-2); border-radius: 5px; background: transparent; color: var(--sc-mute); }
.entry { display: grid; grid-template-columns: minmax(100px, .8fr) minmax(130px, 1fr) auto auto 28px; align-items: center; gap: 6px; }
.entry > input { min-width: 0; height: 32px; padding: 0 8px; border: 1px solid var(--sc-line-2); border-radius: 5px; background: var(--sc-panel); color: var(--sc-text); }
label { display: flex; align-items: center; gap: 4px; color: var(--sc-mute); font-size: 10px; white-space: nowrap; }
label input { accent-color: var(--sc-acid); }
.remove { border-color: transparent; color: var(--sc-faint); }
.error { margin: 0; color: var(--sc-err); font-size: 10.5px; }
@media (max-width: 760px) { .entry { grid-template-columns: 1fr 1fr; } }
</style>
