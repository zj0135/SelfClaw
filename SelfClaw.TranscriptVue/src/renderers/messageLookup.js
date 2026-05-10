import {
	getMessageSegments,
	renderThinkingContent,
	renderToolDetails,
	renderToolGroupDetails,
	thinkingBlockId,
	toolGroupId,
	toolSegmentId,
} from './messages';

function findMessageByElement(actionElement) {
	const messageRow = actionElement.closest('.message-row');
	const messageId = messageRow?.getAttribute('data-message-id');
	if (!messageId) {
		return null;
	}

	return { messageRow, messageId };
}

export function findThinkingSegment(items, actionElement) {
	const id = actionElement.getAttribute('data-thinking-id');
	const match = findMessageByElement(actionElement);
	if (!id || !match) {
		return null;
	}

	const item = items.find((candidate) => candidate.id === match.messageId);
	if (!item) {
		return null;
	}

	let thinkingOrdinal = 0;
	for (const segment of getMessageSegments(item)) {
		if (segment.kind !== 'thinking') {
			continue;
		}

		if (thinkingBlockId(item.id, thinkingOrdinal) === id) {
			return segment;
		}

		thinkingOrdinal += 1;
	}

	return null;
}

export function findToolSegment(items, actionElement) {
	const id = actionElement.getAttribute('data-tool-segment-id');
	const match = findMessageByElement(actionElement);
	if (!id || !match) {
		return null;
	}

	const item = items.find((candidate) => candidate.id === match.messageId);
	if (!item) {
		return null;
	}

	const segments = getMessageSegments(item);
	for (let index = 0; index < segments.length; index += 1) {
		const segment = segments[index];
		if (segment.kind === 'tool' && toolSegmentId(item.id, segment, index) === id) {
			return segment;
		}
	}

	return null;
}

export function findToolGroup(items, actionElement) {
	const id = actionElement.getAttribute('data-tool-group-id');
	const match = findMessageByElement(actionElement);
	if (!id || !match) {
		return null;
	}

	const item = items.find((candidate) => candidate.id === match.messageId);
	if (!item) {
		return null;
	}

	const segments = getMessageSegments(item);
	for (let index = 0; index < segments.length; index += 1) {
		if (segments[index].kind !== 'tool') {
			continue;
		}

		let endIndex = index;
		while (endIndex + 1 < segments.length && segments[endIndex + 1].kind === 'tool') {
			endIndex += 1;
		}

		if (endIndex > index && toolGroupId(item.id, index, endIndex) === id) {
			return {
				item,
				startIndex: index,
				toolSegments: segments.slice(index, endIndex + 1),
			};
		}

		index = endIndex;
	}

	return null;
}

export function ensureThinkingContent(block, segment) {
	if (!block || !segment) {
		return;
	}

	const content = block.querySelector('.thinking-content');
	if (content && !content.firstElementChild) {
		content.innerHTML = renderThinkingContent(segment);
	}
}

export function clearThinkingContent(block) {
	block?.querySelector('.thinking-content')?.replaceChildren();
}

export function ensureToolDetails(block, segment) {
	if (!block || !segment || block.querySelector('.tool-details')) {
		return;
	}

	block.insertAdjacentHTML(
		'beforeend',
		renderToolDetails(segment.status || 'completed', segment.detailTitle || 'Tool', segment.detailText || 'No details available.')
	);
}

export function clearToolDetails(block) {
	block?.querySelector('.tool-details')?.remove();
}

export function ensureToolGroupDetails(block, group, openToolSegments) {
	const details = block?.querySelector('.tool-group-details');
	if (!details || !group || details.firstElementChild) {
		return;
	}

	details.innerHTML = renderToolGroupDetails(group.item, group.toolSegments, group.startIndex, openToolSegments);
}

export function clearToolGroupDetails(block) {
	block?.querySelector('.tool-group-details')?.replaceChildren();
}
