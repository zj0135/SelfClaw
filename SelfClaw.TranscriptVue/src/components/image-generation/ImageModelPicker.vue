<script setup>
import { computed, ref, onMounted, onBeforeUnmount } from 'vue';
import { Layers, ChevronDown, Check, AlertCircle, RotateCw } from 'lucide-vue-next';

const props = defineProps({
	models: { type: Array, required: true },
	selectedModelId: { type: String, default: '' },
	loading: { type: Boolean, default: false },
});

const emit = defineEmits(['select-model', 'reload']);

const isOpen = ref(false);
const dropdownRef = ref(null);

// 没有模型时不编一个假模型顶上：显示的是什么就该是什么，否则用户会对着一个
// 不存在的模型点生成。
const activeModel = computed(() => {
	if (props.selectedModelId) {
		const found = props.models.find((model) => model.profileId === props.selectedModelId);
		if (found) return found;
	}
	return props.models[0] || null;
});

function toggleDropdown() {
	if (props.models.length === 0) return;
	isOpen.value = !isOpen.value;
}

function handleSelect(model) {
	emit('select-model', model.profileId);
	isOpen.value = false;
}

function onClickOutside(event) {
	if (dropdownRef.value && !dropdownRef.value.contains(event.target)) {
		isOpen.value = false;
	}
}

function onTriggerKeydown(event) {
	if (event.key === 'Enter' || event.key === ' ') {
		event.preventDefault();
		toggleDropdown();
	}
}

onMounted(() => {
	document.addEventListener('click', onClickOutside);
});

onBeforeUnmount(() => {
	document.removeEventListener('click', onClickOutside);
});
</script>

<template>
	<div ref="dropdownRef" class="model-picker-container">
		<label class="section-label">模型</label>

		<!-- 触发卡片 -->
		<div
			class="model-select-card"
			:class="{ active: isOpen, disabled: models.length === 0 }"
			role="button"
			:tabindex="models.length === 0 ? -1 : 0"
			:aria-expanded="isOpen"
			aria-haspopup="listbox"
			@click="toggleDropdown"
			@keydown="onTriggerKeydown"
		>
			<div class="model-icon-box">
				<Layers :size="20" class="icon-inner" />
			</div>

			<div class="model-text-group">
				<div class="model-title-row">
					<span class="model-title">{{ activeModel?.name || '未选择模型' }}</span>
					<span v-if="activeModel?.providerName" class="provider-badge">{{ activeModel.providerName }}</span>
				</div>
				<div class="model-sub">{{ activeModel?.description || '尚未配置生图模型' }}</div>
			</div>

			<div class="model-arrow">
				<ChevronDown :size="16" class="arrow-icon" :class="{ rotated: isOpen }" />
			</div>
		</div>

		<!-- 下拉浮层 -->
		<div v-if="isOpen" class="dropdown-popover" role="listbox">
			<div class="popover-header">
				<span>已过滤：仅生图协议模型 (OpenAIImages)</span>
			</div>

			<div class="model-list">
				<div
					v-for="item in models"
					:key="item.profileId"
					class="model-option"
					:class="{ selected: item.profileId === selectedModelId }"
					role="option"
					:aria-selected="item.profileId === selectedModelId"
					tabindex="0"
					@click="handleSelect(item)"
					@keydown.enter.prevent="handleSelect(item)"
				>
					<div class="option-content">
						<div class="option-name-row">
							<span class="option-name">{{ item.name }}</span>
							<span v-if="item.providerName" class="provider-pill">{{ item.providerName }}</span>
						</div>
						<div class="option-desc">{{ item.description }}</div>
					</div>
					<Check v-if="item.profileId === selectedModelId" :size="16" class="check-icon" />
				</div>
			</div>
		</div>

		<!-- 空态提示 -->
		<div v-if="models.length === 0 && !loading" class="no-models-hint">
			<AlertCircle :size="14" class="hint-icon" />
			<span>未检测到 OpenAIImages 格式的模型，可在提供商设置中添加</span>
			<button type="button" class="retry-btn" title="重新加载模型" @click="emit('reload')">
				<RotateCw :size="12" />
				<span>重试</span>
			</button>
		</div>
	</div>
</template>

<style scoped>
.model-picker-container {
	position: relative;
	display: flex;
	flex-direction: column;
	gap: 6px;
	width: 100%;
}

.section-label {
	font-size: 13px;
	font-weight: 600;
	color: var(--ink-1, #171a1f);
}

.model-select-card {
	display: flex;
	align-items: center;
	gap: 12px;
	padding: 10px 14px;
	background: var(--surface-base, #ffffff);
	border: 1px solid var(--line-card, #e3e6ec);
	border-radius: 12px;
	cursor: pointer;
	transition: all 0.15s ease;
}

.model-select-card:hover {
	border-color: var(--accent-base, #3b5bfd);
	background: var(--surface-soft, #f7f8fa);
}

.model-select-card:focus-visible {
	outline: 2px solid var(--accent-base, #3b5bfd);
	outline-offset: 2px;
}

.model-select-card.active {
	border-color: var(--accent-base, #3b5bfd);
	box-shadow: 0 0 0 3px color-mix(in srgb, var(--accent-base, #3b5bfd) 15%, transparent);
}

.model-select-card.disabled {
	cursor: default;
}

.model-icon-box {
	display: flex;
	align-items: center;
	justify-content: center;
	width: 36px;
	height: 36px;
	border-radius: 9px;
	background: color-mix(in srgb, var(--accent-base, #3b5bfd) 10%, var(--surface-base, #ffffff));
	color: var(--accent-base, #3b5bfd);
	flex-shrink: 0;
}

.model-text-group {
	flex: 1;
	min-width: 0;
	display: flex;
	flex-direction: column;
	gap: 2px;
}

.model-title-row {
	display: flex;
	align-items: center;
	gap: 8px;
}

.model-title {
	font-size: 13.5px;
	font-weight: 600;
	color: var(--ink-1, #171a1f);
	white-space: nowrap;
	overflow: hidden;
	text-overflow: ellipsis;
}

.provider-badge {
	font-size: 10px;
	padding: 1px 6px;
	background: var(--surface-muted, #f1f3f6);
	color: var(--ink-3, #6b7280);
	border-radius: 4px;
	font-weight: 500;
}

.model-sub {
	font-size: 11.5px;
	color: var(--ink-3, #6b7280);
	white-space: nowrap;
	overflow: hidden;
	text-overflow: ellipsis;
}

.model-arrow {
	color: var(--ink-4, #8a929e);
	display: flex;
	align-items: center;
}

.arrow-icon {
	transition: transform 0.2s;
}

.arrow-icon.rotated {
	transform: rotate(180deg);
}

.dropdown-popover {
	position: absolute;
	top: calc(100% + 6px);
	left: 0;
	right: 0;
	z-index: 50;
	background: var(--surface-base, #ffffff);
	border: 1px solid var(--line-card, #e3e6ec);
	border-radius: 12px;
	box-shadow: 0 10px 30px rgba(0, 0, 0, 0.12);
	overflow: hidden;
	animation: popoverFadeIn 0.15s ease-out;
}

@keyframes popoverFadeIn {
	from {
		opacity: 0;
		transform: translateY(-6px);
	}
	to {
		opacity: 1;
		transform: translateY(0);
	}
}

.popover-header {
	padding: 8px 12px;
	font-size: 11px;
	font-weight: 600;
	color: var(--accent-base, #3b5bfd);
	background: color-mix(in srgb, var(--accent-base, #3b5bfd) 6%, var(--surface-base, #ffffff));
	border-bottom: 1px solid var(--line-1, rgba(0, 0, 0, 0.06));
}

.model-list {
	max-height: 240px;
	overflow-y: auto;
	padding: 6px;
}

.model-option {
	display: flex;
	align-items: center;
	justify-content: space-between;
	padding: 8px 10px;
	border-radius: 8px;
	cursor: pointer;
	transition: background 0.12s;
}

.model-option:hover {
	background: var(--surface-hover, #eef0f4);
}

.model-option:focus-visible {
	outline: 2px solid var(--accent-base, #3b5bfd);
	outline-offset: -2px;
}

.model-option.selected {
	background: color-mix(in srgb, var(--accent-base, #3b5bfd) 8%, var(--surface-base, #ffffff));
}

.option-content {
	flex: 1;
	min-width: 0;
}

.option-name-row {
	display: flex;
	align-items: center;
	gap: 6px;
}

.option-name {
	font-size: 13px;
	font-weight: 600;
	color: var(--ink-1, #171a1f);
}

.provider-pill {
	font-size: 10px;
	padding: 1px 5px;
	background: var(--surface-muted, #f1f3f6);
	color: var(--ink-3, #6b7280);
	border-radius: 4px;
}

.option-desc {
	font-size: 11px;
	color: var(--ink-3, #6b7280);
	margin-top: 2px;
	white-space: nowrap;
	overflow: hidden;
	text-overflow: ellipsis;
}

.check-icon {
	color: var(--accent-base, #3b5bfd);
	margin-left: 8px;
}

.no-models-hint {
	display: flex;
	align-items: center;
	gap: 6px;
	font-size: 11.5px;
	color: #d97706;
	background: #fffbeb;
	padding: 6px 10px;
	border-radius: 8px;
	border: 1px solid #fde68a;
	margin-top: 2px;
}

.hint-icon {
	flex-shrink: 0;
}

.retry-btn {
	display: inline-flex;
	align-items: center;
	gap: 3px;
	margin-left: auto;
	padding: 2px 7px;
	font-size: 11px;
	color: #b45309;
	background: transparent;
	border: 1px solid #fcd34d;
	border-radius: 6px;
	cursor: pointer;
}

.retry-btn:hover {
	background: #fef3c7;
}
</style>
