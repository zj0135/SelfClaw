<script setup>
import { computed, nextTick, onMounted, onUnmounted, ref } from 'vue';
import { Folder, Monitor, GitBranch, ChevronDown, ChevronRight, Check, Search } from 'lucide-vue-next';

const props = defineProps({
	workspaceSelection: {
		type: Object,
		default: () => ({}),
	},
	// 后端暂未提供 git 分支，先展示占位值；接入后由 workspace-selection 载荷携带。
	branch: {
		type: String,
		default: 'main',
	},
});

const emit = defineEmits(['refresh', 'select-root', 'browse']);

const isOpen = ref(false);
const searchText = ref('');
const statusbarRef = ref(null);
const dropdownRef = ref(null);
const searchInputRef = ref(null);

const current = computed(() => props.workspaceSelection?.current || null);
const folderName = computed(() => current.value?.name || '未选择工作目录');
const folderPath = computed(() => current.value?.path || '');
const roots = computed(() => Array.isArray(props.workspaceSelection?.roots) ? props.workspaceSelection.roots : []);

const filteredRoots = computed(() => {
	const keyword = searchText.value.trim().toLowerCase();
	if (!keyword) {
		return roots.value;
	}

	return roots.value.filter((root) =>
		(root.name || '').toLowerCase().includes(keyword) ||
		(root.path || '').toLowerCase().includes(keyword));
});

const emptyText = computed(() => (roots.value.length === 0 ? '暂无已保存项目' : '无匹配项目'));

function isSelectedRoot(root) {
	return Boolean(root?.id) && root.id === current.value?.id;
}

async function toggleOpen() {
	isOpen.value = !isOpen.value;
	if (!isOpen.value) {
		return;
	}

	searchText.value = '';
	emit('refresh', false);
	await nextTick();
	searchInputRef.value?.focus();
}

function close() {
	isOpen.value = false;
}

function selectRoot(root) {
	if (!root?.id) {
		return;
	}

	emit('select-root', root.id);
	close();
}

function browse() {
	emit('browse');
	close();
}

function onDocumentPointerDown(event) {
	if (!isOpen.value) {
		return;
	}

	if (statusbarRef.value?.contains(event.target) || dropdownRef.value?.contains(event.target)) {
		return;
	}

	close();
}

function onKeydown(event) {
	if (event.key === 'Escape') {
		close();
	}
}

onMounted(() => {
	document.addEventListener('pointerdown', onDocumentPointerDown);
});

onUnmounted(() => {
	document.removeEventListener('pointerdown', onDocumentPointerDown);
});
</script>

<template>
	<div ref="statusbarRef" class="composer-statusbar" aria-label="工作区上下文" @keydown="onKeydown">
		<button
			class="status-item status-folder"
			:class="{ active: isOpen }"
			type="button"
			:title="folderPath || folderName"
			aria-label="选择项目文件夹"
			:aria-expanded="isOpen ? 'true' : 'false'"
			@click.stop="toggleOpen"
		>
			<Folder :size="13" :stroke-width="1.8" aria-hidden="true" />
			<span class="status-text">{{ folderName }}</span>
			<ChevronDown class="status-chevron" :class="{ open: isOpen }" :size="12" :stroke-width="2" aria-hidden="true" />
		</button>
		<span class="status-item">
			<Monitor :size="13" :stroke-width="1.8" aria-hidden="true" />
			<span class="status-text">本地模式</span>
		</span>
		<span class="status-item" :title="branch">
			<GitBranch :size="13" :stroke-width="1.8" aria-hidden="true" />
			<span class="status-text">{{ branch }}</span>
		</span>
	</div>

	<!-- 下拉面板不能放进 .composer-statusbar（会被它的 clip-path 裁掉），
	     作为组件第二个根节点渲染，相对 composer-stack 绝对定位。 -->
	<div v-if="isOpen" ref="dropdownRef" class="project-card" role="dialog" aria-label="选择项目" @keydown="onKeydown">
		<label class="project-search">
			<Search :size="13" :stroke-width="2" aria-hidden="true" />
			<input ref="searchInputRef" v-model="searchText" type="text" placeholder="搜索项目" />
		</label>
		<div class="project-list">
			<button
				v-for="root in filteredRoots"
				:key="root.id"
				class="project-row"
				:class="{ selected: isSelectedRoot(root) }"
				type="button"
				:title="root.path"
				@click="selectRoot(root)"
			>
				<Folder :size="14" :stroke-width="1.7" aria-hidden="true" />
				<span class="project-name">{{ root.name }}</span>
				<Check v-if="isSelectedRoot(root)" class="project-check" :size="14" :stroke-width="2.2" aria-hidden="true" />
			</button>
			<p v-if="filteredRoots.length === 0" class="project-empty">{{ emptyText }}</p>
		</div>
		<div class="project-divider" role="separator"></div>
		<button class="project-row project-add" type="button" @click="browse">
			<Folder :size="14" :stroke-width="1.7" aria-hidden="true" />
			<span class="project-name">添加新项目</span>
			<ChevronRight :size="14" :stroke-width="2" aria-hidden="true" />
		</button>
	</div>
</template>

<style scoped>
.composer-statusbar {
	/* positioned：盖在 composer-shell 投影之上，使两者阴影/描边衔接成一体 */
	position: relative;
	display: flex;
	align-items: center;
	gap: 18px;
	height: 36px;
	/* 负 margin 重叠并遮住输入框底边线，消除分界线 */
	margin-top: -1px;
	padding: 0 14px;
	border: 1px solid rgba(19, 27, 45, 0.1);
	/* 与输入框之间的分隔线：比外框略深，白底下唯一的分界 */
	border-top-color: rgba(19, 27, 45, 0.14);
	border-radius: 0 0 16px 16px;
	background: #ffffff;
	box-shadow:
		0 1px 2px rgba(23, 26, 31, 0.05),
		0 8px 24px rgba(23, 26, 31, 0.04);
	/* 裁掉向上蔓延的阴影（y偏移-模糊半径会伸进输入框底部），只留左/右/下三侧 */
	clip-path: inset(0 -48px -48px -48px);
	transition: border-color 0.18s, box-shadow 0.18s;
}

/* focus 高亮与输入框一致：border 与 shadow 同步。
   整条选择器包在 :global 中（:global 只接受完整选择器，混写会被截断），
   不依赖任何 scope id 继承，匹配稳定。
   clip-path 已裁掉顶部阴影，与输入框的分隔线上不会有 shadow。 */
:global(.composer-shell:focus-within + .composer-statusbar) {
	border-color: rgba(59, 91, 253, 0.45);
	border-top-color: rgba(19, 27, 45, 0.14);
	box-shadow:
		0 1px 2px rgba(23, 26, 31, 0.05),
		0 12px 32px rgba(23, 26, 31, 0.07),
		0 0 0 3px rgba(59, 91, 253, 0.1);
}

.status-item {
	display: inline-flex;
	align-items: center;
	gap: 6px;
	min-width: 0;
	color: #9aa2ad;
}

.status-text {
	min-width: 0;
	overflow: hidden;
	color: #5f6876;
	font-size: 12px;
	font-weight: 500;
	/* line-height: 1 会让小写字母降部（p/y/g）超出内容盒被 overflow 裁掉 */
	line-height: 1.4;
	text-overflow: ellipsis;
	white-space: nowrap;
}

.status-item:first-child .status-text {
	max-width: 220px;
}

/* ===== 文件夹触发按钮（打开项目下拉） ===== */
button.status-folder {
	margin: 0 0 0 -6px;
	padding: 4px 6px;
	border: 0;
	border-radius: 6px;
	background: transparent;
	font: inherit;
	cursor: pointer;
	transition: background 0.15s;
}

button.status-folder:hover,
button.status-folder.active {
	background: rgba(19, 27, 45, 0.06);
}

.status-chevron {
	transition: transform 0.15s;
}

.status-chevron.open {
	transform: rotate(180deg);
}

/* ===== 项目选择下拉（向上展开，锚在状态栏上方） ===== */
.project-card {
	position: absolute;
	left: 0;
	/* 状态栏高 36px，向上留 8px 间距 */
	bottom: 44px;
	z-index: 80;
	width: 288px;
	padding: 10px;
	border: 1px solid #dfe3ea;
	border-radius: 12px;
	background: #ffffff;
	box-shadow:
		0 18px 50px rgba(23, 26, 31, 0.12),
		0 3px 10px rgba(23, 26, 31, 0.05);
}

.project-search {
	display: flex;
	align-items: center;
	gap: 7px;
	height: 34px;
	padding: 0 10px;
	border: 1px solid #e3e6ec;
	border-radius: 8px;
	background: #ffffff;
	color: #9aa2ad;
	transition: border-color 0.15s;
}

.project-search:focus-within {
	border-color: rgba(59, 91, 253, 0.45);
}

.project-search input {
	flex: 1;
	min-width: 0;
	border: 0;
	outline: none;
	background: transparent;
	color: #20242a;
	font: 12.5px/1 var(--font-ui);
}

.project-search input::placeholder {
	color: #9aa2ad;
}

.project-list {
	display: grid;
	gap: 2px;
	max-height: 220px;
	margin-top: 8px;
	overflow: auto;
}

.project-row {
	display: flex;
	align-items: center;
	gap: 8px;
	width: 100%;
	height: 34px;
	padding: 0 8px;
	border: 0;
	border-radius: 7px;
	background: transparent;
	color: #394150;
	font: 12.5px/1 var(--font-ui);
	text-align: left;
	cursor: pointer;
	transition: background 0.12s;
}

.project-row svg {
	flex: none;
	color: #9aa2ad;
}

.project-row:hover,
.project-row.selected {
	background: #f3f4f6;
}

.project-name {
	flex: 1;
	min-width: 0;
	overflow: hidden;
	font-weight: 550;
	text-overflow: ellipsis;
	white-space: nowrap;
}

.project-row .project-check {
	color: #171a1f;
}

.project-empty {
	margin: 4px 8px 6px;
	color: #9aa2ad;
	font-size: 12px;
	line-height: 1.5;
}

.project-divider {
	height: 1px;
	margin: 8px 2px;
	background: #eceff3;
}
</style>
