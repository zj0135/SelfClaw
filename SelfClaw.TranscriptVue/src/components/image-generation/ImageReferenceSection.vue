<script setup>
import { ref } from 'vue';
import { ImagePlus, X } from 'lucide-vue-next';

const props = defineProps({
	referenceImage: { type: Object, default: null },
});

const emit = defineEmits(['upload-reference', 'clear-reference']);

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

function onKeydown(event) {
	if (event.key === 'Enter' || event.key === ' ') {
		event.preventDefault();
		triggerFileInput();
	}
}

function handleClear(event) {
	event.stopPropagation();
	emit('clear-reference');
}
</script>

<template>
	<div class="reference-section">
		<div class="field-header">
			<span class="field-label">参考图片</span>
			<span class="field-optional">/ 可选</span>
		</div>

		<div
			class="dropzone-box"
			:class="{ dragging: isDragging, 'has-file': Boolean(referenceImage) }"
			role="button"
			tabindex="0"
			:aria-label="referenceImage ? '更换参考图' : '添加参考图'"
			@click="triggerFileInput"
			@keydown="onKeydown"
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
				<div class="ref-content-wrapper">
					<div class="thumb-shell">
						<img :src="referenceImage.url" :alt="referenceImage.name || '参考图'" class="thumb-img" />
					</div>
					<div class="ref-meta">
						<span class="ref-status">已载入参考图</span>
						<span class="ref-filename" :title="referenceImage.name">{{ referenceImage.name }}</span>
						<span class="ref-hint">点击更换图片</span>
					</div>
					<button
						class="clear-btn"
						type="button"
						title="清除参考图"
						aria-label="清除参考图"
						@click="handleClear"
					>
						<X :size="15" />
					</button>
				</div>
			</template>

			<template v-else>
				<div class="empty-content">
					<ImagePlus :size="20" class="upload-icon" />
					<span class="upload-label">添加参考图</span>
				</div>
			</template>
		</div>
	</div>
</template>

<style scoped>
.reference-section {
	display: flex;
	flex-direction: column;
	gap: 6px;
	width: 100%;
}

.field-header {
	display: flex;
	align-items: baseline;
	gap: 4px;
}

.field-label {
	font-size: 13px;
	font-weight: 600;
	color: var(--ink-1, #171a1f);
}

.field-optional {
	font-size: 12px;
	color: var(--ink-4, #8a929e);
}

.dropzone-box {
	display: flex;
	align-items: center;
	justify-content: center;
	border: 1.5px dashed var(--line-solid-strong, #d8dde5);
	border-radius: 12px;
	background: var(--surface-soft, #f7f8fa);
	padding: 14px 16px;
	cursor: pointer;
	outline: none;
	transition: all 0.18s ease;
	user-select: none;
	min-height: 64px;
	box-sizing: border-box;
}

.dropzone-box:focus-visible {
	border-color: var(--accent-base, #3b5bfd);
	box-shadow: 0 0 0 3px color-mix(in srgb, var(--accent-base, #3b5bfd) 15%, transparent);
}

.dropzone-box:hover,
.dropzone-box.dragging {
	border-color: var(--accent-base, #3b5bfd);
	background: color-mix(in srgb, var(--accent-base, #3b5bfd) 4%, var(--surface-soft, #f7f8fa));
}

.dropzone-box.has-file {
	padding: 8px 12px;
	background: var(--surface-base, #ffffff);
	border-style: solid;
	border-color: var(--line-card, #e3e6ec);
}

.dropzone-box.has-file:hover {
	border-color: var(--accent-base, #3b5bfd);
}

.hidden-file-input {
	display: none;
}

.empty-content {
	display: flex;
	flex-direction: column;
	align-items: center;
	justify-content: center;
	gap: 6px;
	color: var(--ink-3, #6b7280);
	transition: color 0.15s ease;
}

.dropzone-box:hover .empty-content,
.dropzone-box.dragging .empty-content {
	color: var(--accent-base, #3b5bfd);
}

.upload-icon {
	color: currentColor;
	flex-shrink: 0;
	transition: transform 0.18s ease;
}

.dropzone-box:hover .upload-icon {
	transform: scale(1.08);
}

.upload-label {
	font-size: 13px;
	font-weight: 500;
	letter-spacing: 0.1px;
}

.ref-content-wrapper {
	display: flex;
	align-items: center;
	gap: 12px;
	width: 100%;
}

.thumb-shell {
	width: 48px;
	height: 48px;
	border-radius: 8px;
	overflow: hidden;
	background: var(--surface-canvas, #f5f6f8);
	border: 1px solid var(--line-card, #e3e6ec);
	flex-shrink: 0;
}

.thumb-img {
	width: 100%;
	height: 100%;
	object-fit: cover;
	display: block;
}

.ref-meta {
	display: flex;
	flex-direction: column;
	gap: 2px;
	flex: 1;
	min-width: 0;
}

.ref-status {
	font-size: 12px;
	font-weight: 600;
	color: var(--accent-base, #3b5bfd);
}

.ref-filename {
	font-size: 12px;
	color: var(--ink-2, #454c59);
	white-space: nowrap;
	overflow: hidden;
	text-overflow: ellipsis;
}

.ref-hint {
	font-size: 10.5px;
	color: var(--ink-4, #8a929e);
}

.clear-btn {
	display: flex;
	align-items: center;
	justify-content: center;
	width: 28px;
	height: 28px;
	border: none;
	border-radius: 6px;
	background: transparent;
	color: var(--ink-3, #6b7280);
	cursor: pointer;
	flex-shrink: 0;
	transition: all 0.15s ease;
}

.clear-btn:hover {
	background: #fee2e2;
	color: #dc2626;
}
</style>
