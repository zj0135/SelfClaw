<script setup>
import { computed } from 'vue';
import {
	Activity, BookOpen, Bookmark, Bug, Calendar, Clipboard, Code, Database, Eye, FileCode,
	FileText, Filter, Folder, FolderOpen, GitBranch, Globe, Image, Info, Key, Layers, LayoutGrid,
	Lightbulb, Link, List, Map, MessageSquare, Package, Play, Puzzle, Search, Settings, Shield,
	Sparkles, Star, Table, Tag, Terminal, Timer, Wrench, Zap,
} from 'lucide-vue-next';

// 图标只认名字，不接受插件包里的 SVG——tab 栏渲染在应用源里，包内容进来就是注入面。
// 这份映射必须与后端 PluginPanelIcons 的白名单保持一致。
const iconMap = {
	activity: Activity, 'book-open': BookOpen, bookmark: Bookmark, bug: Bug, calendar: Calendar,
	clipboard: Clipboard, code: Code, database: Database, eye: Eye, 'file-code': FileCode,
	'file-text': FileText, filter: Filter, folder: Folder, 'folder-open': FolderOpen,
	'git-branch': GitBranch, globe: Globe, image: Image, info: Info, key: Key, layers: Layers,
	'layout-grid': LayoutGrid, lightbulb: Lightbulb, link: Link, list: List, map: Map,
	'message-square': MessageSquare, package: Package, play: Play, puzzle: Puzzle, search: Search,
	settings: Settings, shield: Shield, sparkles: Sparkles, star: Star, table: Table, tag: Tag,
	terminal: Terminal, timer: Timer, wrench: Wrench, zap: Zap,
};

const props = defineProps({
	tab: { type: Object, required: true },
	active: { type: Boolean, default: false },
});

defineEmits(['activate', 'close']);

const icon = computed(() => iconMap[props.tab.panel.icon] || Puzzle);
</script>

<template>
	<div class="tab" :class="{ active }" role="tab" :aria-selected="active">
		<button class="tab-main" type="button" :title="tab.panel.title" @click="$emit('activate', tab.key)">
			<span class="tab-icon" aria-hidden="true">
				<component :is="icon" :size="13" :stroke-width="1.8" />
			</span>
			<span class="tab-title">{{ tab.panel.title }}</span>
		</button>
		<button class="tab-close" type="button" :aria-label="`关闭 ${tab.panel.title}`"
			@click.stop="$emit('close', tab.key)">
			<svg viewBox="0 0 12 12" width="11" height="11" aria-hidden="true">
				<path d="M3 3l6 6M9 3l-6 6" stroke="currentColor" stroke-width="1.4" stroke-linecap="round"
					fill="none" />
			</svg>
		</button>
	</div>
</template>

<style scoped>
.tab {
	position: relative;
	display: inline-flex;
	align-items: center;
	max-width: 168px;
	min-width: 0;
	height: 24px;
	flex: 0 1 auto;
	padding-right: 3px;
	border-radius: 999px;
	color: var(--muted);
	transition: background 0.14s, color 0.14s;
}

.tab:hover {
	background: var(--panel-muted);
	color: var(--text);
}

.tab.active {
	background: var(--panel-hover);
	color: var(--text);
}

.tab-main {
	display: inline-flex;
	align-items: center;
	min-width: 0;
	gap: 6px;
	flex: 1 1 auto;
	height: 100%;
	padding: 0 4px 0 9px;
	border: 0;
	background: transparent;
	color: inherit;
	font-size: var(--fs-12);
	font-weight: 560;
}

.tab-icon {
	display: inline-grid;
	flex: none;
	place-items: center;
	color: var(--muted-soft);
}

.tab.active .tab-icon {
	color: var(--accent);
}

.tab-title {
	min-width: 0;
	overflow: hidden;
	text-overflow: ellipsis;
	white-space: nowrap;
}

.tab-close {
	display: grid;
	width: 19px;
	height: 19px;
	flex: none;
	place-items: center;
	border: 0;
	border-radius: 5px;
	background: transparent;
	color: var(--faint);
	opacity: 0;
	transition: background 0.12s, color 0.12s, opacity 0.12s;
}

.tab:hover .tab-close,
.tab.active .tab-close {
	opacity: 1;
}

.tab-close:hover {
	background: var(--border);
	color: var(--text);
}
</style>
