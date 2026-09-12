<script setup>
import { computed, watch } from 'vue';
import { Activity, ChevronDown, ChevronUp, RefreshCw } from 'lucide-vue-next';
import { activityErrorLabel } from '../../renderers/activityLabels.js';

const props = defineProps({ sections: { type: Array, default: () => [] }, preferences: { type: Object, required: true }, forceCollapsed: Boolean, loading: Boolean, error: String });
const emit = defineEmits(['refresh']);
const expanded = computed(() => props.preferences.open && !props.forceCollapsed);
const active = computed(() => props.sections.find((section) => section.id === props.preferences.section) || props.sections[0]);
const count = (section) => section.badge ?? section.counts?.total ?? '';
watch(() => props.sections, (sections) => {
	if (sections.length && !sections.some((section) => section.id === props.preferences.section)) props.preferences.section = sections[0].id;
});
</script>

<template>
	<section class="activity-panel" :class="{ collapsed: !expanded }" aria-label="活动面板">
		<header>
			<button class="activity-toggle" type="button" :aria-expanded="expanded" :disabled="forceCollapsed" @click="preferences.open = !preferences.open">
				<Activity :size="15" /><strong>活动</strong><span v-if="active" class="badge">{{ count(active) }}</span>
				<small v-if="active?.counts?.running">{{ active.counts.running }} 个运行中</small>
				<ChevronDown v-if="expanded" :size="14" /><ChevronUp v-else :size="14" />
			</button>
			<button class="icon-button" type="button" title="刷新活动" aria-label="刷新活动" @click="emit('refresh')"><RefreshCw :size="14" :class="{ spin: loading }" /></button>
		</header>
		<div v-if="expanded" class="activity-body">
			<div v-if="sections.length > 1" class="section-tabs" role="tablist" aria-label="活动分区">
				<button v-for="section in sections" :key="section.id" type="button" role="tab" :aria-selected="section.id === active?.id" @click="preferences.section = section.id">{{ section.title }} <span>{{ count(section) }}</span></button>
			</div>
			<p v-if="error" class="panel-error" role="alert">{{ activityErrorLabel(error) }}</p>
			<div v-if="loading && !active" class="panel-empty" role="status">加载中...</div>
			<slot v-else-if="active" :section="active" />
			<div v-else class="panel-empty">暂无活动</div>
		</div>
	</section>
</template>

<style scoped>
.activity-panel { pointer-events: auto; display: flex; flex-direction: column; min-height: 0; max-height: 100%; width: 100%; color: var(--text); border: 1px solid var(--border-strong); border-radius: 8px; background: var(--panel); box-shadow: 0 6px 22px rgb(0 0 0 / 12%); overflow: hidden; }
header { display: flex; flex: 0 0 38px; padding: 0 9px; gap: 8px; align-items: center; border-bottom: 1px solid var(--border); }
.collapsed header { border-bottom: 0; }
.activity-toggle { display: flex; align-items: center; gap: 8px; flex: 1; min-width: 0; color: inherit; border: 0; background: transparent; padding: 0; font-size: var(--fs-12); text-align: left; }
.activity-toggle > svg:last-child { margin-left: auto; flex-shrink: 0; }
.activity-toggle small { color: var(--muted); font-size: var(--fs-11); }
.badge { font: 600 var(--fs-11) var(--font-mono); color: var(--accent); }
.icon-button { display: grid; place-items: center; width: 26px; height: 26px; border: 0; border-radius: 4px; color: var(--muted); background: transparent; }
.icon-button:hover { background: var(--panel-muted); }
.activity-body { display: flex; flex-direction: column; min-height: 0; overflow: hidden; }
.section-tabs { display: flex; border-bottom: 1px solid var(--border); gap: 4px; padding: 4px 8px; }
.section-tabs button { border: 0; padding: 5px 8px; background: transparent; color: var(--muted); font-size: var(--fs-12); }
.section-tabs button[aria-selected='true'] { color: var(--accent); border-bottom: 2px solid var(--accent); }
.panel-error { margin: 0; padding: 9px 12px; color: var(--danger); font-size: var(--fs-12); overflow-wrap: anywhere; }
.panel-empty { padding: 18px 12px; color: var(--muted); font-size: var(--fs-12); }
.spin { animation: activity-spin 1s linear infinite; } @keyframes activity-spin { to { transform: rotate(360deg); } }
@media (prefers-reduced-motion: reduce) { .spin { animation: none; } }
</style>
