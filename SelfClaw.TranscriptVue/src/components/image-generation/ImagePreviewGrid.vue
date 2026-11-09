<script setup>
import { ref } from 'vue';
import {
	Image as ImageIcon,
	Minus,
	Plus,
	Maximize2,
	Trash2,
	X,
	Plus as PlusIcon,
	Sparkles,
} from 'lucide-vue-next';
import { formatGeneratedAt } from '../../renderers/imageStudio.js';

const props = defineProps({
	gallery: { type: Array, required: true },
	zoomLevel: { type: Number, default: 100 },
	referenceImage: { type: Object, default: null },
	isGenerating: { type: Boolean, default: false },
});

const emit = defineEmits([
	'zoom-in',
	'zoom-out',
	'remove-image',
	'clear-gallery',
	'preview-fullscreen',
	'upload-reference',
	'clear-reference',
]);

const fileInputRef = ref(null);
const isDragging = ref(false);

function triggerFileInput() {
	fileInputRef.value?.click();
}

function onFileSelected(event) {
	const file = event.target.files?.[0];
	if (file) {
		emit('upload-reference', file);
	}
	event.target.value = '';
}

function onDrop(event) {
	isDragging.value = false;
	const file = event.dataTransfer?.files?.[0];
	if (file && file.type.startsWith('image/')) {
		emit('upload-reference', file);
	}
}

function onDragOver(event) {
	event.preventDefault();
	isDragging.value = true;
}

function onDragLeave() {
	isDragging.value = false;
}

function onUploadKeydown(event) {
	if (event.key === 'Enter' || event.key === ' ') {
		event.preventDefault();
		triggerFileInput();
	}
}

function previewAll() {
	if (props.gallery.length > 0) {
		emit('preview-fullscreen', props.gallery[0]);
	}
}
</script>

<template>
	<div class="preview-panel">
		<!-- 顶栏工具条 -->
		<div class="panel-header">
			<div class="title-group">
				<ImageIcon class="header-icon" :size="18" />
				<span class="header-title">图片预览</span>
				<span v-if="gallery.length" class="count-chip">{{ gallery.length }}</span>
			</div>

			<div class="tools-group">
				<button
					v-if="gallery.length"
					class="tool-btn"
					type="button"
					title="清空历史"
					aria-label="清空历史"
					@click="emit('clear-gallery')"
				>
					<Trash2 :size="15" />
				</button>

				<div class="zoom-pill">
					<button class="zoom-btn" type="button" title="缩小" aria-label="缩小" @click="emit('zoom-out')">
						<Minus :size="13" />
					</button>
					<span class="zoom-value">{{ zoomLevel }}%</span>
					<button class="zoom-btn" type="button" title="放大" aria-label="放大" @click="emit('zoom-in')">
						<Plus :size="13" />
					</button>
				</div>

				<button
					class="tool-btn"
					type="button"
					title="全屏预览"
					aria-label="全屏预览"
					:disabled="gallery.length === 0"
					@click="previewAll"
				>
					<Maximize2 :size="15" />
				</button>
			</div>
		</div>

		<!-- 网格展示区：zoom 走 CSS zoom 而不是 transform，放大后才会产生真实的滚动范围 -->
		<div class="grid-viewport">
			<div class="image-grid" :style="{ zoom: zoomLevel / 100 }">
				<div
					v-for="(img, idx) in gallery"
					:key="img.id || idx"
					class="image-card"
				>
					<img
						:src="img.url"
						:alt="img.prompt || '生成的图片'"
						class="card-img"
						@error="emit('remove-image', img.id)"
					/>

					<div class="card-caption">
						<span class="caption-text">{{ img.prompt }}</span>
						<span class="caption-time">{{ formatGeneratedAt(img.createdAt) }}</span>
					</div>

					<div class="card-overlay-actions">
						<button
							class="overlay-btn"
							type="button"
							title="放大查看"
							aria-label="放大查看"
							@click.stop="emit('preview-fullscreen', img)"
						>
							<Maximize2 :size="14" />
						</button>
						<button
							class="overlay-btn danger"
							type="button"
							title="删除图片"
							aria-label="删除图片"
							@click.stop="emit('remove-image', img.id)"
						>
							<Trash2 :size="14" />
						</button>
					</div>

					<div class="card-badge">{{ idx + 1 }}</div>
				</div>

				<!-- 空态：不塞样例图充数 -->
				<div v-if="gallery.length === 0" class="empty-card">
					<div class="empty-icon-shell">
						<Sparkles :size="22" :class="{ 'pulse-icon': isGenerating }" />
					</div>
					<div class="empty-title">{{ isGenerating ? '正在渲染…' : '还没有生成结果' }}</div>
					<div class="empty-desc">
						{{ isGenerating ? '完成后结果会出现在这里' : '在右侧填写提示词，点击开始生成' }}
					</div>
				</div>

				<!-- 添加图片 / 图生图参考卡片 -->
				<div
					class="upload-card"
					:class="{ dragging: isDragging, 'has-file': Boolean(referenceImage) }"
					role="button"
					tabindex="0"
					:aria-label="referenceImage ? '更换参考图' : '上传参考图'"
					@click="triggerFileInput"
					@keydown="onUploadKeydown"
					@dragover="onDragOver"
					@dragleave="onDragLeave"
					@drop.prevent="onDrop"
				>
					<input
						ref="fileInputRef"
						type="file"
						accept="image/png,image/jpeg,image/webp"
						class="hidden-file-input"
						@change="onFileSelected"
					/>

					<template v-if="referenceImage">
						<div class="ref-preview-wrapper" @click.stop>
							<img :src="referenceImage.url" alt="参考图" class="ref-thumb" />
							<button
								class="clear-ref-btn"
								type="button"
								title="清除参考图"
								aria-label="清除参考图"
								@click="emit('clear-reference')"
							>
								<X :size="14" />
							</button>
						</div>
						<div class="ref-info">
							<span class="ref-title">已载入参考图</span>
							<span class="ref-name">{{ referenceImage.name }}</span>
						</div>
					</template>

					<template v-else>
						<div class="upload-icon-circle">
							<PlusIcon :size="20" class="plus-icon" />
						</div>
						<div class="upload-title">添加图片</div>
						<div class="upload-desc">支持拖拽或点击上传</div>
						<div class="upload-formats">JPG / PNG / WEBP</div>
					</template>
				</div>
			</div>
		</div>
	</div>
</template>

<style scoped>
.preview-panel {
	display: flex;
	flex-direction: column;
	height: 100%;
	background: var(--surface-base, #ffffff);
	border: 1px solid var(--line-card, #e3e6ec);
	border-radius: 16px;
	box-shadow: 0 4px 20px -2px rgba(0, 0, 0, 0.04);
	overflow: hidden;
}

.panel-header {
	display: flex;
	align-items: center;
	justify-content: space-between;
	padding: 16px 20px;
	border-bottom: 1px solid var(--line-1, rgba(0, 0, 0, 0.06));
}

.title-group {
	display: flex;
	align-items: center;
	gap: 8px;
	color: var(--ink-1, #171a1f);
	font-size: 15px;
	font-weight: 600;
}

.header-icon {
	color: var(--accent-base, #3b5bfd);
}

.count-chip {
	font-size: 11px;
	font-weight: 600;
	padding: 1px 6px;
	border-radius: 9999px;
	background: var(--surface-muted, #f1f3f6);
	color: var(--ink-3, #6b7280);
	font-variant-numeric: tabular-nums;
}

.tools-group {
	display: flex;
	align-items: center;
	gap: 10px;
}

.tool-btn {
	display: flex;
	align-items: center;
	justify-content: center;
	width: 32px;
	height: 32px;
	border-radius: 8px;
	border: 1px solid var(--line-1, rgba(0, 0, 0, 0.08));
	background: transparent;
	color: var(--ink-3, #6b7280);
	cursor: pointer;
	transition: all 0.15s ease;
}

.tool-btn:hover:not(:disabled) {
	background: var(--surface-hover, #eef0f4);
	color: var(--ink-1, #171a1f);
}

.tool-btn:disabled {
	opacity: 0.45;
	cursor: not-allowed;
}

.zoom-pill {
	display: inline-flex;
	align-items: center;
	border: 1px solid var(--line-1, rgba(0, 0, 0, 0.08));
	border-radius: 9999px;
	padding: 2px 6px;
	background: var(--surface-base, #ffffff);
}

.zoom-btn {
	display: flex;
	align-items: center;
	justify-content: center;
	width: 20px;
	height: 20px;
	border: none;
	background: transparent;
	color: var(--ink-3, #6b7280);
	border-radius: 4px;
	cursor: pointer;
	transition: background 0.15s;
}

.zoom-btn:hover {
	background: var(--surface-hover, #eef0f4);
	color: var(--ink-1, #171a1f);
}

.zoom-value {
	font-size: 12px;
	font-weight: 500;
	color: var(--ink-2, #454c59);
	padding: 0 8px;
	min-width: 44px;
	text-align: center;
}

.grid-viewport {
	flex: 1;
	min-height: 0;
	overflow: auto;
	padding: 20px;
}

.image-grid {
	display: grid;
	grid-template-columns: repeat(2, minmax(0, 1fr));
	gap: 16px;
	width: 100%;
}

.image-card {
	position: relative;
	aspect-ratio: 1 / 1;
	border-radius: 14px;
	overflow: hidden;
	background: var(--surface-canvas, #f5f6f8);
	box-shadow: 0 2px 8px rgba(0, 0, 0, 0.04);
}

.card-img {
	width: 100%;
	height: 100%;
	object-fit: cover;
	display: block;
	transition: transform 0.3s ease;
}

.image-card:hover .card-img {
	transform: scale(1.03);
}

.card-caption {
	position: absolute;
	inset: auto 0 0 0;
	display: flex;
	flex-direction: column;
	gap: 2px;
	padding: 24px 10px 8px;
	background: linear-gradient(to top, rgba(15, 23, 42, 0.78), transparent);
	opacity: 0;
	transition: opacity 0.2s ease;
	pointer-events: none;
}

.image-card:hover .card-caption {
	opacity: 1;
}

.caption-text {
	font-size: 11px;
	line-height: 1.35;
	color: #f8fafc;
	display: -webkit-box;
	line-clamp: 2;
	-webkit-line-clamp: 2;
	-webkit-box-orient: vertical;
	overflow: hidden;
}

.caption-time {
	font-size: 10px;
	color: rgba(248, 250, 252, 0.72);
	font-variant-numeric: tabular-nums;
}

.card-overlay-actions {
	position: absolute;
	top: 10px;
	right: 10px;
	display: flex;
	align-items: center;
	gap: 6px;
	background: rgba(0, 0, 0, 0.55);
	backdrop-filter: blur(8px);
	padding: 4px 6px;
	border-radius: 8px;
	opacity: 0;
	transform: translateY(-4px);
	transition: all 0.2s ease;
}

.image-card:hover .card-overlay-actions {
	opacity: 1;
	transform: translateY(0);
}

.overlay-btn {
	display: flex;
	align-items: center;
	justify-content: center;
	width: 24px;
	height: 24px;
	border: none;
	background: transparent;
	color: #ffffff;
	border-radius: 4px;
	cursor: pointer;
	transition: background 0.15s;
}

.overlay-btn:hover {
	background: rgba(255, 255, 255, 0.2);
}

.overlay-btn.danger:hover {
	background: rgba(239, 68, 68, 0.8);
}

.card-badge {
	position: absolute;
	bottom: 10px;
	left: 10px;
	min-width: 22px;
	height: 22px;
	padding: 0 6px;
	display: flex;
	align-items: center;
	justify-content: center;
	background: rgba(15, 23, 42, 0.7);
	backdrop-filter: blur(4px);
	color: #ffffff;
	font-size: 11px;
	font-weight: 600;
	border-radius: 6px;
}

.empty-card {
	display: flex;
	flex-direction: column;
	align-items: center;
	justify-content: center;
	aspect-ratio: 1 / 1;
	border-radius: 14px;
	border: 1px solid var(--line-1, rgba(0, 0, 0, 0.06));
	background: var(--surface-soft, #f7f8fa);
	padding: 16px;
	text-align: center;
}

.empty-icon-shell {
	display: flex;
	align-items: center;
	justify-content: center;
	width: 48px;
	height: 48px;
	border-radius: 50%;
	background: var(--surface-base, #ffffff);
	border: 1px solid var(--line-1, rgba(0, 0, 0, 0.08));
	color: var(--accent-base, #3b5bfd);
	margin-bottom: 12px;
}

.pulse-icon {
	animation: pulseGlow 1.4s ease-in-out infinite;
}

@keyframes pulseGlow {
	0%, 100% {
		opacity: 0.45;
		transform: scale(0.94);
	}
	50% {
		opacity: 1;
		transform: scale(1.04);
	}
}

.empty-title {
	font-size: 13.5px;
	font-weight: 600;
	color: var(--ink-2, #454c59);
	margin-bottom: 4px;
}

.empty-desc {
	font-size: 11.5px;
	color: var(--ink-4, #8a929e);
}

.upload-card {
	display: flex;
	flex-direction: column;
	align-items: center;
	justify-content: center;
	aspect-ratio: 1 / 1;
	border-radius: 14px;
	border: 1.5px dashed var(--line-solid-strong, #d8dde5);
	background: var(--surface-soft, #f7f8fa);
	cursor: pointer;
	transition: all 0.2s ease;
	padding: 16px;
	text-align: center;
}

.upload-card:focus-visible {
	outline: 2px solid var(--accent-base, #3b5bfd);
	outline-offset: 2px;
}

.upload-card:hover,
.upload-card.dragging {
	border-color: var(--accent-base, #3b5bfd);
	background: color-mix(in srgb, var(--accent-base, #3b5bfd) 4%, var(--surface-soft, #f7f8fa));
}

.hidden-file-input {
	display: none;
}

.upload-icon-circle {
	display: flex;
	align-items: center;
	justify-content: center;
	width: 44px;
	height: 44px;
	border-radius: 50%;
	background: var(--surface-base, #ffffff);
	border: 1px solid var(--line-1, rgba(0, 0, 0, 0.08));
	color: var(--ink-2, #454c59);
	margin-bottom: 12px;
	transition: transform 0.2s;
}

.upload-card:hover .upload-icon-circle {
	transform: scale(1.08);
	color: var(--accent-base, #3b5bfd);
}

.upload-title {
	font-size: 14px;
	font-weight: 600;
	color: var(--ink-1, #171a1f);
	margin-bottom: 6px;
}

.upload-desc {
	font-size: 12px;
	color: var(--ink-3, #6b7280);
	margin-bottom: 4px;
}

.upload-formats {
	font-size: 11px;
	font-weight: 500;
	color: var(--ink-4, #8a929e);
	letter-spacing: 0.5px;
}

.ref-preview-wrapper {
	position: relative;
	width: 80px;
	height: 80px;
	border-radius: 10px;
	overflow: hidden;
	margin-bottom: 10px;
	border: 1px solid var(--line-card, #e3e6ec);
}

.ref-thumb {
	width: 100%;
	height: 100%;
	object-fit: cover;
}

.clear-ref-btn {
	position: absolute;
	top: 4px;
	right: 4px;
	width: 20px;
	height: 20px;
	border-radius: 50%;
	background: rgba(0, 0, 0, 0.65);
	color: #fff;
	border: none;
	display: flex;
	align-items: center;
	justify-content: center;
	cursor: pointer;
}

.ref-info {
	display: flex;
	flex-direction: column;
	gap: 2px;
}

.ref-title {
	font-size: 13px;
	font-weight: 600;
	color: var(--accent-base, #3b5bfd);
}

.ref-name {
	font-size: 11px;
	color: var(--ink-3, #6b7280);
	max-width: 140px;
	overflow: hidden;
	text-overflow: ellipsis;
	white-space: nowrap;
}
</style>
