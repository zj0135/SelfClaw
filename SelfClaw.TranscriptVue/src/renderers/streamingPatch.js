/**
 * streamingPatch.js
 *
 * 专为流式 Markdown 渲染打造的增量 DOM 补丁与平滑淡入动效引擎。
 * 保持已渲染 DOM 节点稳定，消除频繁全量重建引起的卡顿与一闪一闪，
 * 并为新增段落与流式追加文字提供丝滑的物理淡入动画。
 */

function createTemplateFragment(html) {
	const template = document.createElement('template');
	template.innerHTML = String(html || '');
	return template.content;
}

function syncAttributes(targetEl, sourceEl) {
	// 移除 targetEl 上在 sourceEl 中不存在的属性（保留动画标记与过渡类）
	for (let i = targetEl.attributes.length - 1; i >= 0; i -= 1) {
		const attr = targetEl.attributes[i];
		if (!sourceEl.hasAttribute(attr.name)) {
			targetEl.removeAttribute(attr.name);
		}
	}

	// 复制 sourceEl 的属性
	for (let i = 0; i < sourceEl.attributes.length; i += 1) {
		const attr = sourceEl.attributes[i];
		if (attr.name === 'class') {
			// 保留 targetEl 原有的 stream-fade-in 类
			const hadFadeIn = targetEl.classList.contains('stream-fade-in');
			targetEl.setAttribute('class', attr.value);
			if (hadFadeIn) {
				targetEl.classList.add('stream-fade-in');
			}
		} else if (targetEl.getAttribute(attr.name) !== attr.value) {
			targetEl.setAttribute(attr.name, attr.value);
		}
	}
}

function reconcileElement(oldEl, newEl, isStreaming) {
	syncAttributes(oldEl, newEl);

	const oldText = oldEl.textContent || '';
	const newText = newEl.textContent || '';

	// 纯文本容器且新文本以旧文本开头：最常见的流式追加
	if (newEl.children.length === 0) {
		if (oldText === newText) {
			return;
		}

		if (newText.startsWith(oldText)) {
			const delta = newText.slice(oldText.length);
			if (delta.length > 0) {
				if (isStreaming) {
					const tokenSpan = document.createElement('span');
					tokenSpan.className = 'stream-token stream-fade-in';
					tokenSpan.textContent = delta;
					oldEl.appendChild(tokenSpan);
				} else {
					oldEl.appendChild(document.createTextNode(delta));
				}
			}
			return;
		}
	}

	// 包含子元素或非纯前缀追加：对子节点列表进行增量比对
	const oldChildren = Array.from(oldEl.childNodes);
	const newChildren = Array.from(newEl.childNodes);
	const maxLen = Math.max(oldChildren.length, newChildren.length);

	for (let i = 0; i < maxLen; i += 1) {
		const oldChild = oldChildren[i];
		const newChild = newChildren[i];

		if (!oldChild && newChild) {
			const cloned = newChild.cloneNode(true);
			if (isStreaming && cloned.nodeType === Node.ELEMENT_NODE) {
				cloned.classList.add('stream-fade-in');
			}
			oldEl.appendChild(cloned);
		} else if (oldChild && !newChild) {
			oldEl.removeChild(oldChild);
		} else if (oldChild && newChild) {
			if (oldChild.nodeType === newChild.nodeType && oldChild.nodeName === newChild.nodeName) {
				if (oldChild.nodeType === Node.ELEMENT_NODE) {
					reconcileElement(oldChild, newChild, isStreaming);
				} else if (oldChild.nodeType === Node.TEXT_NODE) {
					if (oldChild.nodeValue !== newChild.nodeValue) {
						oldChild.nodeValue = newChild.nodeValue;
					}
				}
			} else {
				const cloned = newChild.cloneNode(true);
				if (isStreaming && cloned.nodeType === Node.ELEMENT_NODE) {
					cloned.classList.add('stream-fade-in');
				}
				oldEl.replaceChild(cloned, oldChild);
			}
		}
	}
}

/**
 * 将 targetHtml 增量渲染到 container 中。
 *
 * @param {HTMLElement} container 目标挂载容器
 * @param {string} targetHtml 经过消毒的 Markdown 渲染 HTML
 * @param {object} options 选项
 * @param {boolean} options.isStreaming 当前是否处于流式传输中
 */
export function patchStreamingHtml(container, targetHtml, { isStreaming = false } = {}) {
	if (!container) {
		return;
	}

	const trimmedHtml = String(targetHtml || '').trim();
	if (!trimmedHtml) {
		container.innerHTML = '';
		return;
	}

	// 容器为空且非流式状态（如初始加载历史消息）：直接赋值最高效
	if (!isStreaming && container.childNodes.length === 0) {
		container.innerHTML = trimmedHtml;
		return;
	}

	// 流式结束时：直接将干净的终态 HTML 同步并返回，去除所有临时动画包装
	if (!isStreaming) {
		container.innerHTML = trimmedHtml;
		return;
	}

	const fragment = createTemplateFragment(trimmedHtml);
	const newNodes = Array.from(fragment.childNodes);
	const oldNodes = Array.from(container.childNodes);
	const maxLen = Math.max(oldNodes.length, newNodes.length);

	for (let i = 0; i < maxLen; i += 1) {
		const oldNode = oldNodes[i];
		const newNode = newNodes[i];

		if (!oldNode && newNode) {
			const cloned = newNode.cloneNode(true);
			if (isStreaming && cloned.nodeType === Node.ELEMENT_NODE) {
				cloned.classList.add('stream-fade-in');
			}
			container.appendChild(cloned);
		} else if (oldNode && !newNode) {
			container.removeChild(oldNode);
		} else if (oldNode && newNode) {
			if (oldNode.nodeType === newNode.nodeType && oldNode.nodeName === newNode.nodeName) {
				if (oldNode.nodeType === Node.ELEMENT_NODE) {
					reconcileElement(oldNode, newNode, isStreaming);
				} else if (oldNode.nodeType === Node.TEXT_NODE) {
					if (oldNode.nodeValue !== newNode.nodeValue) {
						oldNode.nodeValue = newNode.nodeValue;
					}
				}
			} else {
				const cloned = newNode.cloneNode(true);
				if (isStreaming && cloned.nodeType === Node.ELEMENT_NODE) {
					cloned.classList.add('stream-fade-in');
				}
				container.replaceChild(cloned, oldNode);
			}
		}
	}
}

/**
 * 规范化容器内部，移除临时动画标签并合并文本节点。
 */
export function normalizeStreamingContainer(container, targetHtml) {
	if (!container) {
		return;
	}

	if (typeof targetHtml === 'string') {
		container.innerHTML = targetHtml;
		return;
	}

	const tokens = container.querySelectorAll('.stream-token, .stream-fade-in');
	for (let i = 0; i < tokens.length; i += 1) {
		const el = tokens[i];
		el.classList.remove('stream-fade-in');
		if (el.classList.contains('stream-token')) {
			el.replaceWith(...el.childNodes);
		}
	}
	container.normalize();
}
