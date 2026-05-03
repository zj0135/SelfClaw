const GRAPH_VIEWBOX = { width: 1200, height: 720 };

const HUMAN_ID = 'human';
const PROGRAMMING_AGENT_ID = 'programming-agent';
const NODE_RADIUS = 54;
const MAX_SATELLITES_PER_AGENT = 3;
const HUMAN_POSITION = { x: 132, y: 160 };
const PROGRAMMING_AGENT_POSITION = { x: 790, y: 250 };

function isRunningStatus(status) {
	return status === 'streaming' || status === 'running';
}

function getLatestAssistantMessage(snapshot) {
	for (let index = snapshot.items.length - 1; index >= 0; index -= 1) {
		const item = snapshot.items[index];
		if (item.role === 'assistant') {
			return item;
		}
	}

	return null;
}

function hasAssistantRunning(snapshot) {
	return snapshot.items.some((item) => item.role === 'assistant' && (Boolean(item.isThinking) || isRunningStatus(item.status)));
}

function getToolStatuses(snapshot) {
	return snapshot.agentActivities
		.filter((item) => item.kind === 'tool')
		.map((item) => item.status);
}

function formatStatusText(status) {
	switch (status) {
		case 'running':
			return 'BUSY';
		case 'awaitingapproval':
			return 'WAIT';
		case 'completed':
			return 'READY';
		case 'failed':
			return 'FAIL';
		default:
			return 'IDLE';
	}
}

function resolveNodeIcon(label, summary, kind) {
	if (kind === 'human') {
		return 'human';
	}

	const normalizedLabel = String(label || '').trim().toLowerCase();
	const normalizedSummary = String(summary || '').trim().toLowerCase();
	if (
		normalizedLabel.includes('coder') ||
		normalizedLabel.includes('engineer') ||
		normalizedLabel.includes('developer') ||
		normalizedSummary.includes('coder') ||
		normalizedSummary.includes('engineer') ||
		normalizedSummary.includes('developer') ||
		kind === 'programming-agent'
	) {
		return 'code';
	}

	return 'agent';
}

function buildNode({
	id,
	kind,
	label,
	summary,
	x,
	y,
	status,
	selected = false,
	radius = NODE_RADIUS,
}) {
	return {
		id,
		kind,
		label,
		summary,
		x,
		y,
		radius,
		status,
		statusText: formatStatusText(status),
		isSelected: selected,
		isActive: status === 'running' || status === 'awaitingapproval',
		icon: resolveNodeIcon(label, summary, kind),
		satellites: [],
	};
}

function deriveProgrammingNode(snapshot) {
	const latestAssistantMessage = getLatestAssistantMessage(snapshot);
	const toolStatuses = getToolStatuses(snapshot);
	let status = 'idle';

	if (toolStatuses.includes('awaitingapproval')) {
		status = 'awaitingapproval';
	} else if (toolStatuses.includes('running') || hasAssistantRunning(snapshot)) {
		status = 'running';
	} else if (latestAssistantMessage?.status === 'failed') {
		status = 'failed';
	} else if (latestAssistantMessage?.status === 'completed') {
		status = 'completed';
	}

	return buildNode({
		id: PROGRAMMING_AGENT_ID,
		kind: 'programming-agent',
		label: latestAssistantMessage?.title || snapshot.selectedProfileModel || 'Assistant',
		summary: latestAssistantMessage?.subtitle || snapshot.selectedProfileModel || 'Programming agent',
		x: PROGRAMMING_AGENT_POSITION.x,
		y: PROGRAMMING_AGENT_POSITION.y,
		status,
		selected: true,
	});
}

function trimConnection(source, target) {
	const dx = target.x - source.x;
	const dy = target.y - source.y;
	const distance = Math.hypot(dx, dy) || 1;
	const startOffset = source.radius + 8;
	const endOffset = target.radius + 8;
	return {
		startX: source.x + (dx / distance) * startOffset,
		startY: source.y + (dy / distance) * startOffset,
		endX: target.x - (dx / distance) * endOffset,
		endY: target.y - (dy / distance) * endOffset,
	};
}

function buildPath(source, target) {
	const { startX, startY, endX, endY } = trimConnection(source, target);
	const dx = endX - startX;
	const dy = endY - startY;
	const distance = Math.hypot(dx, dy) || 1;

	if (Math.abs(dx) > 200 || Math.abs(dy) > 140) {
		const controlX = startX + dx * 0.56;
		const controlY = startY + dy * 0.2;
		return `M ${startX} ${startY} Q ${controlX} ${controlY} ${endX} ${endY}`;
	}

	if (distance < 12) {
		return `M ${startX} ${startY}`;
	}

	return `M ${startX} ${startY} L ${endX} ${endY}`;
}

function buildEdge(id, source, target, tone, active = false) {
	return {
		id,
		sourceId: source.id,
		targetId: target.id,
		path: buildPath(source, target),
		tone,
		active,
	};
}

function resolveToolGlyph(title) {
	const normalized = String(title || '').trim().toLowerCase();
	if (normalized.includes('shell')) {
		return '>_';
	}

	if (normalized.includes('write')) {
		return 'WR';
	}

	if (normalized.includes('read')) {
		return 'RD';
	}

	if (normalized.includes('search')) {
		return 'SR';
	}

	if (normalized.includes('list')) {
		return 'LS';
	}

	return 'TL';
}

function buildToolSatellite(node, activity, index) {
	const offsets = [
		{ x: -86, y: -46 },
		{ x: -102, y: 0 },
		{ x: -86, y: 46 },
	];
	const offset = offsets[index] || offsets[offsets.length - 1];
	return {
		id: activity.id,
		status: activity.status,
		label: resolveToolGlyph(activity.title),
		title: activity.title,
		summary: activity.summary,
		dx: offset.x,
		dy: offset.y,
	};
}

function attachToolSatellites(snapshot, node) {
	const toolActivities = snapshot.agentActivities
		.filter((item) => item.kind === 'tool')
		.slice(0, MAX_SATELLITES_PER_AGENT);
	node.satellites = toolActivities.map((activity, index) => buildToolSatellite(node, activity, index));
	if (node.satellites.some((item) => item.status === 'awaitingapproval')) {
		node.status = 'awaitingapproval';
		node.statusText = formatStatusText(node.status);
		node.isActive = true;
	}
}

function buildGraphMeta(node) {
	return {
		modeLabel: 'Programming',
		targetLabel: node.label || 'Assistant',
	};
}

export function normalizeGraphSnapshot(raw = {}) {
	return {
		items: Array.isArray(raw.items) ? raw.items.map((item) => ({ ...item })) : [],
		conversations: Array.isArray(raw.conversations) ? raw.conversations.map((item) => ({ ...item })) : [],
		selectedConversationId: raw.selectedConversationId || null,
		selectedProfileModel: raw.selectedProfileModel || '',
		agentActivities: Array.isArray(raw.agentActivities) ? raw.agentActivities.map((item) => ({ ...item })) : [],
	};
}

export function buildGraphModel(snapshot) {
	const human = buildNode({
		id: HUMAN_ID,
		kind: 'human',
		label: 'Human',
		summary: 'You',
		x: HUMAN_POSITION.x,
		y: HUMAN_POSITION.y,
		status: 'idle',
	});
	const agent = deriveProgrammingNode(snapshot);
	attachToolSatellites(snapshot, agent);
	const edges = [buildEdge('human:programming', human, agent, 'primary', agent.isActive || agent.isSelected)];

	return {
		viewBox: GRAPH_VIEWBOX,
		nodes: [human, agent],
		edges,
		targetAgentId: PROGRAMMING_AGENT_ID,
		meta: buildGraphMeta(agent),
		isEmpty: snapshot.items.length === 0 && snapshot.agentActivities.length === 0,
	};
}

function buildPacket(seed, sourceNode, targetNode, kind, status = kind) {
	if (!sourceNode || !targetNode || sourceNode.id === targetNode.id) {
		return null;
	}

	return {
		id: `packet-${seed}`,
		kind,
		status,
		durationMs: kind === 'user' ? 820 : 1080,
		path: buildPath(sourceNode, targetNode),
		radius: kind === 'user' ? 4.5 : 5.5,
	};
}

function buildBurst(seed, targetType, targetId, kind, status = kind, fallbackNodeId = null) {
	return {
		id: `burst-${seed}`,
		targetType,
		targetId,
		fallbackNodeId,
		kind,
		status,
		durationMs: kind === 'running' ? 960 : 1200,
	};
}

function buildEdgeTrace(seed, edgeId, tone = 'primary') {
	return {
		id: `edge-trace-${seed}`,
		edgeId,
		tone,
		durationMs: 620,
	};
}

function hasAssistantStarted(previousMessage, nextMessage) {
	const previousRunning = Boolean(previousMessage?.isThinking) || isRunningStatus(previousMessage?.status);
	const nextRunning = Boolean(nextMessage?.isThinking) || isRunningStatus(nextMessage?.status);
	return nextRunning && !previousRunning;
}

export function buildGraphAnimations(previousSnapshot, nextSnapshot, graphModel, seed = 0) {
	if (!previousSnapshot || previousSnapshot.selectedConversationId !== nextSnapshot.selectedConversationId) {
		return { packets: [], bursts: [], edgeTraces: [], nextSeed: seed };
	}

	let nextSeed = seed;
	const packets = [];
	const bursts = [];
	const edgeTraces = [];
	const nodesById = new Map(graphModel.nodes.map((item) => [item.id, item]));
	const previousMessagesById = new Map(previousSnapshot.items.map((item) => [item.id, item]));
	const previousActivitiesById = new Map(previousSnapshot.agentActivities.map((item) => [item.id, item]));

	for (const message of nextSnapshot.items) {
		const previousMessage = previousMessagesById.get(message.id);
		if (message.role === 'user' && !previousMessage) {
			nextSeed += 1;
			const packet = buildPacket(nextSeed, nodesById.get(HUMAN_ID), nodesById.get(PROGRAMMING_AGENT_ID), 'user');
			if (packet) {
				packets.push(packet);
			}
			nextSeed += 1;
			edgeTraces.push(buildEdgeTrace(nextSeed, 'human:programming'));
			continue;
		}

		if (message.role !== 'assistant') {
			continue;
		}

		if (hasAssistantStarted(previousMessage, message)) {
			nextSeed += 1;
			bursts.push(buildBurst(nextSeed, 'node', PROGRAMMING_AGENT_ID, 'running'));
		}

		if (message.status === 'completed' && previousMessage?.status !== 'completed') {
			nextSeed += 1;
			bursts.push(buildBurst(nextSeed, 'node', PROGRAMMING_AGENT_ID, 'completed'));
			nextSeed += 1;
			const packet = buildPacket(nextSeed, nodesById.get(PROGRAMMING_AGENT_ID), nodesById.get(HUMAN_ID), 'assistant', 'completed');
			if (packet) {
				packets.push(packet);
			}
		}

		if (message.status === 'failed' && previousMessage?.status !== 'failed') {
			nextSeed += 1;
			bursts.push(buildBurst(nextSeed, 'node', PROGRAMMING_AGENT_ID, 'failed'));
		}
	}

	for (const activity of nextSnapshot.agentActivities) {
		if (activity.kind !== 'tool') {
			continue;
		}

		const previousActivity = previousActivitiesById.get(activity.id);
		if (previousActivity?.status === activity.status) {
			continue;
		}

		nextSeed += 1;
		bursts.push(buildBurst(nextSeed, 'satellite', activity.id, 'tool', activity.status, PROGRAMMING_AGENT_ID));
	}

	return { packets, bursts, edgeTraces, nextSeed };
}
