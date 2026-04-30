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
	profileModels: {
		type: Array,
		default: () => [],
	},
	selectedProfileModel: {
		type: String,
		default: '',
	},
	workspaceRoots: {
		type: Array,
		default: () => [],
	},
	selectedWorkspaceRootId: {
		type: String,
		default: '',
	},
});

const emit = defineEmits([
	'select-conversation-mode',
	'request-profile-models',
	'select-profile-model',
	'select-workspace',
]);
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
				<span class="mode-chip-icon" aria-hidden="true">
					<svg v-if="mode.id === 'programming'" viewBox="0 0 16 16" fill="none">
						<path d="M5 4.25 2 8l3 3.75"></path>
						<path d="M11 4.25 14 8l-3 3.75"></path>
						<path d="M9.25 3.25 6.75 12.75"></path>
					</svg>
					<svg v-else-if="mode.id === 'team'" viewBox="0 0 16 16" fill="none">
						<circle cx="5" cy="5.25" r="1.75"></circle>
						<circle cx="11" cy="5.25" r="1.75"></circle>
						<path d="M2.75 12.5c.35-1.65 1.55-2.5 3.25-2.5s2.9.85 3.25 2.5"></path>
						<path d="M8 12.5c.35-1.65 1.55-2.5 3.25-2.5 1 0 1.88.3 2.56.91"></path>
					</svg>
					<svg v-else-if="mode.id === 'channel'" viewBox="0 0 16 16" fill="none">
						<path
							d="M3.25 11.75 4 9.5h-.5A1.75 1.75 0 0 1 1.75 7.75v-2A1.75 1.75 0 0 1 3.5 4h9A1.75 1.75 0 0 1 14.25 5.75v2a1.75 1.75 0 0 1-1.75 1.75H7.25L3.25 11.75Z"
						></path>
						<path d="M5 6.75h5.5"></path>
						<path d="M5 8.75h3.25"></path>
					</svg>
					<svg v-else viewBox="0 0 16 16" fill="none">
						<circle cx="8" cy="8" r="2.25"></circle>
					</svg>
				</span>
				<span class="mode-chip-label">{{ mode.label }}</span>
			</button>
		</div>

		<div class="topbar-right">
			<div id="topbar-model-pill" class="context-pill">
				<span class="context-icon" aria-hidden="true">
					<svg viewBox="0 0 16 16" fill="none">
						<path d="M8 2.25 13 5v6L8 13.75 3 11V5l5-2.75Z"></path>
						<path d="M3 5 8 7.75 13 5"></path>
						<path d="M8 7.75V13.75"></path>
					</svg>
				</span>
				<select
					id="topbar-model-select"
					class="topbar-context-select"
					:class="{ 'topbar-context-select--placeholder': !selectedProfileModel }"
					:value="selectedProfileModel || ''"
					aria-label="Select model"
					@pointerdown="emit('request-profile-models')"
					@focus="emit('request-profile-models')"
					@change="emit('select-profile-model', $event.target.value)"
				>
					<option value="">Select model</option>
					<option v-for="option in profileModels" :key="option.id" :value="option.id">{{ option.label }}</option>
				</select>
			</div>

			<div id="topbar-workspace-pill" class="context-pill">
				<span class="context-icon" aria-hidden="true">
					<svg viewBox="0 0 16 16" fill="none">
						<path d="M1.75 4.5A1.75 1.75 0 0 1 3.5 2.75h3l1.25 1.5h4.75a1.75 1.75 0 0 1 1.75 1.75v5.5A1.75 1.75 0 0 1 12.5 13.25h-9A1.75 1.75 0 0 1 1.75 11.5v-7Z"></path>
					</svg>
				</span>
				<select
					id="topbar-workspace-select"
					class="topbar-context-select"
					:class="{ 'topbar-context-select--placeholder': !selectedWorkspaceRootId }"
					:value="selectedWorkspaceRootId || ''"
					aria-label="Select workspace"
					@change="emit('select-workspace', $event.target.value)"
				>
					<option value="">No workspace</option>
					<option v-for="option in workspaceRoots" :key="option.id" :value="option.id">{{ option.label }}</option>
				</select>
			</div>
		</div>
	</div>
</template>
