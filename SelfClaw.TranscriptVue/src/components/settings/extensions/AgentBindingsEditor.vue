<script setup>
defineProps({
	agents: { type: Array, required: true },
	assignedAgentIds: { type: Array, default: () => [] },
	disabled: { type: Boolean, default: false },
});
defineEmits(['change']);
</script>

<template>
	<div class="bindings">
		<div class="section-label">Agent 绑定</div>
		<label v-for="agent in agents" :key="agent.id">
			<input
				type="checkbox"
				:checked="assignedAgentIds.includes(agent.id)"
				:disabled="disabled"
				@change="$emit('change', agent.id, $event.target.checked)"
			/>
			<span>{{ agent.name }}</span>
			<code>{{ agent.id }}</code>
			<small v-if="agent.id === 'build'">内建</small>
		</label>
		<p v-if="!agents.length">暂无可绑定的 Agent。</p>
	</div>
</template>

<style scoped>
@import '../settings-console.css';
.bindings { display: grid; gap: 2px; }
.section-label { margin-bottom: 7px; color: var(--sc-faint); font-family: var(--sc-mono); font-size: 9px; font-weight: 700; text-transform: uppercase; }
label { display: grid; grid-template-columns: 18px minmax(0, 1fr) auto auto; align-items: center; min-height: 34px; gap: 7px; color: var(--sc-soft); font-size: 12px; }
input { accent-color: var(--sc-acid); }
code { color: var(--sc-faint); font-family: var(--sc-mono); font-size: 9.5px; }
small { padding: 2px 5px; border: 1px solid var(--sc-line-2); border-radius: 4px; color: var(--sc-faint); font-size: 9px; }
p { color: var(--sc-faint); font-size: 11px; }
</style>
