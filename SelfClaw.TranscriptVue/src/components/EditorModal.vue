<script setup>
import { computed } from 'vue';
import { formatSamplingValue } from '../utils/editor';

const props = defineProps({
	open: {
		type: Boolean,
		default: false,
	},
	editor: {
		type: Object,
		required: true,
	},
	profiles: {
		type: Array,
		default: () => [],
	},
});

const emit = defineEmits(['close', 'pick-workspace-path', 'save']);

const title = computed(() => {
	if (props.editor.kind === 'profile') {
		return props.editor.mode === 'create' ? '新增模型配置' : '编辑模型配置';
	}

	if (props.editor.kind === 'channel') {
		return '编辑频道配置';
	}

	return props.editor.mode === 'create' ? '新增工作区' : '编辑工作区';
});

const description = computed(() => {
	if (props.editor.kind === 'profile') {
		return props.editor.mode === 'create'
			? '填写名称、Endpoint、模型、采样参数和 API Key 后保存，新配置会自动加入下拉列表并切换到当前选择。'
			: '你可以更新当前模型配置和采样参数；如果不需要替换密钥，API Key 留空即可。';
	}

	if (props.editor.kind === 'channel') {
		return '填写频道名称、绑定模型和当前渠道要求的连接字段后保存；开启开关后就会开始接收该渠道消息。';
	}

	return props.editor.mode === 'create'
		? '填写名称并选择本机目录后保存，工作区会自动加入下拉列表并设为当前选择。'
		: '在这里调整当前工作区的显示名称或重新选择目录，然后保存变更。';
});
</script>

<template>
	<div id="editor-overlay" class="editor-overlay" :class="{ open }" @click.self="emit('close')">
		<div v-if="open && editor.draft" class="editor-panel" role="dialog" aria-modal="true" :aria-label="title">
			<div class="editor-header">
				<div>
					<div class="editor-title">{{ title }}</div>
					<div class="settings-hint">{{ description }}</div>
				</div>
				<button class="close-btn" type="button" aria-label="关闭" @click="emit('close')">&times;</button>
			</div>

			<div v-if="editor.feedback" class="settings-feedback" :class="editor.feedback.level === 'error' ? 'error' : 'success'">
				{{ editor.feedback.message }}
			</div>

			<div class="editor-body">
				<template v-if="editor.kind === 'profile'">
					<div class="field-inline">
						<div>
							<div class="field-label">配置名称</div>
							<input id="editor-profile-name" v-model="editor.draft.name" class="field-input" type="text" placeholder="例如：OpenAI / 本地代理" />
						</div>
						<div>
							<div class="field-label">模型</div>
							<input id="editor-profile-model" v-model="editor.draft.model" class="field-input" type="text" placeholder="例如：gpt-4.1-mini" />
						</div>
					</div>
					<div>
						<div class="field-label">Endpoint</div>
						<input id="editor-profile-endpoint" v-model="editor.draft.endpoint" class="field-input" type="text" placeholder="https://api.openai.com/v1" />
					</div>
					<div class="field-inline field-inline-ranges">
						<div class="range-field" :class="{ disabled: !editor.draft.temperatureEnabled }">
							<div class="range-header">
								<div>
									<div class="field-label">Temperature</div>
									<label class="toggle-field">
										<input id="editor-profile-temperature-enabled" v-model="editor.draft.temperatureEnabled" class="toggle-input" type="checkbox" />
										<span class="toggle-switch"></span>
										<span class="toggle-label">启用</span>
									</label>
								</div>
								<div class="range-value">{{ formatSamplingValue(editor.draft.temperature, 2) }}</div>
							</div>
							<input
								id="editor-profile-temperature"
								v-model.number="editor.draft.temperature"
								class="field-range"
								type="range"
								min="0"
								max="2"
								step="0.01"
								:disabled="!editor.draft.temperatureEnabled"
							/>
						</div>
						<div class="range-field" :class="{ disabled: !editor.draft.topPEnabled }">
							<div class="range-header">
								<div>
									<div class="field-label">Top-P</div>
									<label class="toggle-field">
										<input id="editor-profile-top-p-enabled" v-model="editor.draft.topPEnabled" class="toggle-input" type="checkbox" />
										<span class="toggle-switch"></span>
										<span class="toggle-label">启用</span>
									</label>
								</div>
								<div class="range-value">{{ formatSamplingValue(editor.draft.topP, 1) }}</div>
							</div>
							<input
								id="editor-profile-top-p"
								v-model.number="editor.draft.topP"
								class="field-range"
								type="range"
								min="0"
								max="1"
								step="0.01"
								:disabled="!editor.draft.topPEnabled"
							/>
						</div>
					</div>
					<div>
						<div class="field-label">API Key</div>
						<input
							id="editor-profile-api-key"
							v-model="editor.draft.apiKey"
							class="field-input"
							type="password"
							:placeholder="editor.mode === 'create' ? '新增配置时必填' : '留空则保留现有密钥'"
						/>
					</div>
				</template>

				<template v-else-if="editor.kind === 'channel'">
					<div class="field-inline">
						<div>
							<div class="field-label">频道名称</div>
							<input id="editor-channel-display-name" v-model="editor.draft.displayName" class="field-input" type="text" placeholder="例如：我的飞书" />
						</div>
						<div>
							<div class="field-label">绑定模型</div>
							<select id="editor-channel-profile" v-model="editor.draft.profileId" class="field-select">
								<option value="">请选择模型</option>
								<option v-for="option in profiles" :key="option.id" :value="option.id">{{ option.label }}</option>
							</select>
						</div>
					</div>
					<div v-for="field in editor.draft.fields" :key="field.key">
						<div class="field-label">{{ field.label }}</div>
						<textarea
							v-if="field.kind === 'multiline'"
							v-model="field.value"
							class="field-input field-textarea"
							:placeholder="field.placeholder || ''"
						></textarea>
						<input
							v-else-if="field.kind === 'secret'"
							v-model="field.value"
							class="field-input"
							type="password"
							:placeholder="field.hasValue ? '留空则保留现有密钥' : (field.placeholder || '请填写')"
						/>
						<input v-else v-model="field.value" class="field-input" type="text" :placeholder="field.placeholder || ''" />
						<div v-if="field.description" class="settings-hint channel-field-hint">{{ field.description }}</div>
					</div>
				</template>

				<template v-else>
					<div>
						<div class="field-label">显示名称</div>
						<input id="editor-workspace-name" v-model="editor.draft.name" class="field-input" type="text" placeholder="例如：SelfClaw 主工作区" />
					</div>
					<div>
						<div class="field-label">工作区位置</div>
						<div class="field-picker-row">
							<div class="field-readonly">{{ editor.draft.rootPath || '请选择文件夹' }}</div>
							<button class="ghost-btn compact-btn" type="button" @click="emit('pick-workspace-path')">选择</button>
						</div>
					</div>
				</template>
			</div>

			<div class="editor-footer">
				<button class="ghost-btn" type="button" @click="emit('close')">取消</button>
				<button class="primary-btn" type="button" @click="emit('save')">保存</button>
			</div>
		</div>
	</div>
</template>
