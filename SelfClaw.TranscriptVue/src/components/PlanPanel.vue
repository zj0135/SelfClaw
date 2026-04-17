<script setup>
import { planPanelStatusLabel, planStepStatusLabel } from '../utils/plan';

defineProps({
	planPanel: {
		type: Object,
		default: null,
	},
	planSteps: {
		type: Array,
		default: () => [],
	},
	collapsed: {
		type: Boolean,
		default: false,
	},
	collapsedPlanText: {
		type: String,
		default: '',
	},
});

const emit = defineEmits(['toggle-collapse']);
</script>

<template>
	<div v-if="planPanel" class="plan-floating-shell" :class="{ collapsed }">
		<div class="plan-panel" :class="[planPanel.state, { collapsed }]">
			<div class="plan-panel-head">
				<div class="plan-panel-copy">
					<div class="plan-panel-title">{{ planPanel.title }}</div>
					<div class="plan-panel-status-text">
						{{ collapsed ? collapsedPlanText : planPanel.statusText }}
					</div>
				</div>
				<div class="plan-panel-head-actions">
					<div class="plan-panel-badge">{{ planPanelStatusLabel(planPanel.state) }}</div>
					<button
						class="plan-panel-toggle"
						type="button"
						:aria-label="collapsed ? '展开任务计划' : '折叠任务计划'"
						:title="collapsed ? '展开任务计划' : '折叠任务计划'"
						@click.stop="emit('toggle-collapse')"
					>
						<span class="plan-panel-toggle-chevron" :class="{ collapsed }">⌄</span>
					</button>
				</div>
			</div>
			<div v-if="collapsed" class="plan-panel-collapsed-row">
				<div class="plan-panel-collapsed-label">当前进度</div>
				<div class="plan-panel-collapsed-value">{{ collapsedPlanText }}</div>
			</div>
			<template v-else>
				<div v-if="planPanel.summary" class="plan-panel-summary">{{ planPanel.summary }}</div>
				<div v-if="planSteps.length > 0" class="plan-step-list">
					<div v-for="step in planSteps" :key="step.id" class="plan-step" :class="step.status">
						<div class="plan-step-leading" aria-hidden="true">
							<span v-if="step.status === 'running'" class="plan-step-spinner"></span>
							<span v-else class="plan-step-dot"></span>
						</div>
						<div class="plan-step-body">
							<div class="plan-step-title">{{ step.title }}</div>
						</div>
						<div class="plan-step-badge">{{ planStepStatusLabel(step.status) }}</div>
					</div>
				</div>
				<div v-else class="plan-panel-placeholder">
					<div class="plan-panel-placeholder-row">
						<span class="plan-step-spinner"></span>
						<span>正在梳理当前请求的执行步骤</span>
					</div>
				</div>
			</template>
		</div>
	</div>
</template>
