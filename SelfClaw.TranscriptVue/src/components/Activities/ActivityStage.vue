<script setup>
import { computed, ref } from 'vue';
import ActivityFloatingPanel from './ActivityFloatingPanel.vue';
import SubagentActivitySection from './SubagentActivitySection.vue';
import { useActivityPanel } from '../../composables/useActivityPanel.js';
import { useSubagentActivity } from '../../composables/useSubagentActivity.js';
import { useActivityPreferences } from '../../composables/useActivityPreferences.js';
import { useActivityPanelSize } from '../../composables/useActivityPanelSize.js';
import { useActivityDetail } from '../../composables/useActivityDetail.js';
const props = defineProps({ parentConversationId: { type: String, default: null } });
const emit = defineEmits(['preview-image']);
const parent = computed(() => props.parentConversationId);
const panel = useActivityPanel(parent);
const activity = useSubagentActivity(panel);
const preferences = useActivityPreferences(parent);
const dock = ref(null);
const stage = ref(null);
const { height: dockHeight, constrained } = useActivityPanelSize(dock, stage);
const detail = useActivityDetail(panel, preferences, computed(() => preferences.value.view.open && !constrained.value));
const sections = computed(() => panel.state.value.sections.filter((section) => section.kind === 'subagents'));
const visible = computed(() => panel.section.value?.counts.total > 0 || panel.error.value);
</script>

<template>
	<div ref="stage" class="activity-stage" :style="{ '--activity-dock-height': `${visible ? dockHeight : 0}px` }">
		<slot />
		<div ref="dock" class="activity-dock" :class="{ visible }">
			<ActivityFloatingPanel v-if="visible" :sections="sections" :preferences="preferences.view" :force-collapsed="constrained" :loading="panel.loading.value" :error="panel.error.value" @refresh="panel.refresh()">
				<template #default="{ section }"><SubagentActivitySection v-if="section.kind === 'subagents'" :key="`${parent}:${section.id}`" :section="section" :activity="activity" :detail="detail" :collapse="preferences.collapse" @preview-image="emit('preview-image', $event)" /></template>
			</ActivityFloatingPanel>
		</div>
	</div>
</template>

<style scoped>
.activity-stage { min-width: 0; min-height: 0; position: relative; display: grid; container: activity-stage / inline-size; overflow: hidden; }
.activity-dock { position: absolute; z-index: 4; right: 12px; bottom: 8px; width: min(620px, calc(100% - 24px)); max-height: min(440px, calc(100% - 16px)); display: flex; pointer-events: none; }
.activity-dock:not(.visible) { height: 0; }
.activity-stage :deep(.transcript-content) { padding-bottom: calc(var(--activity-dock-height) + 28px); }
@container activity-stage (max-width: 480px) { .activity-dock { max-height: min(440px, 60%); } }
</style>
