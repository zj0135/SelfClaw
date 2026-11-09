// 生图工作台的纯渲染数据：标签词表、尺寸/画质/画风的展示文案。
// 尺寸与画质不是前端写死的清单，而是模型能力的投影——宿主按模型回传，
// 这里只负责把 "1024x1536" 变成可读的 "2:3 · 1024 × 1536"。

export const COMMON_TAGS = [
	'写实', '插画', '二次元', '摄影', '科幻',
	'产品设计', '风景', '人物', '建筑',
];

export const EXTENDED_TAGS = [
	'赛博朋克', '水彩画', '国风水墨', '油画风格', '3D渲染', '极简主义', '电影质感', '像素艺术',
];

export const QUALITY_LABELS = {
	auto: '自动',
	low: '低',
	medium: '中',
	high: '高',
	standard: '标准',
	hd: '高清',
};

export const STYLE_LABELS = {
	vivid: '生动',
	natural: '自然',
};

const ORIENTATIONS = {
	'1:1': 'square',
	landscape: 'landscape',
	portrait: 'portrait',
};

function greatestCommonDivisor(a, b) {
	let x = a;
	let y = b;
	while (y) {
		[x, y] = [y, x % y];
	}
	return x || 1;
}

// "1024x1536" → { value, ratio: '2:3', orientation: 'portrait', desc: '1024 × 1536' }
export function describeSize(value) {
	const match = /^(\d+)x(\d+)$/.exec(String(value ?? '').trim().toLowerCase());
	if (!match) {
		return { value, ratio: '', orientation: 'square', desc: String(value ?? '') };
	}

	const width = Number(match[1]);
	const height = Number(match[2]);
	const divisor = greatestCommonDivisor(width, height);
	return {
		value,
		ratio: `${width / divisor}:${height / divisor}`,
		orientation: width === height ? 'square' : width > height ? 'landscape' : 'portrait',
		desc: `${width} × ${height}`,
	};
}

export function describeSizes(sizes) {
	return Array.isArray(sizes) ? sizes.map(describeSize) : [];
}

export function sizeOrientation(ratio) {
	return ORIENTATIONS[ratio] || (ratio && ratio.includes(':') ? 'landscape' : 'square');
}

// 历史结果可能指向已被保留策略清掉的文件，展示时按 ISO 时间给个稳定的人读标签。
export function formatGeneratedAt(iso) {
	const date = new Date(iso);
	if (Number.isNaN(date.getTime())) {
		return '';
	}

	const pad = (value) => String(value).padStart(2, '0');
	const time = `${pad(date.getHours())}:${pad(date.getMinutes())}`;
	const today = new Date();
	return date.toDateString() === today.toDateString()
		? `今天 ${time}`
		: `${pad(date.getMonth() + 1)}-${pad(date.getDate())} ${time}`;
}
