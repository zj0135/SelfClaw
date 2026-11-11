// 生图工作台的纯渲染数据：尺寸/画质/画风等展示文案与校验规则。
// 参数取值不是前端写死的清单，而是模型能力的投影——宿主按模型回传，
// 这里只负责把 "1024x1536" 变成可读的 "2:3 · 1024 × 1536"。

export const QUALITY_LABELS = {
	auto: '自动',
	low: '低',
	medium: '中',
	high: '高',
	standard: '标准',
	hd: '高清',
	xhigh: '超高',
	max: '极致',
};

export const STYLE_LABELS = {
	vivid: '生动',
	natural: '自然',
};

export const BACKGROUND_LABELS = {
	auto: '自动',
	transparent: '透明',
	opaque: '不透明',
};

export const OUTPUT_FORMAT_LABELS = {
	png: 'PNG',
	jpeg: 'JPEG',
	webp: 'WebP',
};

export const MODERATION_LABELS = {
	auto: '自动',
	low: '宽松',
};

// 兜底上限：能力数据尚未到达或网关未声明 prompt 上限时使用，与后端
// ImageGenerationModelCapabilities.DefaultMaxPromptLength 保持一致。
export const DEFAULT_MAX_PROMPT_LENGTH = 1000;

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
	if (String(value ?? '').trim().toLowerCase() === 'auto') {
		return { value, ratio: '自动', orientation: 'square', desc: '自动', isAuto: true };
	}

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

// 前端与宿主用同一组规则校验自定义尺寸：宽高为 16 的倍数、比例 1:3~3:1、不超过上限。
// 返回错误文案（中文）表示不合法，返回空串表示合法。
export function validateCustomSize(width, height, rules) {
	if (!rules) return '当前模型不支持自定义尺寸';
	const w = Number(width);
	const h = Number(height);
	if (!Number.isInteger(w) || !Number.isInteger(h) || w <= 0 || h <= 0) {
		return '宽高需为正整数';
	}
	if (w % rules.multipleOf !== 0 || h % rules.multipleOf !== 0) {
		return `宽高需为 ${rules.multipleOf} 的倍数`;
	}
	if (w > rules.maxWidth || h > rules.maxHeight) {
		return `宽高上限为 ${rules.maxWidth} × ${rules.maxHeight}`;
	}
	const ratio = w / h;
	if (ratio < 1 / rules.maxAspectRatio || ratio > rules.maxAspectRatio) {
		return `宽高比需在 1:${rules.maxAspectRatio} 与 ${rules.maxAspectRatio}:1 之间`;
	}
	return '';
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
