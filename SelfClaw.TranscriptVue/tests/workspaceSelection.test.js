import { effectScope, ref } from 'vue';
import { flushPromises } from '@vue/test-utils';
import { afterEach, expect, it, vi } from 'vitest';
import { useWorkspaceSelection } from '../src/composables/useWorkspaceSelection.js';

let scope;
afterEach(() => scope?.stop());

function createSelection() {
	const requests = [];
	function enqueue(type, payload) {
		const response = { type, payload, ...Promise.withResolvers() };
		requests.push(response);
		return response.promise;
	}
	const bridge = { request: vi.fn(enqueue), requestLatest: vi.fn((key, type, payload) => enqueue(type, payload)) };
	const parent = ref('first');
	scope = effectScope();
	const selection = scope.run(() => useWorkspaceSelection(parent, bridge));
	return { requests, parent, selection };
}

it.each(['success', 'failure'])('ignores a late Git %s after switching conversations', async (outcome) => {
	const { requests, parent, selection } = createSelection();
	requests[0].resolve({ current: { id: 'workspace-a', branchName: 'main' } });
	await flushPromises();
	const git = selection.runGitAction({ type: 'refresh' });
	parent.value = 'second';
	requests[2].resolve({ current: { id: 'workspace-b', branchName: 'current' } });
	await flushPromises();
	if (outcome === 'success') requests[1].resolve({ state: { branchName: 'obsolete' } });
	else requests[1].reject(new Error('obsolete failure'));
	await git;
	expect(selection.state.current).toMatchObject({ id: 'workspace-b', branchName: 'current' });
	expect(selection.state.gitError).toBe('');
	expect(selection.state.gitLoading).toBe(false);
});

it('keeps the latest workspace request loading when an older request fails', async () => {
	const { requests, selection } = createSelection();
	const change = selection.selectRoot('workspace-b');
	requests[0].reject(new Error('obsolete workspace request'));
	await flushPromises();
	expect(selection.state.error).toBe('');
	expect(selection.state.isLoading).toBe(true);
	requests[1].resolve({ current: { id: 'workspace-b' }, roots: [{ id: 'workspace-b' }] });
	await change;
	expect(selection.state.current.id).toBe('workspace-b');
	expect(selection.state.isLoading).toBe(false);
});

it('refreshes workspace metadata after a Git mutation and prevents overlapping Git actions', async () => {
	const { requests, selection } = createSelection();
	requests[0].resolve({ current: { id: 'workspace-a', branchName: 'main' } });
	await flushPromises();
	const change = selection.runGitAction({ type: 'switch-branch', branchName: 'feature' });
	await selection.runGitAction({ type: 'delete-branch', branchName: 'main' });
	expect(requests).toHaveLength(2);
	requests[1].resolve({ state: { branchName: 'feature', isDirty: false } });
	await flushPromises();
	expect(requests[2]).toMatchObject({ type: 'get-workspace-selection', payload: { refresh: true } });
	requests[2].resolve({ current: { id: 'workspace-a', branchName: 'feature', isManagedWorktree: true } });
	await change;
	expect(selection.state.current).toMatchObject({ branchName: 'feature', isManagedWorktree: true });
	expect(selection.state.gitLoading).toBe(false);
	expect(selection.state.isLoading).toBe(false);
});
