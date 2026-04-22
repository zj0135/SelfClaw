<script setup>
import { ref } from 'vue';
import TranscriptGraphView from './TranscriptGraphView.vue';

defineProps({
	messagesHtml: {
		type: String,
		default: '',
	},
	showPlanPanel: {
		type: Boolean,
		default: false,
	},
	planPanelCollapsed: {
		type: Boolean,
		default: false,
	},
	visualizationEnabled: {
		type: Boolean,
		default: false,
	},
	items: {
		type: Array,
		default: () => [],
	},
	conversations: {
		type: Array,
		default: () => [],
	},
	selectedConversationId: {
		type: String,
		default: null,
	},
	selectedConversationModeId: {
		type: String,
		default: 'programming',
	},
	selectedProfileModel: {
		type: String,
		default: '',
	},
	teamMembers: {
		type: Array,
		default: () => [],
	},
	agentActivities: {
		type: Array,
		default: () => [],
	},
});

const emit = defineEmits(['scroll']);
const scrollEl = ref(null);

defineExpose({
	getScrollEl: () => scrollEl.value,
});
</script>

<template>
	<section class="panel transcript-panel">
		<div
			id="transcript-scroll"
		ref="scrollEl"
		class="transcript-scroll"
		:class="{
			'graph-mode': visualizationEnabled,
			'with-floating-plan': showPlanPanel,
			'with-floating-plan-collapsed': showPlanPanel && planPanelCollapsed,
		}"
		@scroll="emit('scroll', $event)"
	>
			<TranscriptGraphView
				v-if="visualizationEnabled"
				:items="items"
				:conversations="conversations"
				:selected-conversation-id="selectedConversationId"
				:selected-conversation-mode-id="selectedConversationModeId"
				:selected-profile-model="selectedProfileModel"
				:team-members="teamMembers"
				:agent-activities="agentActivities"
			/>
			<div v-else v-html="messagesHtml"></div>
		</div>
	</section>
</template>
