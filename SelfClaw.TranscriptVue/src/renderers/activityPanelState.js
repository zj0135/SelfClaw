export function createEmptyActivityState() {
	return { schemaVersion: 1, subscriptionId: null, parentConversationId: null, revision: 0, sections: [], stateError: null };
}

export function reduceActivityState(previous, incoming, subscriptionId, parentId, selectionId = null) {
	if (!subscriptionId || incoming?.subscriptionId !== subscriptionId || (incoming.parentConversationId ?? null) !== parentId) {
		return { accepted: false, acknowledge: false, state: previous };
	}
	if (incoming.schemaVersion !== 1 || !Number.isSafeInteger(incoming.revision) || incoming.revision < 1 || !Array.isArray(incoming.sections)) {
		return { accepted: false, acknowledge: false, state: previous };
	}
	if (incoming.revision <= previous.revision) return { accepted: false, acknowledge: true, state: previous };
	const sections = incoming.sections.map((section) => {
		if (section.kind !== 'subagents') return section;
		const matches = (section.detailSelectionId ?? null) === selectionId;
		return { ...section, tasks: section.tasks || [], detail: matches ? section.detail ?? null : null,
			selectedTask: matches ? section.selectedTask ?? null : null,
			detailError: matches ? section.detailError ?? null : null, detailSelectionId: section.detailSelectionId ?? null };
	});
	return { accepted: true, acknowledge: true, state: { ...incoming, sections, parentConversationId: incoming.parentConversationId ?? null, stateError: incoming.stateError ?? null } };
}

export function getSubagentSection(state) {
	return state.sections.find((section) => section.kind === 'subagents') ?? null;
}
