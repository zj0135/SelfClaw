<script setup>
import { ref } from 'vue';
import PlanPanel from './PlanPanel.vue';

defineProps({
	showPlanPanel: {
		type: Boolean,
		default: false,
	},
	planPanel: {
		type: Object,
		default: null,
	},
	planSteps: {
		type: Array,
		default: () => [],
	},
	planPanelCollapsed: {
		type: Boolean,
		default: false,
	},
	collapsedPlanText: {
		type: String,
		default: '',
	},
	composerValue: {
		type: String,
		default: '',
	},
	composerPlaceholder: {
		type: String,
		default: '',
	},
	isChannelMode: {
		type: Boolean,
		default: false,
	},
	mentionState: {
		type: Object,
		required: true,
	},
	mentionCandidates: {
		type: Array,
		default: () => [],
	},
	profiles: {
		type: Array,
		default: () => [],
	},
	selectedProfileId: {
		type: String,
		default: '',
	},
	isTeamMode: {
		type: Boolean,
		default: false,
	},
	teamRoundModes: {
		type: Array,
		default: () => [],
	},
	selectedTeamRoundModeId: {
		type: String,
		default: '',
	},
	teamOutputModes: {
		type: Array,
		default: () => [],
	},
	selectedTeamOutputModeId: {
		type: String,
		default: '',
	},
	toolPermissionModes: {
		type: Array,
		default: () => [],
	},
	selectedToolPermissionModeId: {
		type: String,
		default: '',
	},
	showPlanningToggle: {
		type: Boolean,
		default: false,
	},
	isBusy: {
		type: Boolean,
		default: false,
	},
	isPlanningModeEnabled: {
		type: Boolean,
		default: false,
	},
	sendButtonDisabled: {
		type: Boolean,
		default: false,
	},
});

const emit = defineEmits([
	'composer-input',
	'composer-keydown',
	'apply-mention',
	'select-profile',
	'select-team-round',
	'select-team-output',
	'select-permission',
	'toggle-planning-mode',
	'toggle-plan-panel-collapse',
	'send-click',
]);

const composerEl = ref(null);

defineExpose({
	getComposerEl: () => composerEl.value,
});
</script>

<template>
	<section class="panel composer-panel">
		<PlanPanel
			v-if="showPlanPanel && planPanel"
			:plan-panel="planPanel"
			:plan-steps="planSteps"
			:collapsed="planPanelCollapsed"
			:collapsed-plan-text="collapsedPlanText"
			@toggle-collapse="emit('toggle-plan-panel-collapse')"
		/>

		<div class="composer-grid">
			<div class="composer-surface">
				<div class="composer-stack">
					<textarea
						id="composer"
						ref="composerEl"
						class="composer-box"
						:value="composerValue"
						:disabled="isChannelMode"
						:placeholder="composerPlaceholder"
						@input="emit('composer-input', $event)"
						@keydown="emit('composer-keydown', $event)"
					></textarea>
					<div id="mention-picker" class="mention-picker" :class="{ open: mentionState.open && mentionCandidates.length > 0 }">
						<button
							v-for="(item, index) in mentionCandidates"
							:key="item.id"
							class="mention-option"
							:class="{ active: index === mentionState.activeIndex }"
							type="button"
							@click.stop="emit('apply-mention', item)"
						>
							<span class="mention-option-name">@{{ item.name }}</span>
							<span class="mention-option-role">{{ item.role }}</span>
						</button>
					</div>
				</div>
				<div class="composer-footer">
					<div class="composer-controls">
						<select
							id="composer-profile-select"
							class="composer-inline-select"
							aria-label="当前模型配置"
							:value="selectedProfileId || ''"
							@change="emit('select-profile', $event.target.value)"
						>
							<option value="">选择模型</option>
							<option v-for="option in profiles" :key="option.id" :value="option.id">{{ option.label }}</option>
						</select>
						<template v-if="isTeamMode">
							<select
								id="composer-team-round-select"
								class="composer-inline-select"
								aria-label="团队最大讨论轮次"
								:value="selectedTeamRoundModeId"
								@change="emit('select-team-round', $event.target.value)"
							>
								<option v-for="option in teamRoundModes" :key="option.id" :value="option.id">{{ option.label }}</option>
							</select>
							<select
								id="composer-team-output-select"
								class="composer-inline-select"
								aria-label="团队总结输出方式"
								:value="selectedTeamOutputModeId"
								@change="emit('select-team-output', $event.target.value)"
							>
								<option v-for="option in teamOutputModes" :key="option.id" :value="option.id">{{ option.label }}</option>
							</select>
						</template>
						<template v-else>
							<select
								id="composer-permission-select"
								class="composer-inline-select"
								aria-label="工具权限模式"
								:value="selectedToolPermissionModeId"
								@change="emit('select-permission', $event.target.value)"
							>
								<option v-for="option in toolPermissionModes" :key="option.id" :value="option.id">{{ option.label }}</option>
							</select>
							<label v-if="showPlanningToggle" class="plan-mode-toggle" :class="{ disabled: isBusy, active: isPlanningModeEnabled }">
								<span class="plan-mode-icon" aria-hidden="true">
									<svg viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round">
										<path d="M2.5 3.5h6"></path>
										<path d="M2.5 8h4.5"></path>
										<path d="M2.5 12.5h5.5"></path>
										<path d="m11 3.5 2.5 2.5-4.5 4.5H6.5V8.5L11 3.5Z"></path>
									</svg>
								</span>
								<span class="plan-mode-label">计划模式</span>
								<input
									class="toggle-input"
									type="checkbox"
									:checked="isPlanningModeEnabled"
									:disabled="isBusy"
									@change="emit('toggle-planning-mode', $event.target.checked)"
								/>
								<span class="toggle-switch"></span>
							</label>
						</template>
					</div>
					<button
						id="send-button"
						class="send-btn"
						:class="{ loading: isBusy, idle: !isBusy }"
						type="button"
						:disabled="sendButtonDisabled"
						:aria-label="isBusy ? '停止生成' : '发送消息'"
						:title="isBusy ? '停止生成' : '发送消息'"
						@click="emit('send-click')"
					>
						<span v-if="isBusy" class="send-btn-spinner" aria-hidden="true">
							<span class="send-btn-spinner-ring"></span>
							<span class="send-btn-spinner-core"></span>
						</span>
						<span v-else class="send-btn-arrow" aria-hidden="true">
							<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round">
								<path d="M12 19V7"></path>
								<path d="m6 11 6-6 6 6"></path>
							</svg>
						</span>
					</button>
				</div>
			</div>
		</div>
	</section>
</template>
