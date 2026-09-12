// Self-contained so Playwright can install the same host before the application's module imports.
export function installActivityHost() {
	const listeners = new Set();
	const requests = [];
	const ids = ['11111111-1111-4111-8111-111111111111', '22222222-2222-4222-8222-222222222222', '33333333-3333-4333-8333-333333333333'];
	const parent = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa';
	let scope = parent;
	let subscription = null;
	let selection = null;
	let selectedTask = null;
	let revision = 0;
	let contentRevision = 1;
	let pageOffset = 0;
	let paused = false;
	let holdResponses = false;
	const deferred = [];
	const task = (id, index) => ({
		taskId: id, parentTurnId: parent, subagentId: 'reviewer', subagentName: `Reviewer ${index + 1}`,
		taskPreview: 'Inspect streaming output and tool results.', attempt: 1, status: 'running', phase: 'thinking',
		canCancel: true, cancelRequested: false, pendingApprovalCount: 0, deliveryStatus: 'none', deliveryAttemptCount: 0,
		queuedAtUtc: new Date(Date.now() - 5000).toISOString(), startedAtUtc: new Date(Date.now() - 3000).toISOString(),
		modelDisplayName: 'Fixture model',
	});
	const tasks = ids.map(task);
	const plugin = { key: 'fixture:panel', pluginId: 'fixture', title: 'Fixture', origin: 'https://fixture.plugin.selfclaw.local', permissions: [] };
	const taskSegments = (id) => [
		{ kind: 'thinking', markdown: `Reasoning for ${id}`, segmentId: `${id}:thinking:0`, segmentOrdinal: 0, isPending: false },
		{ kind: 'content', markdown: 'Partial **answer**', segmentId: `${id}:text:1`, segmentOrdinal: 1, isPending: false },
		{ kind: 'tool', markdown: '', segmentId: id, segmentOrdinal: 2, toolName: 'read_file', text: 'Read example.cs', detailTitle: 'Read File', detailText: 'example.cs', status: 'running', isPending: true },
	];
	const segments = ids.map(taskSegments);
	function deliver(payload) { for (const listener of listeners) listener({ data: JSON.parse(JSON.stringify(payload)) }); }
	function send(payload) {
		if (holdResponses && payload.requestId) deferred.push(JSON.parse(JSON.stringify(payload)));
		else deliver(payload);
	}
	function snapshot(requestId) {
		const selectedIndex = ids.indexOf(selectedTask);
		const detail = selectedIndex < 0 ? null : {
			taskId: selectedTask, task: tasks[selectedIndex], taskText: tasks[selectedIndex].taskPreview,
			contentVersion: String(contentRevision), contentOrigin: tasks[selectedIndex].status === 'running' ? 'live' : 'persisted', historyCompleteness: 'complete',
			blockOffset: 0, totalBlocks: segments[selectedIndex].length, earlierOffset: null, laterOffset: null,
			message: { id: selectedTask, kind: 'message', role: 'assistant', status: 'streaming', isThinking: true, segments: segments[selectedIndex] },
			unplacedTools: [], content: [{ contentId: `tool/${selectedTask}/arguments`, segmentId: selectedTask, field: 'arguments', totalCharacters: 2, previewCharacters: 0, isTruncated: true }],
		};
		return { type: 'activity-panel/state', requestId, schemaVersion: 1, subscriptionId: subscription, parentConversationId: scope, revision: ++revision,
			sections: [{ id: 'subagents', kind: 'subagents', title: '子代理', counts: { total: tasks.length, running: tasks.filter((item) => item.status === 'running').length, queued: 0, succeeded: tasks.filter((item) => item.status === 'succeeded').length, failed: 0, cancelled: tasks.filter((item) => item.status === 'cancelled').length, interrupted: 0 }, listVersion: 'fixture-list', cursor: pageOffset ? `page/${pageOffset}` : null, nextCursor: pageOffset + 50 < tasks.length ? `page/${pageOffset + 50}` : null, tasks: tasks.slice(pageOffset, pageOffset + 50), detailSelectionId: selection, detail }] };
	}
	function push() { if (subscription && !paused) send(snapshot()); }
	window.chrome = { ...(window.chrome || {}), webview: {
		addEventListener(type, listener) { if (type === 'message') listeners.add(listener); },
		postMessage(request) {
			requests.push(request);
			queueMicrotask(() => {
				const { type, requestId } = request;
				if (type === 'activity-panel/subscribe') { subscription = request.subscriptionId; scope = request.parentConversationId; selection = null; selectedTask = null; pageOffset = 0; send(snapshot(requestId)); }
				else if (type === 'activity-panel/select-detail') { selectedTask = request.taskId; selection = request.detailSelectionId; send(snapshot(requestId)); }
				else if (type === 'activity-panel/get-state') { pageOffset = request.cursor ? Number(request.cursor.split('/')[1]) : 0; send(snapshot(requestId)); }
				else if (type === 'activity-panel/unsubscribe') { if (request.subscriptionId === subscription) subscription = null; }
				else if (type === 'activity-panel/cancel-task') { const target = tasks.find((item) => item.taskId === request.taskId); target.status = 'cancelled'; target.phase = 'cancelled'; target.canCancel = false; send({ type, requestId, accepted: true }); push(); }
				else if (type === 'activity-panel/read-content') send({ type: 'activity-panel/content', requestId, subscriptionId: subscription, detailSelectionId: selection, taskId: selectedTask, contentVersion: String(contentRevision), contentId: request.contentId, text: '{}', offset: 0, totalCharacters: 2 });
				else if (type === 'plugin-host/get-panels' && localStorage.getItem('activity:plugin') === '1') send({ type, requestId, panels: [plugin], tabs: [plugin.key] });
				else if (type === 'plugin-host/open') send({ type, requestId, panel: plugin, url: `${plugin.origin}/index.html` });
				else if (requestId) send({ type, requestId, sections: [], models: [], agents: [], tabs: [], panels: [], roots: [], commonFolders: [], current: null });
			});
		},
	} };
	window.activityFixture = {
		parent, ids, requests, tasks, segments, send, snapshot, push,
		setTaskCount(count) {
			while (tasks.length < count) {
				const id = `ffffffff-ffff-4fff-8fff-${String(tasks.length + 1).padStart(12, '0')}`;
				tasks.push(task(id, tasks.length)); ids.push(id); segments.push(taskSegments(id));
			}
			push();
		},
		resetTaskPage() { pageOffset = 0; const state = snapshot(); state.sections[0].listReset = true; send(state); },
		holdResponses(value) { holdResponses = value; },
		release(index = 0, error = null) { const payload = deferred.splice(index, 1)[0]; deliver(error ? { type: 'activity-panel/error', requestId: payload.requestId, error } : payload); },
		pause(value) { paused = value; },
		text(value, index = 0) { segments[index][1].markdown += value; contentRevision++; push(); },
		finish(index = 0) { tasks[index].status = 'succeeded'; tasks[index].phase = 'succeeded'; tasks[index].canCancel = false; tasks[index].deliveryStatus = 'pending'; tasks[index].completedAtUtc = new Date().toISOString(); segments[index][2].status = 'completed'; segments[index][2].isPending = false; segments[index][2].detailText = 'Recorded tool result'; contentRevision++; push(); },
		transcript(parentId = parent) { send({ type: 'replaceState', revision: 1, selectedConversationId: parentId, isBusy: false, agentMode: 'direct', selectedAgentId: 'build', selectedAgentName: 'Build', conversations: [{ id: parentId, title: 'Activity verification' }], items: [{ id: 'parent-message', kind: 'message', role: 'assistant', status: 'completed', timestamp: '12:00', isThinking: false, segments: [{ kind: 'content', markdown: 'Parent answer remains independent.\n\n' + 'Transcript line.\n\n'.repeat(25) }] }] }); },
	};
}
