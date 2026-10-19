import { readonly, ref, watch } from 'vue';

// 与宿主 AttachmentPathResolver 同值的前端预检：宿主校验是权威，这里只为在用户操作时
// 立刻给出中文提示，绝不代替准入判定。
export const MaxAttachmentsPerInput = 6;
export const MaxAttachmentByteLength = 10 * 1024 * 1024;

const mediaTypesByExtension = {
	png: 'image/png',
	jpg: 'image/jpeg',
	jpeg: 'image/jpeg',
	webp: 'image/webp',
	gif: 'image/gif',
};
const allowedMediaTypes = new Set(Object.values(mediaTypesByExtension));

// 浏览器给的 file.type 可能是空串或非图片，扩展名是次选；两者都不可信，宿主仍会按魔数复核。
export function resolveImageMediaType(fileName, declared) {
	if (allowedMediaTypes.has(declared)) {
		return declared;
	}

	const extension =
		String(fileName || '')
			.split('.')
			.pop()
			?.toLowerCase() || '';
	return mediaTypesByExtension[extension] || '';
}

export function isSupportedImageFile(file) {
	return Boolean(file) && Boolean(resolveImageMediaType(file.name, file.type));
}

// 宿主只接受裸 base64：data:...;base64, 前缀必须在前后端边界的前端一侧剥掉。
export function stripDataUrlPrefix(dataUrl) {
	const text = String(dataUrl || '');
	const separator = text.indexOf(',');
	return text.startsWith('data:') && separator >= 0 ? text.slice(separator + 1) : '';
}

function readAsDataUrl(file) {
	return new Promise((resolve, reject) => {
		const reader = new FileReader();
		reader.onload = () => resolve(String(reader.result || ''));
		reader.onerror = () => reject(reader.error || new Error('读取图片失败。'));
		reader.readAsDataURL(file);
	});
}

/**
 * 待发送图片。文件系统只由宿主持有：本 composable 只保管宿主回的引用（id + 元数据 + 预览地址），
 * 并保证新对话的附件落进 `send-prompt` 将要使用的同一个 conversationId 目录。
 */
export function useComposerAttachments(bridge, selectedConversationId) {
	const items = ref([]);
	const error = ref('');
	const busy = ref(false);
	// 新对话的 uuid 提前在这里生成：附件目录与随后的会话必须一致，因此不能等 submit 才决定。
	let pendingConversationId = '';
	let disposed = false;

	const readSelection = typeof selectedConversationId === 'function' ? selectedConversationId : () => selectedConversationId?.value || '';

	function resolveConversationId() {
		const selected = readSelection() || '';
		if (selected) {
			return selected;
		}

		if (!pendingConversationId) {
			pendingConversationId = crypto.randomUUID();
		}

		return pendingConversationId;
	}

	function canAdd(incoming = 1) {
		const additional = Math.max(0, Number(incoming) || 0);
		return items.value.length + additional <= MaxAttachmentsPerInput;
	}

	function normalize(record, conversationId) {
		const attachmentId = String(record?.attachmentId || '');
		if (!attachmentId) {
			return null;
		}

		return {
			attachmentId,
			fileName: record.fileName || 'image',
			mediaType: record.mediaType || '',
			byteLength: Number(record.byteLength) || 0,
			// previewUrl 允许缺失（宿主在文件已不在磁盘时省略它），chip 届时显示占位而不是破图。
			previewUrl: record.previewUrl || '',
			conversationId,
		};
	}

	function adopt(records, conversationId) {
		let added = 0;
		for (const record of records) {
			const item = normalize(record, conversationId);
			if (!item || items.value.some((entry) => entry.attachmentId === item.attachmentId)) {
				continue;
			}

			if (items.value.length >= MaxAttachmentsPerInput) {
				// 超出上限的图片已被宿主落盘，必须就地丢弃，否则会留下无人引用的文件。
				void discard(item);
				continue;
			}

			items.value = [...items.value, item];
			added++;
		}

		if (records.length > added && items.value.length >= MaxAttachmentsPerInput) {
			error.value = `最多可发送 ${MaxAttachmentsPerInput} 张图片，多出的图片未加入。`;
		}

		return added;
	}

	async function discard(item) {
		if (!item?.attachmentId) {
			return;
		}

		try {
			await bridge.request('attachments/discard', {
				conversationId: item.conversationId,
				attachmentId: item.attachmentId,
			});
		} catch {
			// 丢弃失败不阻塞本地移除：残留目录由宿主的启动清理兜底。
		}
	}

	async function addFromPicker() {
		if (disposed || busy.value) {
			return 0;
		}

		if (!canAdd(1)) {
			error.value = `最多可发送 ${MaxAttachmentsPerInput} 张图片。`;
			return 0;
		}

		const conversationId = resolveConversationId();
		busy.value = true;
		error.value = '';
		try {
			const response = await bridge.request('attachments/pick', { conversationId, multiple: true });
			if (disposed) {
				return 0;
			}

			if (response?.ok === false) {
				throw new Error(response.error || '无法添加图片。');
			}

			return adopt(Array.isArray(response?.attachments) ? response.attachments : [], conversationId);
		} catch (failure) {
			if (!disposed) {
				error.value = failure?.message || '无法添加图片，请重试。';
			}

			return 0;
		} finally {
			busy.value = false;
		}
	}

	async function addFromFiles(files) {
		const list = Array.from(files || []);
		if (disposed || !list.length || busy.value) {
			return 0;
		}

		const conversationId = resolveConversationId();
		busy.value = true;
		error.value = '';
		const failures = [];
		let added = 0;
		try {
			for (const file of list) {
				if (disposed) {
					return added;
				}

				if (!canAdd(1)) {
					failures.push(`最多可发送 ${MaxAttachmentsPerInput} 张图片。`);
					break;
				}

				const mediaType = resolveImageMediaType(file?.name, file?.type);
				if (!mediaType) {
					failures.push(`「${file?.name || '图片'}」不是支持的图片格式，只能添加 PNG、JPEG、WebP 或 GIF。`);
					continue;
				}

				if (Number(file?.size) > MaxAttachmentByteLength) {
					failures.push(`「${file.name}」超过 10 MB 上限。`);
					continue;
				}

				let dataUrl = '';
				try {
					dataUrl = await readAsDataUrl(file);
				} catch {
					failures.push(`读取「${file.name}」失败，请重新添加。`);
					continue;
				}

				const dataBase64 = stripDataUrlPrefix(dataUrl);
				if (!dataBase64) {
					failures.push(`读取「${file.name}」失败，请重新添加。`);
					continue;
				}

				try {
					const response = await bridge.request('attachments/ingest-bytes', {
						conversationId,
						fileName: file.name,
						mediaType,
						dataBase64,
					});
					if (disposed) {
						return added;
					}

					if (response?.ok === false) {
						throw new Error(response.error || '无法添加图片。');
					}

					added += adopt(Array.isArray(response?.attachments) ? response.attachments : [], conversationId);
				} catch (failure) {
					failures.push(failure?.message || `无法添加「${file.name}」。`);
				}
			}
		} finally {
			busy.value = false;
		}

		if (!disposed && failures.length) {
			error.value = failures.join('；');
		}

		return added;
	}

	async function remove(attachmentId) {
		const item = items.value.find((entry) => entry.attachmentId === attachmentId);
		if (!item) {
			return;
		}

		items.value = items.value.filter((entry) => entry.attachmentId !== attachmentId);
		await discard(item);
	}

	// 提交被接受后宿主已通过队列持有引用：这里只清空 chip，绝不再删文件。
	function clear() {
		items.value = [];
		error.value = '';
	}

	async function discardAll() {
		const pending = items.value;
		items.value = [];
		error.value = '';
		await Promise.all(pending.map((item) => discard(item)));
	}

	watch(readSelection, (next) => {
		const accepted = Boolean(next) && next === pendingConversationId;
		pendingConversationId = '';
		if (accepted) {
			return;
		}

		void discardAll();
	});

	return {
		items: readonly(items),
		error: readonly(error),
		busy: readonly(busy),
		canAdd,
		resolveConversationId,
		addFromPicker,
		addFromFiles,
		remove,
		clear,
		discardAll,
	};
}
