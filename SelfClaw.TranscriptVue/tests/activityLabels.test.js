import { expect, it } from 'vitest';
import { activityTimingLabel } from '../src/renderers/activityLabels.js';

const now = Date.parse('2026-11-01T10:00:10Z');

it('shows only the queue time before a task starts and only the run time afterwards', () => {
	expect(activityTimingLabel({ queuedAtUtc: '2026-11-01T10:00:00Z' }, now)).toBe('排队 10s');
	expect(activityTimingLabel({ queuedAtUtc: '2026-11-01T10:00:00Z', startedAtUtc: '2026-11-01T10:00:04Z' }, now)).toBe('运行 6s');
	expect(activityTimingLabel({
		queuedAtUtc: '2026-11-01T10:00:00Z', startedAtUtc: '2026-11-01T10:00:04Z', completedAtUtc: '2026-11-01T10:01:10Z',
	}, now)).toBe('运行 1m 6s');
	expect(activityTimingLabel({ queuedAtUtc: 'not-a-date' }, now)).toBe('');
	expect(activityTimingLabel({ queuedAtUtc: '2026-11-01T10:00:00Z', startedAtUtc: 'not-a-date' }, now)).toBe('');
});
