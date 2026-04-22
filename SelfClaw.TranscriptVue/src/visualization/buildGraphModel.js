export const GRAPH_VIEWBOX = { width: 1200, height: 720 };

const HUMAN_ID = 'human';
const PROGRAMMING_AGENT_ID = 'programming-agent';
const FALLBACK_COORDINATOR_ID = 'team-coordinator';
const NODE_RADIUS = 54;
const MAX_SATELLITES_PER_AGENT = 3;

function lower(value) {
	return String(value || '').trim().toLowerCase();
}

function isRunningStatus(status) {
	return status === 'streaming' || status === 'running';
}

function isSelectedConversationDirect(snapshot) {
	const selectedConversation = getSelectedConversation(snapshot);
	return Boolean(selectedConversation?.boundAgentId);
}

function getSelectedConversation(snapshot) {
	return snapshot.conversations.find((item) => item.id === snapshot.selectedConversationId) || null;
}

function getCoordinator(snapshot) {
	if (!snapshot.teamMembers.length) {
		return null;
	}

	return snapshot.teamMembers.find((item) => {
		const title = lower(item.title);
		const summary = lower(item.summary);
		return title === 'coordinator' || summary === 'coordinator';
	}) || snapshot.teamMembers[0];
}

function getCoordinatorId(snapshot) {
	return getCoordinator(snapshot)?.id || FALLBACK_COORDINATOR_ID;
}

function getHumanTargetAgentId(snapshot) {
	if (snapshot.modeId !== 'team') {
		return PROGRAMMING_AGENT_ID;
	}

	const selectedConversation = getSelectedConversation(snapshot);
	return selectedConversation?.boundAgentId || getCoordinatorId(snapshot);
}

function getMessageAgentId(message, snapshot) {
	if (message?.agentId) {
		return message.agentId;
	}

	if (snapshot.modeId !== 'team') {
		return PROGRAMMING_AGENT_ID;
	}

	const selectedConversation = getSelectedConversation(snapshot);
	return selectedConversation?.boundAgentId || getCoordinatorId(snapshot);
}

function getActivityOwnerAgentId(activity, snapshot) {
	if (activity?.ownerAgentId) {
		return activity.ownerAgentId;
	}

	if (snapshot.modeId !== 'team') {
		return PROGRAMMING_AGENT_ID;
	}

	const selectedConversation = getSelectedConversation(snapshot);
	return selectedConversation?.boundAgentId || getCoordinatorId(snapshot);
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

function getLatestAssistantMessageForAgent(snapshot, agentId) {
	for (let index = snapshot.items.length - 1; index >= 0; index -= 1) {
		const item = snapshot.items[index];
		if (item.role !== 'assistant') {
			continue;
		}

		if (getMessageAgentId(item, snapshot) === agentId) {
			return item;
		}
	}

	return null;
}

function hasAssistantRunningForAgent(snapshot, agentId) {
	return snapshot.items.some((item) => {
		if (item.role !== 'assistant') {
			return false;
		}

		if (getMessageAgentId(item, snapshot) !== agentId) {
			return false;
		}

		return Boolean(item.isThinking) || isRunningStatus(item.status);
	});
}

function getToolStatusesForAgent(snapshot, agentId) {
	return snapshot.agentActivities
		.filter((item) => item.kind === 'tool' && getActivityOwnerAgentId(item, snapshot) === agentId)
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

	const normalizedLabel = lower(label);
	const normalizedSummary = lower(summary);
	if (normalizedLabel === 'coordinator' || normalizedSummary === 'coordinator') {
		return 'coordinator';
	}

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
	const toolStatuses = getToolStatusesForAgent(snapshot, PROGRAMMING_AGENT_ID);
	let status = 'idle';

	if (toolStatuses.includes('awaitingapproval')) {
		status = 'awaitingapproval';
	} else if (toolStatuses.includes('running') || hasAssistantRunningForAgent(snapshot, PROGRAMMING_AGENT_ID)) {
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
		x: 790,
		y: 250,
		status,
		selected: true,
	});
}

function deriveTeamNodeStatus(member, snapshot) {
	const agentId = member.id;
	const toolStatuses = getToolStatusesForAgent(snapshot, agentId);
	if (toolStatuses.includes('awaitingapproval')) {
		return 'awaitingapproval';
	}

	if (toolStatuses.includes('running') || hasAssistantRunningForAgent(snapshot, agentId)) {
		return 'running';
	}

	switch (member.status) {
		case 'running':
			return 'running';
		case 'failed':
			return 'failed';
		case 'completed':
			return 'completed';
		default:
			return 'idle';
	}
}

function buildTeamWorkerPositions(count) {
	if (count <= 0) {
		return [];
	}

	if (count <= 4) {
		const startX = 310;
		const endX = 1010;
		return Array.from({ length: count }, (_, index) => {
			const progress = count === 1 ? 0.5 : index / (count - 1);
			return {
				x: startX + (endX - startX) * progress,
				y: 520 - Math.abs(progress - 0.5) * 36,
			};
		});
	}

	const firstRowCount = Math.ceil(count / 2);
	const secondRowCount = count - firstRowCount;
	const firstRow = buildTeamWorkerPositions(firstRowCount).map((item) => ({ ...item, y: item.y - 54 }));
	const secondRow = buildTeamWorkerPositions(secondRowCount).map((item) => ({ ...item, y: item.y + 68 }));
	return [...firstRow, ...secondRow];
}

function buildProgrammingGraph(snapshot) {
	const humanTargetId = getHumanTargetAgentId(snapshot);
	const human = buildNode({
		id: HUMAN_ID,
		kind: 'human',
		label: 'Human',
		summary: 'You',
		x: 150,
		y: 128,
		status: 'idle',
	});
	const agent = deriveProgrammingNode(snapshot);
	agent.isSelected = humanTargetId === agent.id;
	return {
		nodes: [human, agent],
		targetAgentId: humanTargetId,
		meta: {
			modeLabel: 'Programming',
			targetLabel: agent.label,
		},
	};
}

function buildTeamGraph(snapshot) {
	const coordinator = getCoordinator(snapshot);
	const coordinatorId = getCoordinatorId(snapshot);
	const targetAgentId = getHumanTargetAgentId(snapshot);
	const human = buildNode({
		id: HUMAN_ID,
		kind: 'human',
		label: 'Human',
		summary: 'You',
		x: 150,
		y: 128,
		status: 'idle',
	});
	const coordinatorNode = buildNode({
		id: coordinatorId,
		kind: 'team-agent',
		label: coordinator?.title || 'Coordinator',
		summary: coordinator?.summary || 'Coordinator',
		x: 650,
		y: 224,
		status: coordinator ? deriveTeamNodeStatus(coordinator, snapshot) : 'idle',
		selected: targetAgentId === coordinatorId,
	});

	const workers = snapshot.teamMembers.filter((item) => item.id !== coordinatorId);
	const workerPositions = buildTeamWorkerPositions(workers.length);
	const workerNodes = workers.map((member, index) =>
		buildNode({
			id: member.id,
			kind: 'team-agent',
			label: member.title,
			summary: member.summary,
			x: workerPositions[index]?.x || 650,
			y: workerPositions[index]?.y || 520,
			status: deriveTeamNodeStatus(member, snapshot),
			selected: targetAgentId === member.id,
		})
	);

	return {
		nodes: [human, coordinatorNode, ...workerNodes],
		targetAgentId,
		meta: {
			modeLabel: 'Team',
			targetLabel:
				workerNodes.find((item) => item.id === targetAgentId)?.label ||
				(targetAgentId === coordinatorNode.id ? coordinatorNode.label : 'Coordinator'),
		},
	};
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
	const normalized = lower(title);
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

	if (normalized.includes('export')) {
		return 'EX';
	}

	return 'TL';
}

function buildToolSatellite(node, activity, index) {
	const side = node.x > GRAPH_VIEWBOX.width * 0.68 ? -1 : 1;
	const offsets = [
		{ x: 86 * side, y: -46 },
		{ x: 102 * side, y: 0 },
		{ x: 86 * side, y: 46 },
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

function attachToolSatellites(snapshot, nodesById) {
	const toolBuckets = new Map();

	for (const activity of snapshot.agentActivities) {
		if (activity.kind !== 'tool') {
			continue;
		}

		const ownerAgentId = getActivityOwnerAgentId(activity, snapshot);
		if (!nodesById.has(ownerAgentId)) {
			continue;
		}

		const bucket = toolBuckets.get(ownerAgentId) || [];
		if (bucket.length >= MAX_SATELLITES_PER_AGENT) {
			continue;
		}

		bucket.push(activity);
		toolBuckets.set(ownerAgentId, bucket);
	}

	for (const [ownerAgentId, activities] of toolBuckets.entries()) {
		const node = nodesById.get(ownerAgentId);
		node.satellites = activities.map((activity, index) => buildToolSatellite(node, activity, index));
		if (node.satellites.some((item) => item.status === 'awaitingapproval')) {
			node.status = 'awaitingapproval';
			node.statusText = formatStatusText(node.status);
			node.isActive = true;
		}
	}
}

function buildEdges(snapshot, nodes, targetAgentId) {
	const nodesById = new Map(nodes.map((item) => [item.id, item]));
	const human = nodesById.get(HUMAN_ID);
	if (!human) {
		return [];
	}

	if (snapshot.modeId !== 'team') {
		const agent = nodesById.get(PROGRAMMING_AGENT_ID);
		if (!agent) {
			return [];
		}

		return [
			buildEdge(
				'human:programming',
				human,
				agent,
				'primary',
				targetAgentId === agent.id || agent.isActive
			),
		];
	}

	const coordinatorId = getCoordinatorId(snapshot);
	const coordinator = nodesById.get(coordinatorId);
	if (!coordinator) {
		return [];
	}

	const edges = [
		buildEdge(
			'human:coordinator',
			human,
			coordinator,
			'primary',
			targetAgentId === coordinator.id || coordinator.isActive
		),
	];

	nodes
		.filter((item) => item.id !== HUMAN_ID && item.id !== coordinator.id)
		.forEach((item) => {
			edges.push(
				buildEdge(
					`${coordinator.id}:${item.id}`,
					coordinator,
					item,
					'secondary',
					item.isActive || item.status === 'completed' || item.status === 'failed'
				)
			);
		});

	if (targetAgentId && targetAgentId !== coordinator.id && nodesById.has(targetAgentId)) {
		edges.push(
			buildEdge(
				`human:${targetAgentId}:direct`,
				human,
				nodesById.get(targetAgentId),
				'direct',
				true
			)
		);
	}

	return edges;
}

function buildGraphMeta(snapshot, targetAgentId, nodesById) {
	return {
		modeLabel: snapshot.modeId === 'team' ? 'Team' : 'Programming',
		targetLabel: nodesById.get(targetAgentId)?.label || 'Assistant',
	};
}

export function normalizeGraphSnapshot(raw = {}) {
	return {
		modeId: raw.selectedConversationModeId === 'team' ? 'team' : 'programming',
		items: Array.isArray(raw.items) ? raw.items.map((item) => ({ ...item })) : [],
		conversations: Array.isArray(raw.conversations) ? raw.conversations.map((item) => ({ ...item })) : [],
		selectedConversationId: raw.selectedConversationId || null,
		selectedProfileModel: raw.selectedProfileModel || '',
		teamMembers: Array.isArray(raw.teamMembers) ? raw.teamMembers.map((item) => ({ ...item })) : [],
		agentActivities: Array.isArray(raw.agentActivities) ? raw.agentActivities.map((item) => ({ ...item })) : [],
	};
}

export function buildGraphModel(snapshot) {
	const baseGraph = snapshot.modeId === 'team' ? buildTeamGraph(snapshot) : buildProgrammingGraph(snapshot);
	const nodesById = new Map(baseGraph.nodes.map((item) => [item.id, item]));
	attachToolSatellites(snapshot, nodesById);
	const edges = buildEdges(snapshot, baseGraph.nodes, baseGraph.targetAgentId);

	return {
		viewBox: GRAPH_VIEWBOX,
		nodes: baseGraph.nodes,
		edges,
		targetAgentId: baseGraph.targetAgentId,
		meta: buildGraphMeta(snapshot, baseGraph.targetAgentId, nodesById),
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
		durationMs: kind === 'signal' ? 820 : 1080,
		path: buildPath(sourceNode, targetNode),
		radius: kind === 'signal' ? 4.5 : 5.5,
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

function resolveAssistantFlow(agentId, snapshot) {
	if (snapshot.modeId !== 'team') {
		return { sourceId: agentId, targetId: HUMAN_ID };
	}

	if (isSelectedConversationDirect(snapshot)) {
		return { sourceId: agentId, targetId: HUMAN_ID };
	}

	const coordinatorId = getCoordinatorId(snapshot);
	if (agentId === coordinatorId) {
		return { sourceId: agentId, targetId: HUMAN_ID };
	}

	return { sourceId: agentId, targetId: coordinatorId };
}

function hasAssistantStarted(previousMessage, nextMessage) {
	const previousRunning = Boolean(previousMessage?.isThinking) || isRunningStatus(previousMessage?.status);
	const nextRunning = Boolean(nextMessage?.isThinking) || isRunningStatus(nextMessage?.status);
	return nextRunning && !previousRunning;
}

export function buildGraphAnimations(previousSnapshot, nextSnapshot, graphModel, seed = 0) {
	if (
		!previousSnapshot ||
		previousSnapshot.selectedConversationId !== nextSnapshot.selectedConversationId ||
		previousSnapshot.modeId !== nextSnapshot.modeId
	) {
		return { packets: [], bursts: [], nextSeed: seed };
	}

	let nextSeed = seed;
	const packets = [];
	const bursts = [];
	const nodesById = new Map(graphModel.nodes.map((item) => [item.id, item]));
	const previousMessagesById = new Map(previousSnapshot.items.map((item) => [item.id, item]));
	const previousActivitiesById = new Map(previousSnapshot.agentActivities.map((item) => [item.id, item]));
	const previousMembersById = new Map(previousSnapshot.teamMembers.map((item) => [item.id, item]));

	const humanTargetId = getHumanTargetAgentId(nextSnapshot);
	for (const message of nextSnapshot.items) {
		const previousMessage = previousMessagesById.get(message.id);
		if (message.role === 'user' && !previousMessage) {
			nextSeed += 1;
			const packet = buildPacket(nextSeed, nodesById.get(HUMAN_ID), nodesById.get(humanTargetId), 'user');
			if (packet) {
				packets.push(packet);
			}
			continue;
		}

		if (message.role !== 'assistant') {
			continue;
		}

		const agentId = getMessageAgentId(message, nextSnapshot);
		if (hasAssistantStarted(previousMessage, message)) {
			nextSeed += 1;
			bursts.push(buildBurst(nextSeed, 'node', agentId, 'running'));
		}

		if (message.status === 'completed' && previousMessage?.status !== 'completed') {
			nextSeed += 1;
			bursts.push(buildBurst(nextSeed, 'node', agentId, 'completed'));
			const flow = resolveAssistantFlow(agentId, nextSnapshot);
			nextSeed += 1;
			const packet = buildPacket(nextSeed, nodesById.get(flow.sourceId), nodesById.get(flow.targetId), 'assistant', 'completed');
			if (packet) {
				packets.push(packet);
			}
		}

		if (message.status === 'failed' && previousMessage?.status !== 'failed') {
			nextSeed += 1;
			bursts.push(buildBurst(nextSeed, 'node', agentId, 'failed'));
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

		const ownerAgentId = getActivityOwnerAgentId(activity, nextSnapshot);
		nextSeed += 1;
		bursts.push(buildBurst(nextSeed, 'satellite', activity.id, 'tool', activity.status, ownerAgentId));
	}

	if (nextSnapshot.modeId === 'team') {
		const coordinatorId = getCoordinatorId(nextSnapshot);
		for (const member of nextSnapshot.teamMembers) {
			const previousMember = previousMembersById.get(member.id);
			if (!previousMember || previousMember.status === member.status) {
				continue;
			}

			nextSeed += 1;
			bursts.push(buildBurst(nextSeed, 'node', member.id, 'status', member.status));

			if (member.id === coordinatorId) {
				continue;
			}

			nextSeed += 1;
			const packet = buildPacket(nextSeed, nodesById.get(coordinatorId), nodesById.get(member.id), 'signal', member.status);
			if (packet) {
				packets.push(packet);
			}
		}
	}

	return { packets, bursts, nextSeed };
}
