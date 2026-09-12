import { describe, expect, it } from 'vitest';
import { createEmptyActivityState, reduceActivityState } from '../src/renderers/activityPanelState.js';

const state = (revision, selection = null) => ({ schemaVersion: 1, subscriptionId: 'current', parentConversationId: 'parent', revision,
	sections: [{ id: 'subagents', kind: 'subagents', tasks: [], detailSelectionId: selection, detail: { taskId: 'A' } }] });
describe('activity snapshot reducer', () => {
	it('rejects old scope and accepts duplicate ACK without applying twice', () => {
		const first = reduceActivityState(createEmptyActivityState(), state(2), 'current', 'parent').state;
		expect(reduceActivityState(first, state(1), 'current', 'parent')).toMatchObject({ accepted: false, acknowledge: true });
		expect(reduceActivityState(first, state(3), 'new', 'parent')).toMatchObject({ accepted: false, acknowledge: false });
		expect(reduceActivityState(first, state(3), 'current', 'other')).toMatchObject({ accepted: false, acknowledge: false });
		expect(reduceActivityState(first, state(3), null, 'parent')).toMatchObject({ accepted: false, acknowledge: false });
	});
	it('clears omitted nullable fields and cannot restore old detail selection', () => {
		const first = reduceActivityState(createEmptyActivityState(), state(1, 'old'), 'current', 'parent', 'old').state;
		const next = reduceActivityState(first, state(2, 'old'), 'current', 'parent', 'new').state;
		expect(next.sections[0].detail).toBeNull();
		const cleared = reduceActivityState(next, { ...state(3), sections: [] }, 'current', 'parent').state;
		expect(cleared.sections).toEqual([]);
		expect(cleared.stateError).toBeNull();
	});
});
