<script setup>
import { onMounted, onBeforeUnmount } from 'vue';
import {
	Settings,
	RotateCcw,
	Sparkles,
	Loader2,
	AlertCircle,
	CheckCircle2,
} from 'lucide-vue-next';
import { useImageStudio } from '../composables/useImageStudio.js';
import ImagePreviewGrid from '../components/image-generation/ImagePreviewGrid.vue';
import ImageModelPicker from '../components/image-generation/ImageModelPicker.vue';
import ImagePromptSection from '../components/image-generation/ImagePromptSection.vue';
import ImageSizeSelector from '../components/image-generation/ImageSizeSelector.vue';
import ImageSliderControls from '../components/image-generation/ImageSliderControls.vue';
import ImageAdvancedOptions from '../components/image-generation/ImageAdvancedOptions.vue';

const emit = defineEmits(['preview-image']);

const {
	models,
	loadingModels,
	selectedModelProfileId,
	prompt,
	negativePrompt,
	selectedSizeKey,
	count,
	steps,
	cfgScale,
	quality,
	style,
	referenceImage,
	isGenerating,
	zoomLevel,
	gallery,
	statusMessage,
	errorMessage,
	appendTag,
	resetParams,
	removeImage,
	setZoom,
	setReferenceImageFile,
	clearReferenceImage,
	generate,
} = useImageStudio();

function handlePreviewFullscreen(image) {
	if (image?.url) {
		emit('preview-image', {
			src: image.url,
			alt: image.prompt || 'Generated preview image',
		});
	} else if (gallery.length > 0) {
		emit('preview-image', {
			src: gallery[0].url,
			alt: gallery[0].prompt || 'Generated preview image',
		});
	}
}

function handleKeydown(event) {
	if (event.ctrlKey && event.key === 'Enter') {
		event.preventDefault();
		generate();
	}
}

onMounted(() => {
	window.addEventListener('keydown', handleKeydown);
});

onBeforeUnmount(() => {
	window.removeEventListener('keydown', handleKeydown);
});
</script>

<template>
	<div class="studio-root">
		<!-- 顶栏页面标题 -->
		<header class="studio-header">
			<h1 class="studio-title">生图工作台</h1>
			<p class="studio-sub">输入创意，生成高质量的图片</p>
		</header>

		<!-- 左右两栏主舞台 -->
		<div class="studio-body">
			<!-- 左侧：图片预览卡片 -->
			<section class="left-stage">
				<ImagePreviewGrid
					:gallery="gallery"
					:zoom-level="zoomLevel"
					:reference-image="referenceImage"
					@zoom-in="setZoom(10)"
					@zoom-out="setZoom(-10)"
					@remove-image="removeImage"
					@preview-fullscreen="handlePreviewFullscreen"
					@upload-reference="setReferenceImageFile"
					@clear-reference="clearReferenceImage"
				/>
			</section>

			<!-- 右侧：参数设置卡片 -->
			<section class="right-panel">
				<!-- 卡片顶栏 -->
				<div class="params-header">
					<div class="params-title-group">
						<Settings :size="18" class="settings-icon" />
						<span class="params-title">参数设置</span>
					</div>

					<button class="reset-btn" type="button" title="重置参数" @click="resetParams">
						<RotateCcw :size="14" />
						<span>重置</span>
					</button>
				</div>

				<!-- 可滚动表单区域 -->
				<div class="params-form-scroll">
					<!-- 模型选择（严格仅展示生图协议模型） -->
					<ImageModelPicker
						:models="models"
						:selected-model-id="selectedModelProfileId"
						:loading="loadingModels"
						@select-model="selectedModelProfileId = $event"
					/>

					<!-- 提示词与标签 -->
					<ImagePromptSection
						v-model:prompt="prompt"
						v-model:negative-prompt="negativePrompt"
						@append-tag="appendTag"
					/>

					<!-- 图片尺寸选择器 -->
					<ImageSizeSelector
						:selected-key="selectedSizeKey"
						@select-size="selectedSizeKey = $event"
					/>

					<!-- 滑块控制群：数量、步数、CFG -->
					<ImageSliderControls
						v-model:count="count"
						v-model:steps="steps"
						v-model:cfg-scale="cfgScale"
					/>

					<!-- 高级设置折叠栏：画质与画风 -->
					<ImageAdvancedOptions
						v-model:quality="quality"
						v-model:style-val="style"
					/>
				</div>

				<!-- 状态与错误提示栏 -->
				<div v-if="errorMessage" class="notice-bar error">
					<AlertCircle :size="14" class="notice-icon" />
					<span class="notice-text">{{ errorMessage }}</span>
				</div>
				<div v-else-if="statusMessage" class="notice-bar success">
					<CheckCircle2 :size="14" class="notice-icon" />
					<span class="notice-text">{{ statusMessage }}</span>
				</div>

				<!-- 底部开始生成大按钮 -->
				<div class="params-footer">
					<button
						type="button"
						class="generate-action-btn"
						:disabled="isGenerating || !prompt.trim()"
						@click="generate"
					>
						<Loader2 v-if="isGenerating" :size="18" class="spin-icon" />
						<Sparkles v-else :size="18" class="sparkle-icon" />
						<span class="btn-main-text">
							{{ isGenerating ? '正在生成中...' : `开始生成 (${count}张)` }}
						</span>
						<span class="shortcut-chip">Ctrl ↵</span>
					</button>
				</div>
			</section>
		</div>
	</div>
</template>

<style scoped>
.studio-root {
	display: flex;
	flex-direction: column;
	width: 100%;
	height: 100%;
	min-height: 0;
	padding: 24px 32px 32px;
	box-sizing: border-box;
	background: var(--surface-canvas, #f5f6f8);
	overflow: hidden;
	color: var(--ink-1, #171a1f);
	font-family: inherit;
}

.studio-header {
	margin-bottom: 20px;
	flex-shrink: 0;
}

.studio-title {
	font-size: 22px;
	font-weight: 700;
	letter-spacing: -0.3px;
	color: var(--ink-1, #171a1f);
	margin: 0 0 4px;
}

.studio-sub {
	font-size: 13px;
	color: var(--ink-3, #6b7280);
	margin: 0;
}

.studio-body {
	flex: 1;
	min-height: 0;
	display: grid;
	grid-template-columns: minmax(0, 1.15fr) minmax(380px, 0.95fr);
	gap: 24px;
}

.left-stage {
	height: 100%;
	min-height: 0;
}

.right-panel {
	display: flex;
	flex-direction: column;
	height: 100%;
	min-height: 0;
	background: var(--surface-base, #ffffff);
	border: 1px solid var(--line-card, #e3e6ec);
	border-radius: 16px;
	box-shadow: 0 4px 20px -2px rgba(0, 0, 0, 0.04);
	overflow: hidden;
}

.params-header {
	display: flex;
	align-items: center;
	justify-content: space-between;
	padding: 16px 20px;
	border-bottom: 1px solid var(--line-1, rgba(0, 0, 0, 0.06));
	flex-shrink: 0;
}

.params-title-group {
	display: flex;
	align-items: center;
	gap: 8px;
	color: var(--ink-1, #171a1f);
	font-size: 15px;
	font-weight: 600;
}

.settings-icon {
	color: var(--accent-base, #3b5bfd);
}

.reset-btn {
	display: flex;
	align-items: center;
	gap: 5px;
	background: transparent;
	border: none;
	color: var(--ink-3, #6b7280);
	font-size: 12.5px;
	cursor: pointer;
	padding: 4px 8px;
	border-radius: 6px;
	transition: all 0.15s ease;
}

.reset-btn:hover {
	background: var(--surface-hover, #eef0f4);
	color: var(--ink-1, #171a1f);
}

.params-form-scroll {
	flex: 1;
	min-height: 0;
	overflow-y: auto;
	padding: 20px;
	display: flex;
	flex-direction: column;
	gap: 18px;
}

.notice-bar {
	display: flex;
	align-items: center;
	gap: 8px;
	margin: 0 20px 8px;
	padding: 8px 12px;
	border-radius: 8px;
	font-size: 12px;
	flex-shrink: 0;
}

.notice-bar.error {
	background: #fef2f2;
	color: #dc2626;
	border: 1px solid #fecaca;
}

.notice-bar.success {
	background: #f0fdf4;
	color: #16a34a;
	border: 1px solid #bbf7d0;
}

.notice-icon {
	flex-shrink: 0;
}

.notice-text {
	flex: 1;
	line-height: 1.4;
}

.params-footer {
	padding: 16px 20px;
	border-top: 1px solid var(--line-1, rgba(0, 0, 0, 0.06));
	background: var(--surface-base, #ffffff);
	flex-shrink: 0;
}

.generate-action-btn {
	display: flex;
	align-items: center;
	justify-content: center;
	gap: 10px;
	width: 100%;
	height: 46px;
	border: none;
	border-radius: 12px;
	background: var(--accent-base, #3b5bfd);
	color: #ffffff;
	font-size: 14.5px;
	font-weight: 600;
	cursor: pointer;
	box-shadow: 0 4px 14px color-mix(in srgb, var(--accent-base, #3b5bfd) 35%, transparent);
	transition: all 0.18s ease;
}

.generate-action-btn:hover:not(:disabled) {
	background: var(--accent-base-strong, #2f49d1);
	transform: translateY(-1px);
	box-shadow: 0 6px 18px color-mix(in srgb, var(--accent-base, #3b5bfd) 45%, transparent);
}

.generate-action-btn:disabled {
	opacity: 0.55;
	cursor: not-allowed;
	transform: none;
	box-shadow: none;
}

.btn-main-text {
	letter-spacing: 0.3px;
}

.shortcut-chip {
	font-size: 11px;
	background: rgba(255, 255, 255, 0.22);
	padding: 2px 6px;
	border-radius: 4px;
	letter-spacing: 0.5px;
	font-variant-numeric: tabular-nums;
}

.spin-icon {
	animation: spin 1s linear infinite;
}

@keyframes spin {
	from {
		transform: rotate(0deg);
	}
	to {
		transform: rotate(360deg);
	}
}

/* 响应式调整 */
@media (max-width: 960px) {
	.studio-body {
		grid-template-columns: 1fr;
		overflow-y: auto;
	}
	.left-stage {
		height: 480px;
	}
}
</style>
