<script setup>
import { computed, onBeforeUnmount, ref, watch } from 'vue';
import { buildGraphAnimations, buildGraphModel, normalizeGraphSnapshot } from '../visualization/buildGraphModel';

const props = defineProps({
	items: {
		type: Array,
		default: () => [],
	},
	conversations: {
		type: Array,
		default: () => [],
	},
	selectedConversationId: {
		type: String,
		default: null,
	},
	selectedConversationModeId: {
		type: String,
		default: 'programming',
	},
	selectedProfileModel: {
		type: String,
		default: '',
	},
	teamMembers: {
		type: Array,
		default: () => [],
	},
	agentActivities: {
		type: Array,
		default: () => [],
	},
});

const ZOOM_MIN = 0.6;
const ZOOM_MAX = 1.8;
const ZOOM_STEP = 0.1;
const WHEEL_ZOOM_STEP = 0.01;

const model = ref({
	viewBox: { width: 1200, height: 720 },
	nodes: [],
	edges: [],
	meta: {
		modeLabel: 'Programming',
		targetLabel: 'Assistant',
	},
	isEmpty: true,
});
const packets = ref([]);
const bursts = ref([]);
const edgeTraces = ref([]);
const previousSnapshot = ref(null);
const zoom = ref(1);
const pan = ref({ x: 0, y: 0 });
const isPanning = ref(false);
const graphCanvasEl = ref(null);
const cleanupHandles = new Set();
const pointerPanState = {
	pointerId: null,
	startClientX: 0,
	startClientY: 0,
	startPanX: 0,
	startPanY: 0,
};
let animationSeed = 0;

const snapshotInput = computed(() => ({
	items: props.items,
	conversations: props.conversations,
	selectedConversationId: props.selectedConversationId,
	selectedConversationModeId: props.selectedConversationModeId,
	selectedProfileModel: props.selectedProfileModel,
	teamMembers: props.teamMembers,
	agentActivities: props.agentActivities,
}));

const zoomPercent = computed(() => `${Math.round(zoom.value * 100)}%`);
const stageTransform = computed(() => {
	const { width, height } = model.value.viewBox;
	const centerX = width / 2;
	const centerY = height / 2;
	return `translate(${centerX + pan.value.x} ${centerY + pan.value.y}) scale(${zoom.value}) translate(${-centerX} ${-centerY})`;
});
const canZoomOut = computed(() => zoom.value > ZOOM_MIN + 0.001);
const canZoomIn = computed(() => zoom.value < ZOOM_MAX - 0.001);
const canvasClasses = computed(() => ({
	'pannable-cue': !isPanning.value,
	panning: isPanning.value,
}));
const nodeLookup = computed(() => new Map(model.value.nodes.map((item) => [item.id, item])));
const edgeLookup = computed(() => new Map(model.value.edges.map((item) => [item.id, item])));
const satelliteLookup = computed(() => {
	const map = new Map();
	for (const node of model.value.nodes) {
		for (const satellite of node.satellites || []) {
			map.set(satellite.id, {
				x: node.x + satellite.dx,
				y: node.y + satellite.dy,
				radius: 24,
			});
		}
	}

	return map;
});

const statusCounts = computed(() => {
	const counters = {
		running: 0,
		awaitingapproval: 0,
		failed: 0,
	};

	for (const node of model.value.nodes) {
		if (node.status in counters) {
			counters[node.status] += 1;
		}
		for (const satellite of node.satellites || []) {
			if (satellite.status in counters) {
				counters[satellite.status] += 1;
			}
		}
	}

	return [
		{ key: 'running', label: '运行', value: counters.running },
		{ key: 'awaitingapproval', label: '等待', value: counters.awaitingapproval },
		{ key: 'failed', label: '异常', value: counters.failed },
	];
});

const visibleEdgeTraces = computed(() =>
	edgeTraces.value
		.map((trace) => {
			const edge = edgeLookup.value.get(trace.edgeId);
			if (!edge) {
				return null;
			}

			return {
				...trace,
				path: edge.path,
			};
		})
		.filter(Boolean)
);

const visibleBursts = computed(() =>
	bursts.value
		.map((burst) => {
			if (burst.targetType === 'satellite') {
				const satellite = satelliteLookup.value.get(burst.targetId);
				if (satellite) {
					return {
						...burst,
						x: satellite.x,
						y: satellite.y,
						radius: satellite.radius,
					};
				}
			}

			const node = nodeLookup.value.get(burst.targetId) || nodeLookup.value.get(burst.fallbackNodeId);
			if (!node) {
				return null;
			}

			return {
				...burst,
				x: node.x,
				y: node.y,
				radius: node.radius + 18,
			};
		})
		.filter(Boolean)
);

watch(
	snapshotInput,
	(value) => {
		const nextSnapshot = normalizeGraphSnapshot(value);
		const nextModel = buildGraphModel(nextSnapshot);
		const animations = buildGraphAnimations(previousSnapshot.value, nextSnapshot, nextModel, animationSeed);
		animationSeed = animations.nextSeed;
		model.value = nextModel;
		pushTransientEntries(packets, animations.packets);
		pushTransientEntries(bursts, animations.bursts);
		pushTransientEntries(edgeTraces, animations.edgeTraces || []);
		previousSnapshot.value = nextSnapshot;
	},
	{ immediate: true }
);

function pushTransientEntries(targetRef, entries) {
	for (const entry of entries) {
		targetRef.value = [...targetRef.value, entry];
		const handle = window.setTimeout(() => {
			targetRef.value = targetRef.value.filter((item) => item.id !== entry.id);
			cleanupHandles.delete(handle);
		}, entry.durationMs + 240);
		cleanupHandles.add(handle);
	}
}

function getLabelLines(label) {
	const value = String(label || '').trim();
	if (!value) {
		return ['Agent'];
	}

	if (value.length <= 12) {
		return [value];
	}

	const words = value.split(/\s+/).filter(Boolean);
	if (words.length >= 2) {
		const firstLine = [];
		let firstLineLength = 0;
		for (const word of words) {
			const projectedLength = firstLineLength + word.length + (firstLine.length ? 1 : 0);
			if (firstLine.length && projectedLength > 12) {
				break;
			}

			firstLine.push(word);
			firstLineLength = projectedLength;
		}

		if (firstLine.length && firstLine.length < words.length) {
			return [firstLine.join(' '), words.slice(firstLine.length).join(' ')];
		}
	}

	const midpoint = Math.ceil(value.length / 2);
	return [value.slice(0, midpoint), value.slice(midpoint)];
}

function setZoom(nextZoom) {
	const clamped = Math.min(ZOOM_MAX, Math.max(ZOOM_MIN, Math.round(nextZoom * 100) / 100));
	zoom.value = clamped;
}

function zoomIn() {
	setZoom(zoom.value + ZOOM_STEP);
}

function zoomOut() {
	setZoom(zoom.value - ZOOM_STEP);
}

function onCanvasPointerDown(event) {
	if (!event.ctrlKey || event.button !== 0) {
		return;
	}

	event.preventDefault();
	pointerPanState.pointerId = event.pointerId;
	pointerPanState.startClientX = event.clientX;
	pointerPanState.startClientY = event.clientY;
	pointerPanState.startPanX = pan.value.x;
	pointerPanState.startPanY = pan.value.y;
	isPanning.value = true;
	graphCanvasEl.value?.setPointerCapture?.(event.pointerId);
}

function onCanvasPointerMove(event) {
	if (!isPanning.value || pointerPanState.pointerId !== event.pointerId) {
		return;
	}

	event.preventDefault();
	pan.value = {
		x: pointerPanState.startPanX + (event.clientX - pointerPanState.startClientX),
		y: pointerPanState.startPanY + (event.clientY - pointerPanState.startClientY),
	};
}

function endCanvasPan(event) {
	if (pointerPanState.pointerId !== event.pointerId) {
		return;
	}

	graphCanvasEl.value?.releasePointerCapture?.(event.pointerId);
	pointerPanState.pointerId = null;
	isPanning.value = false;
}

function onCanvasWheel(event) {
	if (!event.ctrlKey) {
		return;
	}

	event.preventDefault();
	const direction = event.deltaY > 0 ? -1 : 1;
	setZoom(zoom.value + direction * WHEEL_ZOOM_STEP);
}

function clearCleanupHandles() {
	for (const handle of cleanupHandles) {
		window.clearTimeout(handle);
	}
	cleanupHandles.clear();
}

onBeforeUnmount(() => {
	clearCleanupHandles();
});
</script>

<template>
	<div class="graph-view">
		<div ref="graphCanvasEl" class="graph-canvas" :class="canvasClasses" @pointerdown="onCanvasPointerDown"
			@pointermove="onCanvasPointerMove" @pointerup="endCanvasPan" @pointercancel="endCanvasPan"
			@lostpointercapture="endCanvasPan" @wheel="onCanvasWheel">
			<div class="graph-toolbar" role="toolbar" aria-label="缩放工具栏">
				<span class="graph-toolbar-label">缩放</span>
				<strong class="graph-toolbar-value">{{ zoomPercent }}</strong>
				<div class="graph-toolbar-divider" aria-hidden="true"></div>
				<button type="button" class="graph-toolbar-btn" :disabled="!canZoomOut" aria-label="缩小"
					@click="zoomOut">
					<svg viewBox="0 0 16 16" aria-hidden="true">
						<path d="M 3 8 H 13"></path>
					</svg>
				</button>
				<button type="button" class="graph-toolbar-btn" :disabled="!canZoomIn" aria-label="放大" @click="zoomIn">
					<svg viewBox="0 0 16 16" aria-hidden="true">
						<path d="M 3 8 H 13"></path>
						<path d="M 8 3 V 13"></path>
					</svg>
				</button>
			</div>

			<div class="graph-hud" aria-hidden="true">
				<div class="graph-hud-target">
					<span class="graph-hud-label">TARGET</span>
					<strong>{{ model.meta.targetLabel }}</strong>
				</div>
				<div class="graph-hud-stats">
					<div v-for="item in statusCounts" :key="item.key" :class="['graph-hud-stat', item.key]">
						<span>{{ item.label }}</span>
						<strong>{{ item.value }}</strong>
					</div>
				</div>
			</div>

			<svg class="graph-svg" :viewBox="`0 0 ${model.viewBox.width} ${model.viewBox.height}`"
				preserveAspectRatio="xMidYMid meet" role="img" aria-label="Agent runtime visualization">
				<defs>
					<pattern id="graph-grid-small" width="28" height="28" patternUnits="userSpaceOnUse">
						<path d="M 28 0 L 0 0 0 28" class="graph-grid-small"></path>
					</pattern>
					<pattern id="graph-grid-large" width="112" height="112" patternUnits="userSpaceOnUse">
						<rect width="112" height="112" fill="url(#graph-grid-small)"></rect>
						<path d="M 112 0 L 0 0 0 112" class="graph-grid-large"></path>
					</pattern>
					<pattern id="graph-checker" width="40" height="40" patternUnits="userSpaceOnUse">
						<rect width="40" height="40" class="graph-checker-base"></rect>
						<rect width="20" height="20" class="graph-checker-accent"></rect>
						<rect x="20" y="20" width="20" height="20" class="graph-checker-accent"></rect>
					</pattern>
					<radialGradient id="graph-surface-gradient" cx="52%" cy="18%" r="88%">
						<stop offset="0%" class="graph-surface-stop graph-surface-stop-1"></stop>
						<stop offset="58%" class="graph-surface-stop graph-surface-stop-2"></stop>
						<stop offset="100%" class="graph-surface-stop graph-surface-stop-3"></stop>
					</radialGradient>
				</defs>

				<g class="graph-stage" :transform="stageTransform">
					<rect x="0" y="0" :width="model.viewBox.width" :height="model.viewBox.height" class="graph-checker">
					</rect>
					<rect x="0" y="0" :width="model.viewBox.width" :height="model.viewBox.height" class="graph-surface">
					</rect>
					<rect x="0" y="0" :width="model.viewBox.width" :height="model.viewBox.height" class="graph-grid">
					</rect>

					<g class="graph-edge-layer">
						<path v-for="edge in model.edges" :key="edge.id" :d="edge.path"
							:class="['graph-edge', edge.tone, { active: edge.active }]"></path>
						<path v-for="trace in visibleEdgeTraces" :key="trace.id" :d="trace.path"
							:pathLength="100" :class="['graph-edge-trace', trace.tone]">
							<animate attributeName="stroke-dashoffset" from="100" to="0" :dur="`${trace.durationMs}ms`"
								repeatCount="1" fill="freeze"></animate>
							<animate attributeName="opacity" values="0;1;1;0" keyTimes="0;0.12;0.82;1"
								:dur="`${trace.durationMs}ms`" repeatCount="1"></animate>
						</path>
					</g>

					<g class="graph-packet-layer">
						<g v-for="packet in packets" :key="packet.id"
							:class="['graph-packet-shell', packet.kind, packet.status]">
							<circle class="graph-packet-glow" :r="packet.radius + 3">
								<animate attributeName="opacity" values="0;1;1;0" keyTimes="0;0.08;0.92;1"
									:dur="`${packet.durationMs}ms`" repeatCount="1"></animate>
								<animateMotion :dur="`${packet.durationMs}ms`" repeatCount="1" :path="packet.path"
									fill="freeze"></animateMotion>
							</circle>
							<circle class="graph-packet-core" :r="packet.radius">
								<animate attributeName="opacity" values="0;1;1;0" keyTimes="0;0.08;0.92;1"
									:dur="`${packet.durationMs}ms`" repeatCount="1"></animate>
								<animateMotion :dur="`${packet.durationMs}ms`" repeatCount="1" :path="packet.path"
									fill="freeze"></animateMotion>
							</circle>
						</g>
					</g>

					<g class="graph-node-layer">
						<g v-for="node in model.nodes" :key="node.id"
							:class="['graph-node', node.kind, node.status, { active: node.isActive, selected: node.isSelected }]"
							:transform="`translate(${node.x} ${node.y})`">
							<title>{{ `${node.label} (${node.statusText})` }}</title>
							<circle class="graph-node-halo" :r="node.radius + 22"></circle>
							<circle class="graph-node-outer" :r="node.radius"></circle>
							<circle class="graph-node-inner" :r="node.radius - 9"></circle>

							<g class="graph-node-icon" aria-hidden="true">
								<template v-if="node.icon === 'human' || node.icon === 'agent'">
									<circle cx="0" cy="-10" r="10.5" class="graph-icon-stroke"></circle>
									<path d="M -18 18 C -16 3 16 3 18 18" class="graph-icon-stroke"></path>
								</template>
								<template v-else-if="node.icon === 'coordinator'">
									<circle cx="0" cy="-15" r="7.5" class="graph-icon-stroke"></circle>
									<circle cx="-14" cy="10" r="6.5" class="graph-icon-stroke"></circle>
									<circle cx="14" cy="10" r="6.5" class="graph-icon-stroke"></circle>
									<path d="M 0 -7 V 3" class="graph-icon-stroke"></path>
									<path d="M -14 3 H 14" class="graph-icon-stroke"></path>
									<path d="M -14 3 V 4" class="graph-icon-stroke"></path>
									<path d="M 14 3 V 4" class="graph-icon-stroke"></path>
								</template>
								<template v-else>
									<path d="M -17 -4 -6 -15" class="graph-icon-stroke"></path>
									<path d="M -17 20 -6 9" class="graph-icon-stroke"></path>
									<path d="M 17 -4 6 -15" class="graph-icon-stroke"></path>
									<path d="M 17 20 6 9" class="graph-icon-stroke"></path>
									<path d="M 2 -18 -6 22" class="graph-icon-stroke"></path>
								</template>
							</g>

							<g v-for="satellite in node.satellites" :key="satellite.id"
								:class="['graph-satellite', satellite.status]"
								:transform="`translate(${satellite.dx} ${satellite.dy})`">
								<title>{{ `${satellite.title}: ${satellite.summary}` }}</title>
								<circle class="graph-satellite-ring" r="15"></circle>
								<circle class="graph-satellite-core" r="10.5"></circle>
								<text class="graph-satellite-label" y="4.2">{{ satellite.label }}</text>
							</g>

							<text class="graph-node-label" :y="node.radius + 28">
								<tspan v-for="(line, index) in getLabelLines(node.label)"
									:key="`${node.id}-label-${index}`" x="0" :dy="index === 0 ? 0 : 15">
									{{ line }}
								</tspan>
							</text>
							<text class="graph-node-status" :y="node.radius + 54">{{ node.statusText }}</text>
						</g>
					</g>

					<g class="graph-burst-layer">
						<g v-for="burst in visibleBursts" :key="burst.id" class="graph-burst-shell"
							:transform="`translate(${burst.x} ${burst.y})`">
							<circle cx="0" cy="0" :r="burst.radius" :class="['graph-burst', burst.kind, burst.status]">
							</circle>
						</g>
					</g>
				</g>
			</svg>

			<div v-if="model.isEmpty" class="graph-empty-hint">
				发送一条消息后，Human 到 Agent 的运行流向会在这里持续可视化展示。
			</div>
		</div>
	</div>
</template>
