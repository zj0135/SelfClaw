<script setup>
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue';
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

const emit = defineEmits(['close', 'pick-workspace-path', 'fetch-models', 'save']);

const modelComboboxRef = ref(null);
const modelInputRef = ref(null);
const isModelMenuOpen = ref(false);

const title = computed(() => {
	if (props.editor.kind === 'profile') {
		return props.editor.mode === 'create' ? '新增模型配置' : '编辑模型配置';
	}

	if (props.editor.kind === 'channel') {
		return '编辑频道配置';
	}

	if (props.editor.kind === 'mcp') {
		return props.editor.mode === 'create' ? '新增 MCP 服务' : '编辑 MCP 服务';
	}

	if (props.editor.kind === 'agent') {
		return props.editor.mode === 'create' ? '新增智能体' : '编辑智能体';
	}

	return props.editor.mode === 'create' ? '新增工作区' : '编辑工作区';
});

const profileModelOptions = computed(() => {
	if (props.editor.kind !== 'profile' || !props.editor.draft) {
		return [];
	}

	return [...new Set([...(props.editor.draft.modelOptions || []), props.editor.draft.model].filter((item) => Boolean(item && item.trim())))];
});

const canFetchProfileModels = computed(() => props.editor.kind === 'profile' && Boolean(props.editor.draft?.endpoint?.trim()));

function closeModelMenu() {
	isModelMenuOpen.value = false;
}

async function openModelMenu(fetchOnOpen = false) {
	if (props.editor.kind !== 'profile' || !props.editor.draft) {
		return;
	}

	isModelMenuOpen.value = true;

	if (fetchOnOpen && canFetchProfileModels.value && !props.editor.draft.isFetchingModels && !props.editor.draft.hasFetchedModelOptions) {
		emit('fetch-models');
	}

	await nextTick();
	modelInputRef.value?.focus?.();
}

function toggleModelMenu() {
	if (isModelMenuOpen.value) {
		closeModelMenu();
		return;
	}

	void openModelMenu(true);
}

function handleModelInputKeydown(event) {
	if (event.key === 'Escape' && isModelMenuOpen.value) {
		event.preventDefault();
		closeModelMenu();
	}
}

function selectProfileModel(option) {
	if (props.editor.kind !== 'profile' || !props.editor.draft) {
		return;
	}

	props.editor.draft.model = option;
	closeModelMenu();
}

function handleDocumentPointerDown(event) {
	if (!isModelMenuOpen.value) {
		return;
	}

	const container = modelComboboxRef.value;
	if (container && !container.contains(event.target)) {
		closeModelMenu();
	}
}

watch(
	() => props.open,
	(isOpen) => {
		if (!isOpen) {
			closeModelMenu();
		}
	}
);

onMounted(() => {
	document.addEventListener('pointerdown', handleDocumentPointerDown, true);
});

onBeforeUnmount(() => {
	document.removeEventListener('pointerdown', handleDocumentPointerDown, true);
});
</script>

<template>
	<div id="editor-overlay" class="editor-overlay" :class="{ open }" @click.self="emit('close')">
		<div v-if="open && editor.draft" class="editor-panel" role="dialog" aria-modal="true" :aria-label="title">
			<div class="editor-header">
				<div>
					<div class="editor-title">{{ title }}</div>
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
							<input id="editor-profile-name" v-model="editor.draft.name" class="field-input" type="text"
								placeholder="例如 OpenAI / 本地模型" />
						</div>
						<div>
							<div class="field-label">模型</div>
							<div ref="modelComboboxRef" class="field-combobox" :class="{ open: isModelMenuOpen }">
								<div class="field-combobox-trigger" role="combobox" aria-haspopup="listbox"
									:aria-expanded="isModelMenuOpen ? 'true' : 'false'">
									<input id="editor-profile-model" ref="modelInputRef" v-model="editor.draft.model"
										class="field-combobox-input" type="text" placeholder="例如 gpt-4.1-mini"
										@keydown="handleModelInputKeydown" />
									<button class="field-combobox-toggle" type="button" aria-label="切换模型列表" @click.stop="toggleModelMenu">
										<span class="field-combobox-chevron" aria-hidden="true"></span>
									</button>
								</div>
								<div v-if="isModelMenuOpen" class="field-combobox-menu"
									:class="{ 'has-options': canFetchProfileModels && !editor.draft.isFetchingModels && profileModelOptions.length > 0 }"
									role="listbox">
									<div v-if="editor.draft.isFetchingModels" class="field-combobox-status">正在获取模型列表...</div>
									<div v-else-if="!canFetchProfileModels" class="field-combobox-status">请先填写 Endpoint</div>
									<template v-else-if="profileModelOptions.length > 0">
										<button v-for="option in profileModelOptions" :key="option" class="field-combobox-option" type="button"
											@click="selectProfileModel(option)">
											{{ option }}
										</button>
									</template>
									<div v-else class="field-combobox-status">暂无可用模型</div>
								</div>
							</div>
						</div>
					</div>
					<div>
						<div class="field-label">Endpoint</div>
						<input id="editor-profile-endpoint" v-model="editor.draft.endpoint" class="field-input" type="text"
							placeholder="https://api.openai.com/v1" />
					</div>
					<div class="field-inline field-inline-ranges">
						<div class="range-field" :class="{ disabled: !editor.draft.temperatureEnabled }">
							<div class="range-header">
								<div>
									<div class="field-label">Temperature</div>
									<label class="toggle-field">
										<input id="editor-profile-temperature-enabled" v-model="editor.draft.temperatureEnabled"
											class="toggle-input" type="checkbox" />
										<span class="toggle-switch"></span>
										<span class="toggle-label">启用</span>
									</label>
								</div>
								<div class="range-value">{{ formatSamplingValue(editor.draft.temperature, 2) }}</div>
							</div>
							<input id="editor-profile-temperature" v-model.number="editor.draft.temperature" class="field-range" type="range"
								min="0" max="2" step="0.01" :disabled="!editor.draft.temperatureEnabled" />
						</div>
						<div class="range-field" :class="{ disabled: !editor.draft.topPEnabled }">
							<div class="range-header">
								<div>
									<div class="field-label">Top-P</div>
									<label class="toggle-field">
										<input id="editor-profile-top-p-enabled" v-model="editor.draft.topPEnabled" class="toggle-input"
											type="checkbox" />
										<span class="toggle-switch"></span>
										<span class="toggle-label">启用</span>
									</label>
								</div>
								<div class="range-value">{{ formatSamplingValue(editor.draft.topP, 1) }}</div>
							</div>
							<input id="editor-profile-top-p" v-model.number="editor.draft.topP" class="field-range" type="range"
								min="0" max="1" step="0.01" :disabled="!editor.draft.topPEnabled" />
						</div>
					</div>
					<div>
						<div class="field-label">API Key</div>
						<input id="editor-profile-api-key" v-model="editor.draft.apiKey" class="field-input" type="password"
							:placeholder="editor.mode === 'create' ? '请输入 API Key' : '留空表示沿用已有 API Key'" />
					</div>
				</template>

				<template v-else-if="editor.kind === 'channel'">
					<div class="field-inline">
						<div>
							<div class="field-label">频道显示名称</div>
							<input id="editor-channel-display-name" v-model="editor.draft.displayName" class="field-input" type="text"
								placeholder="例如 飞书机器人" />
						</div>
						<div>
							<div class="field-label">绑定模型配置</div>
							<select id="editor-channel-profile" v-model="editor.draft.profileId" class="field-select">
								<option value="">请选择模型配置</option>
								<option v-for="option in profiles" :key="option.id" :value="option.id">{{ option.label }}</option>
							</select>
						</div>
					</div>
					<div v-for="field in editor.draft.fields" :key="field.key">
						<div class="field-label">{{ field.label }}</div>
						<textarea v-if="field.kind === 'multiline'" v-model="field.value" class="field-input field-textarea"
							:placeholder="field.placeholder || ''"></textarea>
						<input v-else-if="field.kind === 'secret'" v-model="field.value" class="field-input" type="password"
							:placeholder="field.hasValue ? '留空表示沿用已有密钥' : (field.placeholder || '请输入密钥')" />
						<input v-else v-model="field.value" class="field-input" type="text" :placeholder="field.placeholder || ''" />
						<div v-if="field.description" class="settings-hint channel-field-hint">{{ field.description }}</div>
					</div>
				</template>

				<template v-else-if="editor.kind === 'mcp'">
					<div class="field-inline">
						<div>
							<div class="field-label">服务 ID</div>
							<input id="editor-mcp-server-id" v-model="editor.draft.serverId" class="field-input" type="text"
								:readonly="editor.mode === 'edit'" placeholder="filesystem" />
						</div>
						<div>
							<div class="field-label">显示名称</div>
							<input id="editor-mcp-display-name" v-model="editor.draft.displayName" class="field-input" type="text"
								placeholder="Filesystem" />
						</div>
					</div>
					<div>
						<div class="field-label">命令</div>
						<input id="editor-mcp-command" v-model="editor.draft.command" class="field-input" type="text" placeholder="npx" />
					</div>
					<div>
						<div class="field-label">参数</div>
						<textarea id="editor-mcp-args" v-model="editor.draft.argsText" class="field-input field-textarea"
							placeholder="-y&#10;@modelcontextprotocol/server-filesystem&#10;D:\Repositories"></textarea>
						<div class="settings-hint channel-field-hint">每行一个参数。</div>
					</div>
					<div>
						<div class="field-label">环境变量</div>
						<textarea id="editor-mcp-env" v-model="editor.draft.envText" class="field-input field-textarea"
							placeholder="KEY=value"></textarea>
						<div class="settings-hint channel-field-hint">每行一个 `KEY=VALUE`。</div>
					</div>
					<label class="toggle-field">
						<input id="editor-mcp-enabled" v-model="editor.draft.enabled" class="toggle-input" type="checkbox" />
						<span class="toggle-switch"></span>
						<span class="toggle-label">启用</span>
					</label>
				</template>

				<template v-else-if="editor.kind === 'agent'">
					<div class="field-inline">
						<div>
							<div class="field-label">智能体 ID</div>
							<input id="editor-agent-id" v-model="editor.draft.agentId" class="field-input" type="text"
								:readonly="editor.mode === 'edit' && editor.draft.isBuiltIn" placeholder="code-review" />
						</div>
						<div>
							<div class="field-label">执行模式</div>
							<select id="editor-agent-mode" v-model="editor.draft.mode" class="field-select">
								<option value="direct">direct</option>
								<option value="plan">plan</option>
							</select>
						</div>
					</div>
					<div class="field-inline">
						<div>
							<div class="field-label">名称</div>
							<input id="editor-agent-name" v-model="editor.draft.name" class="field-input" type="text" placeholder="代码审查专家" />
						</div>
						<div>
							<div class="field-label">tools</div>
							<input id="editor-agent-tools" v-model="editor.draft.toolPolicy" class="field-input" type="text" readonly />
						</div>
					</div>
					<div>
						<div class="field-label">描述</div>
						<input id="editor-agent-description" v-model="editor.draft.description" class="field-input" type="text"
							placeholder="代码审查专家，检查代码质量和安全性" />
					</div>
					<div class="field-inline">
						<div>
							<div class="field-label">skills</div>
							<textarea id="editor-agent-skills" v-model="editor.draft.skillsText" class="field-input field-textarea"
								placeholder="skillName1&#10;skillName2"></textarea>
							<div class="settings-hint channel-field-hint">每行一个 skill id。</div>
						</div>
						<div>
							<div class="field-label">mcpServers</div>
							<textarea id="editor-agent-mcp-servers" v-model="editor.draft.mcpServersText" class="field-input field-textarea"
								placeholder="mcpServerName1&#10;mcpServerName2"></textarea>
							<div class="settings-hint channel-field-hint">每行一个 MCP server id。</div>
						</div>
					</div>
					<div>
						<div class="field-label">正文提示词</div>
						<textarea id="editor-agent-instructions" v-model="editor.draft.instructions" class="field-input field-textarea field-textarea-lg"
							placeholder="你是一位资深代码审查员，负责确保代码质量。"></textarea>
					</div>
					<div v-if="editor.draft.warnings?.length" class="settings-hint channel-field-hint">
						<div v-for="warning in editor.draft.warnings" :key="warning">{{ warning }}</div>
					</div>
				</template>

				<template v-else>
					<div>
						<div class="field-label">工作区名称</div>
						<input id="editor-workspace-name" v-model="editor.draft.name" class="field-input" type="text"
							placeholder="例如 SelfClaw 源码仓库" />
					</div>
					<div>
						<div class="field-label">工作区路径</div>
						<div class="field-picker-row">
							<input id="editor-workspace-root-path" class="field-input field-path-input" type="text" readonly
								:value="editor.draft.rootPath" placeholder="请选择一个文件夹" @click="emit('pick-workspace-path')"
								@keydown.enter.prevent="emit('pick-workspace-path')" @keydown.space.prevent="emit('pick-workspace-path')" />
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
