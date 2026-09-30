import {
	Activity, BookOpen, Bookmark, Bug, Calendar, Clipboard, Code, Database, Eye, FileCode,
	FileText, Filter, Folder, FolderOpen, GitBranch, Globe, Image, Info, Key, Layers, LayoutGrid,
	Lightbulb, Link, List, Map, MessageSquare, Package, Play, Puzzle, Search, Settings, Shield,
	Sparkles, Star, Table, Tag, Terminal, Timer, Wrench, Zap,
} from 'lucide-vue-next';

// 插件视图的图标只认名字，不接受包里的 SVG——标签栏与启动器渲染在应用源里，包内容进来就是
// 注入面。这份映射必须与后端 PluginViewIcons 的白名单一致；不一致时后端会拒绝安装，
// 而这里未知的名字退回 puzzle，所以漂移的表现是「图标不对」而不是「装不上」。
export const pluginIconMap = {
	activity: Activity, 'book-open': BookOpen, bookmark: Bookmark, bug: Bug, calendar: Calendar,
	clipboard: Clipboard, code: Code, database: Database, eye: Eye, 'file-code': FileCode,
	'file-text': FileText, filter: Filter, folder: Folder, 'folder-open': FolderOpen,
	'git-branch': GitBranch, globe: Globe, image: Image, info: Info, key: Key, layers: Layers,
	'layout-grid': LayoutGrid, lightbulb: Lightbulb, link: Link, list: List, map: Map,
	'message-square': MessageSquare, package: Package, play: Play, puzzle: Puzzle, search: Search,
	settings: Settings, shield: Shield, sparkles: Sparkles, star: Star, table: Table, tag: Tag,
	terminal: Terminal, timer: Timer, wrench: Wrench, zap: Zap,
};

export function resolvePluginIcon(name) {
	return pluginIconMap[name] || Puzzle;
}
