<script setup>
import { onMounted, onUnmounted, ref } from 'vue';
import PlanPanel from './PlanPanel.vue';

const props = defineProps({
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
	showVisualizationToggle: {
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
	visualizationEnabled: {
		type: Boolean,
		default: false,
	},
	sendButtonDisabled: {
		type: Boolean,
		default: false,
	},
	attachments: {
		type: Array,
		default: () => [],
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
	'toggle-visualization-mode',
	'toggle-plan-panel-collapse',
	'pick-images',
	'capture-screenshot',
	'remove-attachment',
	'send-click',
]);

const composerEl = ref(null);
const toolsShellEl = ref(null);
const toolsMenuOpen = ref(false);

function toggleToolsMenu() {
	if (props.isChannelMode) {
		return;
	}

	toolsMenuOpen.value = !toolsMenuOpen.value;
}

function closeToolsMenu() {
	toolsMenuOpen.value = false;
}

function onDocumentPointerDown(event) {
	if (!toolsMenuOpen.value || toolsShellEl.value?.contains(event.target)) {
		return;
	}

	closeToolsMenu();
}

function requestImagePicker() {
	emit('pick-images');
	closeToolsMenu();
}

function requestScreenshotCapture() {
	closeToolsMenu();
	window.setTimeout(() => emit('capture-screenshot'), 0);
}

function formatAttachmentSize(byteLength) {
	const size = Number(byteLength || 0);
	if (size >= 1024 * 1024) {
		return `${(size / (1024 * 1024)).toFixed(size >= 10 * 1024 * 1024 ? 0 : 1)} MB`;
	}

	if (size >= 1024) {
		return `${Math.max(1, Math.round(size / 1024))} KB`;
	}

	return `${Math.max(0, size)} B`;
}

onMounted(() => {
	document.addEventListener('pointerdown', onDocumentPointerDown);
});

onUnmounted(() => {
	document.removeEventListener('pointerdown', onDocumentPointerDown);
});

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
					<div v-if="attachments.length > 0" class="composer-attachments" aria-label="待发送图片">
						<div v-for="attachment in attachments" :key="attachment.id" class="composer-attachment">
							<img v-if="attachment.dataUrl" class="composer-attachment-preview" :src="attachment.dataUrl" :alt="attachment.fileName" />
							<div v-else class="composer-attachment-preview empty" aria-hidden="true">
								<svg viewBox="0 0 20 20" fill="none" stroke="currentColor" stroke-width="1.5">
									<path d="M4.5 15.5h11v-11h-11v11Z"></path>
									<path d="m6.5 13 3-3 2 2 1-1 2 2"></path>
									<circle cx="7.5" cy="7.5" r="1"></circle>
								</svg>
							</div>
							<div class="composer-attachment-meta">
								<span class="composer-attachment-name">{{ attachment.fileName }}</span>
								<span class="composer-attachment-size">{{ formatAttachmentSize(attachment.byteLength) }}</span>
							</div>
							<button
								class="composer-attachment-remove"
								type="button"
								:aria-label="`移除 ${attachment.fileName}`"
								:title="`移除 ${attachment.fileName}`"
								@click="emit('remove-attachment', attachment.id)"
							>
								<svg viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round">
									<path d="M4.5 4.5 11.5 11.5"></path>
									<path d="M11.5 4.5 4.5 11.5"></path>
								</svg>
							</button>
						</div>
					</div>
				</div>
				<div class="composer-footer">
					<div class="composer-controls">
						<div ref="toolsShellEl" class="composer-tools-shell">
							<button
								class="composer-tools-trigger"
								:class="{ active: toolsMenuOpen }"
								type="button"
								:disabled="isChannelMode"
								:aria-expanded="toolsMenuOpen ? 'true' : 'false'"
								aria-haspopup="menu"
								aria-label="展开输入工具"
								title="展开输入工具"
								@click.stop="toggleToolsMenu"
							>
								<svg viewBox="0 0 20 20" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round">
									<path d="M10 4v12"></path>
									<path d="M4 10h12"></path>
								</svg>
							</button>
							<div v-if="toolsMenuOpen" class="composer-tools-menu" role="menu" @click.stop>
								<button class="composer-tools-menu-row" type="button" role="menuitem" @click="requestImagePicker">
									<span class="composer-tools-menu-icon" aria-hidden="true">
										<svg viewBox="0 0 18 18" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round">
											<path d="M6.25 9.25 9 6.5a3 3 0 1 1 4.25 4.25l-4 4a4.25 4.25 0 0 1-6-6l5.25-5.25"></path>
										</svg>
									</span>
									<span class="composer-tools-menu-label">添加图片</span>
								</button>
								<button class="composer-tools-menu-row" type="button" role="menuitem" @click="requestScreenshotCapture">
									<span class="composer-tools-menu-icon" aria-hidden="true">
										<svg viewBox="0 0 18 18" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round">
											<path d="M5.75 5.25 7 3.5h4l1.25 1.75H15a1.5 1.5 0 0 1 1.5 1.5v6.5a1.5 1.5 0 0 1-1.5 1.5H3a1.5 1.5 0 0 1-1.5-1.5v-6.5A1.5 1.5 0 0 1 3 5.25h2.75Z"></path>
											<circle cx="9" cy="10" r="3"></circle>
											<path d="M13.75 7.25h.01"></path>
										</svg>
									</span>
									<span class="composer-tools-menu-label">添加截图</span>
								</button>
								<label
									v-if="showPlanningToggle"
									class="composer-tools-menu-row composer-tools-menu-toggle"
									:class="{ disabled: isBusy }"
									role="menuitemcheckbox"
									:aria-checked="isPlanningModeEnabled ? 'true' : 'false'"
								>
									<span class="composer-tools-menu-icon" aria-hidden="true">
										<svg viewBox="0 0 18 18" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round">
											<path d="M3.25 4.5h6.5"></path>
											<path d="M3.25 9h5"></path>
											<path d="M3.25 13.5h6"></path>
											<path d="m12.25 4.25 2 2-4 4H8.5V8.5l3.75-4.25Z"></path>
										</svg>
									</span>
									<span class="composer-tools-menu-label">计划模式</span>
									<input
										class="toggle-input"
										type="checkbox"
										:checked="isPlanningModeEnabled"
										:disabled="isBusy"
										@change="emit('toggle-planning-mode', $event.target.checked)"
									/>
									<span class="toggle-switch"></span>
								</label>
								<label
									v-if="showVisualizationToggle"
									class="composer-tools-menu-row composer-tools-menu-toggle"
									role="menuitemcheckbox"
									:aria-checked="visualizationEnabled ? 'true' : 'false'"
								>
									<span class="composer-tools-menu-icon" aria-hidden="true">
										<svg viewBox="0 0 18 18" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round">
											<circle cx="4" cy="9" r="1.75"></circle>
											<circle cx="14" cy="4" r="1.75"></circle>
											<circle cx="14" cy="14" r="1.75"></circle>
											<path d="M5.6 8.2 12.35 4.8"></path>
											<path d="m5.6 9.8 6.75 3.4"></path>
										</svg>
									</span>
									<span class="composer-tools-menu-label">可视化</span>
									<input
										class="toggle-input"
										type="checkbox"
										:checked="visualizationEnabled"
										@change="emit('toggle-visualization-mode', $event.target.checked)"
									/>
									<span class="toggle-switch"></span>
								</label>
							</div>
						</div>
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
