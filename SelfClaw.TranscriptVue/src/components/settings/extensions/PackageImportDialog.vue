<script setup>
import { CheckCircle2, FileArchive, Hash, X } from 'lucide-vue-next';

defineProps({
	open: { type: Boolean, default: false },
	result: { type: Object, default: null },
});
defineEmits(['close']);
</script>

<template>
	<div v-if="open && result" class="backdrop" @mousedown.self="$emit('close')">
		<section class="dialog" role="dialog" aria-modal="true" aria-labelledby="package-import-title">
			<header>
				<div class="success">
					<CheckCircle2 :size="18" aria-hidden="true" />
				</div>
				<div>
					<span>IMPORT COMPLETE</span>
					<h2 id="package-import-title">{{ result.package?.name || '导入完成' }}</h2>
				</div>
				<button type="button" class="icon-button" title="关闭" @click="$emit('close')">
					<X :size="16" aria-hidden="true" />
				</button>
			</header>

			<div class="manifest">
				<div><span>ID</span><code>{{ result.package?.id }}</code></div>
				<div><span>版本</span><strong>{{ result.package?.version }}</strong></div>
				<div><span>文件</span><strong>{{ result.summary?.fileCount ?? 0 }}</strong></div>
			</div>

			<p v-if="result.package?.description" class="description">{{ result.package.description }}</p>
			<div v-if="result.package?.permissions?.length" class="permissions">
				<span>声明权限</span>
				<code v-for="permission in result.package.permissions" :key="permission">{{ permission }}</code>
			</div>
			<div v-if="result.summary?.manifest?.triggers?.length" class="triggers">
				<span v-for="trigger in result.summary.manifest.triggers" :key="trigger">{{ trigger }}</span>
			</div>
			<div class="hash-row">
				<Hash :size="14" aria-hidden="true" />
				<code>{{ result.summary?.contentHash }}</code>
			</div>

			<footer>
				<div>
					<FileArchive :size="14" aria-hidden="true" />默认保持停用
				</div>
				<button type="button" class="primary" @click="$emit('close')">完成</button>
			</footer>
		</section>
	</div>
</template>

<style scoped>
@import '../../../styles/settings-console.css';

.backdrop {
	position: fixed;
	inset: 0;
	z-index: 50;
	display: grid;
	place-items: center;
	padding: 20px;
	background: var(--overlay-heavy);
}

.dialog {
	width: min(480px, 100%);
	border: 1px solid var(--sc-line-2);
	border-radius: 7px;
	background: var(--sc-panel);
	box-shadow: 0 24px 70px var(--overlay-shadow);
	color: var(--sc-text);
}

header {
	display: grid;
	grid-template-columns: 34px minmax(0, 1fr) 32px;
	align-items: center;
	gap: 10px;
	padding: 18px 20px;
	border-bottom: 1px solid var(--sc-line);
}

.success {
	display: grid;
	width: 30px;
	height: 30px;
	place-items: center;
	border-radius: 50%;
	background: color-mix(in srgb, var(--success) 12%, transparent);
	color: var(--sc-ok);
}

header span {
	color: var(--sc-faint);
	font-family: var(--sc-mono);
	font-size: var(--fs-9);
}

h2 {
	margin: 3px 0 0;
	overflow-wrap: anywhere;
	font-size: var(--fs-15);
	font-weight: 650;
	letter-spacing: 0;
}

button {
	display: inline-flex;
	align-items: center;
	justify-content: center;
	height: 34px;
	border: 1px solid var(--sc-line-2);
	border-radius: 5px;
	background: var(--sc-raise);
	color: var(--sc-soft);
}

.icon-button {
	width: 32px;
	height: 32px;
	padding: 0;
}

.manifest {
	display: grid;
	grid-template-columns: minmax(0, 1.4fr) .7fr .55fr;
	padding: 16px 20px;
	border-bottom: 1px solid var(--sc-line);
}

.manifest div {
	min-width: 0;
	padding-right: 12px;
}

.manifest div+div {
	padding-left: 12px;
	border-left: 1px solid var(--sc-line);
}

.manifest span {
	display: block;
	margin-bottom: 5px;
	color: var(--sc-faint);
	font-size: var(--fs-9);
}

.manifest code,
.manifest strong {
	display: block;
	overflow: hidden;
	color: var(--sc-text);
	font-family: var(--sc-mono);
	font-size: var(--fs-11);
	font-weight: 550;
	text-overflow: ellipsis;
	white-space: nowrap;
}

.description {
	margin: 16px 20px 0;
	color: var(--sc-mute);
	font-size: var(--fs-12);
	line-height: 1.6;
}

.permissions {
	display: flex;
	flex-wrap: wrap;
	align-items: center;
	gap: 6px;
	margin: 14px 20px 0;
}

.permissions span {
	margin-right: 3px;
	color: var(--sc-faint);
	font-size: var(--fs-9);
}

.permissions code {
	padding: 3px 6px;
	border: 1px solid var(--caution-line);
	border-radius: 4px;
	color: var(--caution-icon);
	font-family: var(--sc-mono);
	font-size: var(--fs-9);
}

.triggers {
	display: flex;
	flex-wrap: wrap;
	gap: 5px;
	margin: 12px 20px 0;
}

.triggers span {
	padding: 3px 6px;
	border: 1px solid var(--sc-line-2);
	border-radius: 4px;
	color: var(--sc-soft);
	font-family: var(--sc-mono);
	font-size: var(--fs-9);
}

.hash-row {
	display: grid;
	grid-template-columns: 16px minmax(0, 1fr);
	align-items: start;
	gap: 7px;
	margin: 16px 20px;
	padding: 9px 10px;
	background: var(--sc-bg);
	color: var(--sc-faint);
}

.hash-row code {
	overflow-wrap: anywhere;
	color: var(--sc-mute);
	font-family: var(--sc-mono);
	font-size: var(--fs-9);
	line-height: 1.5;
}

footer {
	display: flex;
	align-items: center;
	justify-content: space-between;
	gap: 16px;
	padding: 14px 20px;
	border-top: 1px solid var(--sc-line);
}

footer div {
	display: flex;
	align-items: center;
	gap: 7px;
	color: var(--sc-faint);
	font-size: var(--fs-10);
}

.primary {
	min-width: 72px;
	padding: 0 14px;
	border-color: var(--sc-acid);
	background: var(--sc-acid);
	color: var(--sc-acid-ink);
}

@media (max-width: 520px) {
	.manifest {
		grid-template-columns: 1fr 1fr;
		row-gap: 12px;
	}

	.manifest div:nth-child(3) {
		padding: 12px 0 0;
		border: 0;
	}
}
</style>
