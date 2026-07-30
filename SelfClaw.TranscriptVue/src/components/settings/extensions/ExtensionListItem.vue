<script setup>
import { ChevronRight, LoaderCircle } from 'lucide-vue-next';
import ExtensionStatusBadge from './ExtensionStatusBadge.vue';
defineProps({
	item: { type: Object, required: true },
	selected: { type: Boolean, default: false },
	pending: { type: Boolean, default: false },
});
defineEmits(['select', 'toggle']);
</script>

<template>
	<div class="row" :class="{ selected, pending }">
		<button type="button" class="main" @click="$emit('select')">
			<div class="identity"><strong>{{ item.name }}</strong><code>{{ item.id }}</code></div>
			<p>{{ item.description || (item.transport ? item.transport + ' transport' : '无描述') }}</p>
			<div class="meta">
				<ExtensionStatusBadge :status="item.status" />
				<span v-if="item.version">v{{ item.version }}</span>
				<span v-if="item.sourcePluginId">由 {{ item.sourcePluginId }} 管理</span>
				<span>绑定 {{ item.assignedAgentIds?.length || 0 }} 个 Agent</span>
			</div>
		</button>
		<label class="switch" :title="item.sourcePluginId ? `由 ${item.sourcePluginId} 管理` : item.enabled ? '停用' : '启用'">
			<input type="checkbox" :checked="item.enabled" :disabled="pending || Boolean(item.sourcePluginId)" @change="$emit('toggle', $event.target.checked)" />
			<span></span>
		</label>
		<LoaderCircle v-if="pending" :size="16" class="spinner" aria-label="正在更新" />
		<ChevronRight v-else :size="16" class="chevron" aria-hidden="true" />
	</div>
</template>

<style scoped>
@import '../settings-console.css';
.row { display: grid; grid-template-columns: minmax(0, 1fr) 38px 20px; align-items: center; min-height: 88px; border-top: 1px solid var(--sc-line); transition: background 140ms var(--sc-ease-out); }
.row:last-child { border-bottom: 1px solid var(--sc-line); }
.row:hover, .row.selected { background: var(--sc-raise); }
.row.selected { box-shadow: inset 2px 0 var(--sc-acid); }
.row.pending { opacity: 0.7; }
.main { min-width: 0; height: 100%; padding: 13px 16px; border: 0; background: transparent; color: inherit; text-align: left; }
.identity { display: flex; align-items: baseline; gap: 9px; min-width: 0; }
strong { overflow: hidden; color: var(--sc-text); font-size: 13.5px; text-overflow: ellipsis; white-space: nowrap; }
code { color: var(--sc-faint); font-family: var(--sc-mono); font-size: 10px; }
p { margin: 6px 0 8px; overflow: hidden; color: var(--sc-mute); font-size: 12px; text-overflow: ellipsis; white-space: nowrap; }
.meta { display: flex; align-items: center; gap: 14px; color: var(--sc-faint); font-size: 10.5px; }
.switch { position: relative; width: 34px; height: 20px; }
.switch input { position: absolute; opacity: 0; }
.switch span { position: absolute; inset: 0; border-radius: 10px; background: var(--sc-line-2); transition: background 140ms; }
.switch span::after { position: absolute; top: 3px; left: 3px; width: 14px; height: 14px; border-radius: 50%; background: var(--sc-panel); box-shadow: 0 1px 3px rgba(0,0,0,.18); content: ''; transition: transform 140ms; }
.switch input:checked + span { background: var(--sc-acid); }
.switch input:checked + span::after { transform: translateX(14px); }
.switch input:focus-visible + span { outline: 2px solid var(--sc-acid); outline-offset: 2px; }
.switch input:disabled + span { cursor: not-allowed; opacity: .45; }
.chevron { color: var(--sc-faint); }
.spinner { color: var(--sc-acid); animation: sc-spin .8s linear infinite; }
</style>
