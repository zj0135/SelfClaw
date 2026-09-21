<script setup>
import { computed, ref } from 'vue';
import ActivityFloatingPanel from './ActivityFloatingPanel.vue';
import SubagentActivitySection from './SubagentActivitySection.vue';
import { useActivityPanel } from '../../composables/useActivityPanel.js';
import { useSubagentActivity } from '../../composables/useSubagentActivity.js';
import { useActivityPreferences } from '../../composables/useActivityPreferences.js';
import { useActivityStageConstraint } from '../../composables/useActivityStageConstraint.js';
import { useActivityDetail } from '../../composables/useActivityDetail.js';
const props = defineProps({ parentConversationId: { type: String, default: null } });
const emit = defineEmits(['preview-image']);
const parent = computed(() => props.parentConversationId);
const panel = useActivityPanel(parent);
const activity = useSubagentActivity(panel);
const preferences = useActivityPreferences(parent);
const stage = ref(null);
const { constrained } = useActivityStageConstraint(stage);
const expanded = computed(() => preferences.value.view.open && !constrained.value);
const detail = useActivityDetail(panel, preferences, expanded);
const sections = computed(() => panel.state.value.sections.filter((section) => section.kind === 'subagents'));
const visible = computed(() => panel.section.value?.counts.total > 0 || panel.error.value);
</script>

<template>
	<div ref="stage" class="activity-stage">
		<slot />
		<div v-if="visible" class="activity-dock" :class="{ collapsed: !expanded }">
			<ActivityFloatingPanel :sections="sections" :preferences="preferences.view" :force-collapsed="constrained" :loading="panel.loading.value" :error="panel.error.value" @refresh="panel.refresh()">
				<template #default="{ section }"><SubagentActivitySection v-if="section.kind === 'subagents'" :key="`${parent}:${section.id}`" :section="section" :activity="activity" :detail="detail" :collapse="preferences.collapse" @preview-image="emit('preview-image', $event)" /></template>
			</ActivityFloatingPanel>
		</div>
	</div>
</template>

<style scoped>
.activity-stage { min-width: 0; min-height: 0; position: relative; display: grid; container: activity-stage / inline-size; overflow: hidden; }
/* 浮动面板脱离文档流：展开/折叠都不参与舞台布局，也不影响主转录的滚动几何。 */
.activity-dock { position: absolute; z-index: 5; top: 20px; right: 20px; width: min(620px, calc(100% - 40px)); max-height: min(440px, calc(100% - 40px)); display: flex; justify-content: flex-end; pointer-events: none; }
.activity-dock.collapsed { width: auto; }
.activity-dock.collapsed :deep(.activity-panel) { width: auto; }
@container activity-stage (max-width: 480px) { .activity-dock { max-height: min(440px, 60%); } }
</style>
