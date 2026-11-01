// 审查面板的文案与路径拆分：改动种类 → 字母徽标 / 中文标签 / 配色语气（tone 对应 CSS 的 tone-* 类）。
const KIND_LABELS = {
	modified: { letter: 'M', label: '修改', tone: 'modified' },
	added: { letter: 'A', label: '新增', tone: 'added' },
	deleted: { letter: 'D', label: '删除', tone: 'deleted' },
	renamed: { letter: 'R', label: '重命名', tone: 'renamed' },
	copied: { letter: 'C', label: '复制', tone: 'renamed' },
	untracked: { letter: 'U', label: '未跟踪', tone: 'added' },
	conflicted: { letter: '!', label: '冲突', tone: 'deleted' },
};

export function changeKindLabel(kind) {
	return KIND_LABELS[kind] ?? { letter: '?', label: kind || '未知', tone: 'modified' };
}

export function splitPath(path) {
	const value = String(path || '');
	const index = value.lastIndexOf('/');
	return index < 0 ? { directory: '', name: value } : { directory: value.slice(0, index), name: value.slice(index + 1) };
}

export function scopeLabel(scope) {
	return scope === 'staged' ? '已暂存' : '未暂存';
}
