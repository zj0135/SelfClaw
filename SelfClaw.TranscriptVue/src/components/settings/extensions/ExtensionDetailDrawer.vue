<script setup>
import { Pencil, Trash2, X } from 'lucide-vue-next';
import AgentBindingsEditor from './AgentBindingsEditor.vue';
import ExtensionStatusBadge from './ExtensionStatusBadge.vue';

defineProps({
	item: { type: Object, required: true },
	kind: { type: String, required: true },
	agents: { type: Array, required: true },
	pending: { type: Boolean, default: false },
});
defineEmits(['close', 'delete', 'edit', 'binding']);
</script>

<template>
	<aside class="drawer">
		<header>
			<div>
				<ExtensionStatusBadge :status="item.status" />
				<h2>{{ item.name }}</h2>
				<code>{{ item.id }}</code>
			</div>
			<button type="button" class="icon" title="关闭详情" @click="$emit('close')"><X :size="17" /></button>
		</header>

		<p class="description">{{ item.description || '无描述' }}</p>
		<dl>
			<template v-if="item.version"><dt>版本</dt><dd>{{ item.version }}</dd></template>
			<template v-if="item.transport"><dt>传输</dt><dd>{{ item.transport === 1 ? 'HTTP' : item.transport === 0 ? 'STDIO' : item.transport }}</dd></template>
			<template v-if="item.sourcePluginId"><dt>来源插件</dt><dd>{{ item.sourcePluginId }}</dd></template>
			<template v-if="item.lastCheckedAtUtc"><dt>最近检查</dt><dd>{{ new Date(item.lastCheckedAtUtc).toLocaleString() }}</dd></template>
			<template v-if="item.lastError"><dt>最近错误</dt><dd class="error">{{ item.lastError }}</dd></template>
			<template v-if="item.tools?.length"><dt>工具</dt><dd>{{ item.tools.join(', ') }}</dd></template>
			<template v-if="item.permissions?.length"><dt>权限</dt><dd>{{ item.permissions.join(', ') }}</dd></template>
		</dl>

		<AgentBindingsEditor
			v-if="!item.sourcePluginId"
			:agents="agents"
			:assigned-agent-ids="item.assignedAgentIds"
			:disabled="pending"
			@change="(agentId, enabled) => $emit('binding', agentId, enabled)"
		/>
		<p v-else class="managed">绑定继承自插件 {{ item.sourcePluginId }}，请在插件页调整。</p>

		<footer v-if="kind === 'mcpServer' || !item.sourcePluginId">
			<button v-if="kind === 'mcpServer'" type="button" class="secondary" :disabled="pending" @click="$emit('edit')">
				<Pencil :size="14" />编辑
			</button>
			<button v-if="!item.sourcePluginId" type="button" class="danger" :disabled="pending" @click="$emit('delete')">
				<Trash2 :size="14" />删除
			</button>
		</footer>
	</aside>
</template>

<style scoped>
@import '../settings-console.css';
.drawer { display: flex; flex-direction: column; min-width: 0; height: 100%; padding: 18px; border-left: 1px solid var(--sc-line); background: var(--sc-bg); animation: sc-fade 160ms ease-out both; }
header { display: flex; align-items: flex-start; justify-content: space-between; gap: 12px; }
h2 { margin: 7px 0 2px; overflow-wrap: anywhere; color: var(--sc-text); font-size: 18px; letter-spacing: 0; }
code { color: var(--sc-faint); font-family: var(--sc-mono); font-size: 10px; }
.icon { width: 30px; height: 30px; padding: 0; border: 0; background: transparent; color: var(--sc-mute); }
.description { margin: 18px 0; color: var(--sc-mute); font-size: 12px; line-height: 1.65; }
dl { display: grid; grid-template-columns: 76px minmax(0, 1fr); margin: 0 0 22px; border-top: 1px solid var(--sc-line); }
dt, dd { margin: 0; padding: 9px 0; border-bottom: 1px solid var(--sc-line); font-size: 11px; }
dt { color: var(--sc-faint); }
dd { overflow-wrap: anywhere; color: var(--sc-soft); }
dd.error { color: var(--sc-err); }
.managed { margin: 0; padding: 10px 0; border-top: 1px solid var(--sc-line); border-bottom: 1px solid var(--sc-line); color: var(--sc-faint); font-size: 11px; line-height: 1.55; }
footer { display: flex; gap: 8px; margin-top: auto; padding-top: 20px; }
footer button { display: inline-flex; align-items: center; justify-content: center; flex: 1; height: 34px; gap: 6px; border: 1px solid var(--sc-line-2); border-radius: 6px; background: transparent; color: var(--sc-soft); }
footer .danger { color: var(--sc-err); }
button:disabled { cursor: wait; opacity: .5; }
</style>
