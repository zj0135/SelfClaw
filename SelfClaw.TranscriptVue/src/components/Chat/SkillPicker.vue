<script setup>
import { onMounted, onUnmounted, ref } from 'vue';
import { BookOpenCheck, LoaderCircle } from 'lucide-vue-next';
import { isSuperseded, useHostBridge } from '../../composables/hostBridge.js';

const props = defineProps({
	agentId: { type: String, default: '' },
	agentName: { type: String, default: '' },
	capabilityRevision: { type: Number, default: 0 },
});
const emit = defineEmits(['select']);
const { requestLatest } = useHostBridge();
const cache = new Map();
const rootRef = ref(null);
const open = ref(false);
const loading = ref(false);
const error = ref('');
const skills = ref([]);

async function toggle() {
	open.value = !open.value;
	if (!open.value) return;
	await load();
}

async function load() {
	const cacheKey = `${props.agentId}:${props.capabilityRevision}`;
	if (cache.has(cacheKey)) {
		skills.value = cache.get(cacheKey);
		error.value = '';
		return;
	}

	loading.value = true;
	error.value = '';
	try {
		const response = await requestLatest(
			'effective-skills',
			'extensions/list-effective-skills',
			{ agentId: props.agentId || null },
		);
		const next = Array.isArray(response.skills) ? response.skills : [];
		cache.set(cacheKey, next);
		skills.value = next;
	} catch (cause) {
		if (!isSuperseded(cause)) error.value = cause?.message || '无法加载技能。';
	} finally {
		loading.value = false;
	}
}

function selectSkill(skill) {
	emit('select', skill.id);
	open.value = false;
}

function onDocumentPointerDown(event) {
	if (open.value && !rootRef.value?.contains(event.target)) open.value = false;
}

onMounted(() => document.addEventListener('pointerdown', onDocumentPointerDown));
onUnmounted(() => document.removeEventListener('pointerdown', onDocumentPointerDown));
</script>

<template>
	<div ref="rootRef" class="skill-picker">
		<button class="trigger" type="button" title="插入技能" aria-label="插入技能" :aria-expanded="open" @click="toggle">
			<BookOpenCheck :size="16" :stroke-width="1.8" aria-hidden="true" />
		</button>
		<transition name="picker-pop">
			<div v-if="open" class="popover">
				<header>
					<div>
						<strong>技能</strong>
						<span>{{ agentName || agentId }}</span>
					</div>
					<LoaderCircle v-if="loading" :size="14" class="spin" aria-hidden="true" />
				</header>
				<p v-if="error" class="message error">{{ error }}</p>
				<p v-else-if="!loading && skills.length === 0" class="message">当前 Agent 没有可用技能</p>
				<div v-else class="skill-list">
					<button v-for="skill in skills" :key="skill.id" type="button" @click="selectSkill(skill)">
						<span>{{ skill.name }}</span>
						<code>{{ skill.id }}</code>
						<small v-if="skill.description">{{ skill.description }}</small>
					</button>
				</div>
			</div>
		</transition>
	</div>
</template>

<style scoped>
.skill-picker {
	position: relative;
}

.trigger {
	display: inline-flex;
	align-items: center;
	justify-content: center;
	width: 32px;
	height: 32px;
	padding: 0;
	border: 0;
	border-radius: 8px;
	background: transparent;
	color: var(--muted);
}

.trigger:hover,
.trigger[aria-expanded='true'] {
	background: var(--panel-muted);
	color: var(--text);
}

.popover {
	position: absolute;
	z-index: 30;
	bottom: calc(100% + 9px);
	left: 0;
	width: min(310px, calc(100vw - 32px));
	max-height: 320px;
	overflow: hidden;
	border: 1px solid var(--border);
	border-radius: 7px;
	background: var(--panel);
	box-shadow: 0 16px 42px rgba(var(--shadow-ink), .16);
}

header {
	display: flex;
	align-items: center;
	justify-content: space-between;
	gap: 12px;
	min-height: 48px;
	padding: 9px 12px;
	border-bottom: 1px solid var(--border);
}

header div {
	min-width: 0;
}

header strong,
header span {
	display: block;
	overflow: hidden;
	text-overflow: ellipsis;
	white-space: nowrap;
}

header strong {
	color: var(--text-strong);
	font-size: var(--fs-12);
}

header span {
	margin-top: 2px;
	color: var(--muted-soft);
	font-size: var(--fs-10);
}

.skill-list {
	max-height: 260px;
	overflow-y: auto;
	padding: 5px;
}

.skill-list button {
	display: grid;
	width: 100%;
	grid-template-columns: minmax(0, 1fr) auto;
	gap: 2px 10px;
	padding: 9px 10px;
	border: 0;
	border-radius: 5px;
	background: transparent;
	text-align: left;
}

.skill-list button:hover {
	background: var(--bg-canvas);
}

.skill-list span {
	overflow: hidden;
	color: var(--text-strong);
	font-size: var(--fs-12);
	font-weight: 600;
	text-overflow: ellipsis;
	white-space: nowrap;
}

.skill-list code {
	color: var(--muted);
	font-family: var(--font-mono);
	font-size: var(--fs-9);
}

.skill-list small {
	grid-column: 1 / -1;
	overflow: hidden;
	color: var(--muted);
	font-size: var(--fs-10);
	line-height: 1.4;
	text-overflow: ellipsis;
	white-space: nowrap;
}

.message {
	margin: 0;
	padding: 20px 12px;
	color: var(--muted-soft);
	font-size: var(--fs-11);
	text-align: center;
}

.message.error {
	color: var(--err-text);
}

.spin {
	animation: spin .8s linear infinite;
}

.picker-pop-enter-active,
.picker-pop-leave-active {
	transition: opacity .14s ease, transform .14s ease;
}

.picker-pop-enter-from,
.picker-pop-leave-to {
	opacity: 0;
	transform: translateY(4px);
}

@keyframes spin {
	to {
		transform: rotate(360deg);
	}
}
</style>
