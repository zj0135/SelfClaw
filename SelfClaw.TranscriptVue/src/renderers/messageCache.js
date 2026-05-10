import { getMessageSegments, renderMessages, thinkingBlockId, toolGroupId, toolSegmentId } from './messages';

const MAX_CACHE_SIZE = 240;
const SIGNATURE_SAMPLE_LENGTH = 160;

function summarizeLargeValue(value) {
	const text = String(value || '');
	if (text.length <= SIGNATURE_SAMPLE_LENGTH * 2) {
		return text;
	}

	return `${text.length}:${text.slice(0, SIGNATURE_SAMPLE_LENGTH)}:${text.slice(-SIGNATURE_SAMPLE_LENGTH)}`;
}

function getSegmentSignature(segment, index) {
	const kind = String(segment?.kind || '');
	switch (kind) {
		case 'tool':
			return [
				kind,
				segment.segmentId || index,
				segment.text || '',
				segment.status || '',
				segment.durationText || '',
				segment.detailTitle || '',
				summarizeLargeValue(segment.detailText),
			].join('\u001f');
		case 'thinking':
			return [kind, Boolean(segment.isPending), summarizeLargeValue(segment.html)].join('\u001f');
		default:
			return [kind, summarizeLargeValue(segment.html)].join('\u001f');
	}
}

function getAttachmentsSignature(attachments) {
	if (!Array.isArray(attachments) || attachments.length === 0) {
		return '';
	}

	return attachments
		.map((attachment) =>
			[
				attachment?.id || '',
				attachment?.fileName || '',
				attachment?.mediaType || '',
				attachment?.byteLength || 0,
				summarizeLargeValue(attachment?.dataUrl),
			].join('\u001e')
		)
		.join('\u001d');
}

function getMessageContentSignature(item) {
	return [
		item.id || '',
		item.kind || '',
		item.role || '',
		item.status || '',
		item.title || '',
		item.subtitle || '',
		item.timestamp || '',
		Boolean(item.isThinking),
		getAttachmentsSignature(item.attachments),
		getMessageSegments(item).map(getSegmentSignature).join('\u001c'),
	].join('\u001b');
}

function getMessageOpenState(item, openThoughts, openToolSegments, openToolGroups) {
	const segments = getMessageSegments(item);
	const states = [];
	let thinkingOrdinal = 0;

	for (let index = 0; index < segments.length; index += 1) {
		const segment = segments[index];
		if (segment.kind === 'thinking') {
			const id = thinkingBlockId(item.id, thinkingOrdinal++);
			if (openThoughts.has(id)) {
				states.push(`t:${id}`);
			}
			continue;
		}

		if (segment.kind !== 'tool') {
			continue;
		}

		let endIndex = index;
		while (endIndex + 1 < segments.length && segments[endIndex + 1].kind === 'tool') {
			endIndex += 1;
		}

		if (endIndex > index) {
			const groupId = toolGroupId(item.id, index, endIndex);
			if (openToolGroups.has(groupId)) {
				states.push(`g:${groupId}`);
			}
		}

		for (let toolIndex = index; toolIndex <= endIndex; toolIndex += 1) {
			const id = toolSegmentId(item.id, segments[toolIndex], toolIndex);
			if (openToolSegments.has(id)) {
				states.push(`s:${id}`);
			}
		}

		index = endIndex;
	}

	return states.join('|');
}

function touchCacheEntry(cache, key, entry) {
	cache.delete(key);
	cache.set(key, entry);
}

export function createMessageHtmlCache() {
	const cache = new Map();

	function renderItem(item, openThoughts, openToolSegments, openToolGroups) {
		const key = item.id || `message:${cache.size}`;
		const contentSignature = getMessageContentSignature(item);
		const openState = getMessageOpenState(item, openThoughts, openToolSegments, openToolGroups);
		const cached = cache.get(key);
		if (cached?.contentSignature === contentSignature && cached.openState === openState) {
			touchCacheEntry(cache, key, cached);
			return cached.html;
		}

		const html = renderMessages([item], openThoughts, openToolSegments, openToolGroups);
		touchCacheEntry(cache, key, { contentSignature, openState, html });

		if (cache.size > MAX_CACHE_SIZE) {
			cache.delete(cache.keys().next().value);
		}

		return html;
	}

	return {
		render(items, openThoughts, openToolSegments, openToolGroups) {
			if (!items?.length) {
				return renderMessages(items, openThoughts, openToolSegments, openToolGroups);
			}

			const liveKeys = new Set(items.map((item, index) => item.id || `message:${index}`));
			for (const key of cache.keys()) {
				if (!liveKeys.has(key)) {
					cache.delete(key);
				}
			}

			return items.map((item) => renderItem(item, openThoughts, openToolSegments, openToolGroups)).join('');
		},
		clear() {
			cache.clear();
		},
	};
}
