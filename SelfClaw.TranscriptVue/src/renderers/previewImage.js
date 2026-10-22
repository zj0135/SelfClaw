// 正文/思考的富文本经 v-html 注入，其中的 <img> 不是组件渲染的元素，
// 点击预览只能靠事件委托 + closest 命中。承载 v-html 的组件各自调用它。
export function resolvePreviewImage(target) {
	if (!(target instanceof Element)) {
		return null;
	}

	const image = target.closest('.message-attachment-image, .markdown-content img');
	if (!(image instanceof HTMLImageElement)) {
		return null;
	}

	const src = image.currentSrc || image.src || '';
	if (!src) {
		return null;
	}

	return {
		src,
		alt: image.getAttribute('alt') || '',
	};
}
