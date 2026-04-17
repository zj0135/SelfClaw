<script setup>
import { computed, ref } from 'vue';

const props = defineProps({
	open: {
		type: Boolean,
		default: false,
	},
	settingsSections: {
		type: Array,
		default: () => [],
	},
	activeSection: {
		type: String,
		default: 'profile',
	},
	activeSettingsMeta: {
		type: Object,
		default: null,
	},
	visibleFeedback: {
		type: Object,
		default: null,
	},
	selectedProfile: {
		type: Object,
		default: null,
	},
	profiles: {
		type: Array,
		default: () => [],
	},
	selectedProfileId: {
		type: String,
		default: '',
	},
	profileSummaryCards: {
		type: Array,
		default: () => [],
	},
	selectedWorkspace: {
		type: Object,
		default: null,
	},
	workspaceRoots: {
		type: Array,
		default: () => [],
	},
	selectedWorkspaceRootId: {
		type: String,
		default: '',
	},
	workspaceSummaryCards: {
		type: Array,
		default: () => [],
	},
	channels: {
		type: Array,
		default: () => [],
	},
	selectedThemeLabel: {
		type: String,
		default: '',
	},
	themeOptions: {
		type: Array,
		default: () => [],
	},
	selectedThemeId: {
		type: String,
		default: 'system',
	},
});

const emit = defineEmits([
	'close',
	'select-section',
	'panel-scroll',
	'select-profile',
	'edit-profile',
	'delete-profile',
	'create-profile',
	'select-workspace',
	'edit-workspace',
	'delete-workspace',
	'create-workspace',
	'toggle-channel',
	'edit-channel',
	'select-theme',
]);

const panelEl = ref(null);

const enabledChannelCount = computed(() => props.channels.filter((item) => item.isEnabled).length);

function onPanelScroll(event) {
	const panel = event.target instanceof HTMLElement ? event.target : null;
	if (panel) {
		emit('panel-scroll', panel.scrollTop);
	}
}

function onChannelToggle(channel, event) {
	emit('toggle-channel', { channel, enabled: Boolean(event.target.checked) });
}

defineExpose({
	getPanelEl: () => panelEl.value,
});
</script>

<template>
	<div id="settings-overlay" class="settings-overlay" :class="{ open }" @click.self="emit('close')">
		<div v-if="open" class="settings-panel" role="dialog" aria-modal="true" aria-label="系统设置">
			<aside class="settings-nav">
				<div class="settings-nav-header">
					<div class="settings-title">系统设置</div>
					<div class="settings-hint">左侧切换模块，右侧集中完成当前配置。</div>
				</div>

				<div class="settings-nav-list">
					<button
						v-for="section in settingsSections"
						:key="section.id"
						class="settings-nav-item"
						:class="{ active: activeSection === section.id }"
						type="button"
						:aria-pressed="activeSection === section.id"
						@click="emit('select-section', section.id)"
					>
						<div class="settings-nav-item-top">
							<div class="settings-nav-item-title">{{ section.title }}</div>
							<div class="settings-nav-item-badge">{{ section.badge }}</div>
						</div>
						<div class="settings-nav-item-description">{{ section.description }}</div>
					</button>
				</div>

				<div class="settings-nav-footer">
					<button class="ghost-btn" type="button" @click="emit('close')">完成</button>
				</div>
			</aside>

			<div ref="panelEl" class="settings-content" @scroll="onPanelScroll">
				<div class="settings-header">
					<div>
						<div class="field-label">{{ activeSettingsMeta?.eyebrow }}</div>
						<div class="settings-section-title settings-section-title-hero">{{ activeSettingsMeta?.title }}</div>
						<div class="settings-hint settings-header-hint">{{ activeSettingsMeta?.description }}</div>
					</div>
					<button class="close-btn" type="button" aria-label="关闭" @click="emit('close')">&times;</button>
				</div>

				<div v-if="visibleFeedback" class="settings-feedback" :class="visibleFeedback.level === 'error' ? 'error' : 'success'">
					{{ visibleFeedback.message }}
				</div>

				<section v-if="activeSection === 'profile'" class="settings-section settings-section-active">
					<div class="settings-section-header">
						<div class="settings-section-copy">
							<div class="field-label">当前配置</div>
							<div class="settings-section-title">模型选择与管理</div>
						</div>
						<div class="settings-badge">{{ selectedProfile ? '已选择' : '未选择' }}</div>
					</div>
					<div class="field-group">
						<div class="field-label">当前配置</div>
						<div class="settings-select-row">
							<select id="profile-select" class="field-select" :value="selectedProfileId || ''" @change="emit('select-profile', $event.target.value)">
								<option value="">未选择配置</option>
								<option v-for="option in profiles" :key="option.id" :value="option.id">{{ option.label }}</option>
							</select>
							<button class="ghost-btn compact-btn" type="button" :disabled="!selectedProfile" @click="emit('edit-profile')">编辑</button>
							<button class="ghost-btn compact-btn danger-btn" type="button" :disabled="!selectedProfile" @click="emit('delete-profile')">删除</button>
							<button class="icon-add-btn" type="button" aria-label="新增模型配置" @click="emit('create-profile')">+</button>
						</div>
					</div>
					<div class="selected-summary-grid">
						<div v-for="card in profileSummaryCards" :key="card.label" class="selected-summary-card">
							<div class="selected-summary-label">{{ card.label }}</div>
							<div class="selected-summary-value">{{ card.value }}</div>
						</div>
					</div>
				</section>

				<section v-else-if="activeSection === 'workspace'" class="settings-section settings-section-active">
					<div class="settings-section-header">
						<div class="settings-section-copy">
							<div class="field-label">当前工作区</div>
							<div class="settings-section-title">工作区绑定与切换</div>
						</div>
						<div class="settings-badge">{{ selectedWorkspace ? '已绑定' : '未绑定' }}</div>
					</div>
					<div class="field-group">
						<div class="field-label">当前工作区</div>
						<div class="settings-select-row">
							<select id="workspace-select" class="field-select" :value="selectedWorkspaceRootId || ''" @change="emit('select-workspace', $event.target.value)">
								<option value="">未绑定工作区</option>
								<option v-for="option in workspaceRoots" :key="option.id" :value="option.id">{{ option.label }}</option>
							</select>
							<button class="ghost-btn compact-btn" type="button" :disabled="!selectedWorkspace" @click="emit('edit-workspace')">编辑</button>
							<button class="ghost-btn compact-btn danger-btn" type="button" :disabled="!selectedWorkspace" @click="emit('delete-workspace')">删除</button>
							<button class="icon-add-btn" type="button" aria-label="新增工作区" @click="emit('create-workspace')">+</button>
						</div>
					</div>
					<div class="selected-summary-grid">
						<div v-for="card in workspaceSummaryCards" :key="card.label" class="selected-summary-card">
							<div class="selected-summary-label">{{ card.label }}</div>
							<div class="selected-summary-value">{{ card.value }}</div>
						</div>
					</div>
				</section>

				<section v-else-if="activeSection === 'channels'" class="settings-section settings-section-active">
					<div class="settings-section-header">
						<div class="settings-section-copy">
							<div class="field-label">支持的频道</div>
							<div class="settings-section-title">频道接入与监听</div>
						</div>
						<div class="settings-badge">{{ enabledChannelCount }} / {{ channels.length }}</div>
					</div>
					<div class="channel-card-list">
						<article v-for="channel in channels" :key="channel.id" class="channel-card" :class="[{ enabled: channel.isEnabled }, channel.status]">
							<div class="channel-card-top">
								<div class="channel-card-copy">
									<div class="field-label">{{ channel.name }}</div>
									<div class="settings-section-title">{{ channel.displayName || channel.name }}</div>
									<div class="settings-hint">{{ channel.description }}</div>
								</div>
								<label class="toggle-field channel-toggle">
									<input class="toggle-input" type="checkbox" :checked="channel.isEnabled" @change="onChannelToggle(channel, $event)" />
									<span class="toggle-switch"></span>
									<span class="toggle-label">{{ channel.isEnabled ? '已开启' : '已关闭' }}</span>
								</label>
							</div>
							<div class="selected-summary-grid channel-summary-grid">
								<div v-for="summary in channel.summaryItems" :key="summary.label" class="selected-summary-card">
									<div class="selected-summary-label">{{ summary.label }}</div>
									<div class="selected-summary-value">{{ summary.value }}</div>
								</div>
							</div>
							<div v-if="channel.statusDetail" class="settings-hint channel-status-detail">{{ channel.statusDetail }}</div>
							<div class="channel-card-actions">
								<div class="settings-badge">{{ channel.statusLabel }}</div>
								<button class="ghost-btn compact-btn" type="button" @click="emit('edit-channel', channel)">配置</button>
							</div>
						</article>
					</div>
				</section>

				<section v-else class="settings-section settings-section-active">
					<div class="settings-section-header">
						<div class="settings-section-copy">
							<div class="field-label">界面主题</div>
							<div class="settings-section-title">主题与外观</div>
						</div>
						<div class="settings-badge">{{ selectedThemeLabel }}</div>
					</div>
					<div class="field-group">
						<div class="field-label">界面主题</div>
						<select id="theme-select" class="field-select" :value="selectedThemeId || 'system'" @change="emit('select-theme', $event.target.value)">
							<option v-for="option in themeOptions" :key="option.id" :value="option.id">{{ option.label }}</option>
						</select>
					</div>
				</section>
			</div>
		</div>
	</div>
</template>
