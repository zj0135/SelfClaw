<script setup>
defineProps({
	conversationModes: {
		type: Array,
		default: () => [],
	},
	selectedConversationModeId: {
		type: String,
		default: '',
	},
	currentModelLabel: {
		type: String,
		default: '',
	},
	currentWorkspaceLabel: {
		type: String,
		default: '',
	},
});

const emit = defineEmits(['select-conversation-mode', 'open-settings']);
</script>

<template>
	<div class="panel topbar">
		<div id="mode-chip-row" class="chip-row">
			<button
				v-for="mode in conversationModes"
				:key="mode.id"
				class="mode-chip"
				:class="{ active: mode.id === selectedConversationModeId }"
				type="button"
				@click="emit('select-conversation-mode', mode.id)"
			>
				{{ mode.label }}
			</button>
		</div>
		<div class="topbar-right">
			<div id="topbar-model-pill" class="context-pill" :title="currentModelLabel">
				<span class="context-label">模型</span>
				<span id="topbar-model-value" class="context-value">{{ currentModelLabel }}</span>
			</div>
			<div id="topbar-workspace-pill" class="context-pill" :title="currentWorkspaceLabel">
				<span class="context-label">工作区</span>
				<span id="topbar-workspace-value" class="context-value">{{ currentWorkspaceLabel }}</span>
			</div>
			<button class="icon-btn" type="button" aria-label="打开系统设置" @click="emit('open-settings')">设置</button>
		</div>
	</div>
</template>
