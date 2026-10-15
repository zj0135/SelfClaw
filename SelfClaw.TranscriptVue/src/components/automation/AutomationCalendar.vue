<script setup>
import { computed, onMounted, onUnmounted, ref } from 'vue';
import FullCalendar from '@fullcalendar/vue3';
import dayGridPlugin from '@fullcalendar/vue3/daygrid';
import timeGridPlugin from '@fullcalendar/vue3/timegrid';
import listPlugin from '@fullcalendar/vue3/list';
import interactionPlugin from '@fullcalendar/vue3/interaction';
import classicThemePlugin from '@fullcalendar/vue3/themes/classic';
import zhCnLocale from '@fullcalendar/vue3/locales/zh-cn';
import {
	ArrowRight,
	ChevronLeft,
	ChevronRight,
	Clock,
	History,
	Pencil,
	Sparkles,
	StickyNote,
} from 'lucide-vue-next';
import AutomationEventChip from './AutomationEventChip.vue';
import {
	formatDuration,
	formatLocalDateTime,
	formatScheduleRule,
	parseCalendarTitle,
	runStatusLabel,
	taskKindLabel,
	toCalendarEvents,
} from '../../renderers/automationEvents';

const props = defineProps({
	events: { type: Array, default: () => [] },
	tasks: { type: Array, default: () => [] },
	timeZone: { type: String, default: '' },
	truncated: { type: Boolean, default: false },
});

const emit = defineEmits(['range-change', 'event-click', 'date-click', 'event-drop']);

const calendarRootRef = ref(null);
const calendarRef = ref(null);
const currentTitle = ref('');
const currentView = ref('dayGridMonth');
const isTodayInView = ref(true);

const popoverState = ref({
	visible: false,
	event: null,
	task: null,
	top: 0,
	left: 0,
	placement: 'bottom',
});

let hideTimer = null;

function clearHideTimer() {
	if (hideTimer) {
		clearTimeout(hideTimer);
		hideTimer = null;
	}
}

function scheduleHidePopover() {
	clearHideTimer();
	hideTimer = setTimeout(() => {
		popoverState.value.visible = false;
	}, 160);
}

function hidePopoverImmediately() {
	clearHideTimer();
	popoverState.value.visible = false;
}

function showPopover(eventData, targetElement) {
	if (!eventData || !calendarRootRef.value) return;
	clearHideTimer();

	const targetEl = targetElement instanceof Element ? targetElement : targetElement?.target;
	if (!targetEl || !calendarRootRef.value.contains(targetEl)) return;

	const containerRect = calendarRootRef.value.getBoundingClientRect();
	const targetRect = targetEl.getBoundingClientRect();

	const POPOVER_WIDTH = 300;
	let left = targetRect.left - containerRect.left + (targetRect.width / 2) - (POPOVER_WIDTH / 2);
	const padding = 12;
	left = Math.max(padding, Math.min(left, containerRect.width - POPOVER_WIDTH - padding));

	const targetTopRel = targetRect.top - containerRect.top;
	const targetBottomRel = targetRect.bottom - containerRect.top;

	const showAbove = targetBottomRel + 220 > containerRect.height;
	const top = showAbove ? targetTopRel - 8 : targetBottomRel + 8;
	const placement = showAbove ? 'top' : 'bottom';

	const matchedTask = props.tasks.find((t) => t.id === eventData.taskId) || null;

	popoverState.value = {
		visible: true,
		event: eventData,
		task: matchedTask,
		top,
		left,
		placement,
	};
}

function handleChipHover(eventProps, mouseEvent) {
	showPopover(eventProps, mouseEvent?.currentTarget || mouseEvent?.target);
}

function handleChipLeave() {
	scheduleHidePopover();
}

function handlePopoverClick() {
	if (popoverState.value.event) {
		emit('event-click', { ...popoverState.value.event });
	}
	hidePopoverImmediately();
}

function getBadgeKindText(event, task) {
	if (event.kind === 'run') return '运行历史';
	if (event.kind === 'todo') return '待办事项';
	if (task?.kind) return taskKindLabel(task.kind);
	if (event.taskKind) return taskKindLabel(event.taskKind);
	return '自动化';
}

function getBadgeStatusText(event, task) {
	if (event.kind === 'run') return runStatusLabel(event.status);
	if (event.kind === 'todo') {
		if (event.isOverdue) return '已逾期';
		return event.status === 'done' ? '已完成' : '待处理';
	}
	if (task && task.isEnabled === false) return '已停用';
	return '计划执行';
}

function getExecutionTimeInfo(event, task) {
	if (event.kind === 'run') {
		const timeFormatted = formatLocalDateTime(event.atUtc, props.timeZone);
		const dur = event.durationMs ? formatDuration(event.durationMs) : '';
		return {
			label: '执行时间',
			time: timeFormatted,
			sub: dur ? `（耗时 ${dur}）` : '',
		};
	}
	if (event.kind === 'todo') {
		const isDateOnly = Boolean(event.allDay);
		const timeFormatted = isDateOnly ? event.atUtc.slice(0, 10) : formatLocalDateTime(event.atUtc, props.timeZone);
		return {
			label: isDateOnly ? '截止日期' : '截止时间',
			time: timeFormatted,
			sub: event.isOverdue ? '（逾期）' : '',
		};
	}
	const timeFormatted = formatLocalDateTime(event.atUtc, props.timeZone);
	const rule = formatScheduleRule(task || { kind: event.taskKind });
	return {
		label: '执行时间',
		time: timeFormatted,
		sub: rule ? `（${rule}）` : '',
	};
}

const calendarEvents = computed(() => toCalendarEvents(props.events).events);

const viewOptions = [
	{ id: 'dayGridMonth', label: '月' },
	{ id: 'timeGridWeek', label: '周' },
	{ id: 'timeGridDay', label: '日' },
	{ id: 'listWeek', label: '日程' },
];

const parsedTitle = computed(() => parseCalendarTitle(currentTitle.value));

function getApi() {
	return calendarRef.value?.getApi();
}

function handlePrev() {
	getApi()?.prev();
}

function handleNext() {
	getApi()?.next();
}

function handleToday() {
	getApi()?.today();
}

function handleViewChange(viewId) {
	currentView.value = viewId;
	getApi()?.changeView(viewId);
}

const calendarOptions = computed(() => ({
	plugins: [classicThemePlugin, dayGridPlugin, timeGridPlugin, listPlugin, interactionPlugin],
	initialView: 'dayGridMonth',
	locale: zhCnLocale,
	height: '100%',
	expandRows: true,
	dayMaxEvents: 4,
	nowIndicator: true,
	selectable: true,
	selectMirror: true,
	headerToolbar: false,
	events: calendarEvents.value,
	eventClick: (info) => {
		hidePopoverImmediately();
		emit('event-click', { ...info.event.extendedProps });
	},
	eventMouseEnter: (info) => {
		showPopover(info.event.extendedProps, info.el);
	},
	eventMouseLeave: () => {
		scheduleHidePopover();
	},
	dateClick: (info) => {
		hidePopoverImmediately();
		emit('date-click', { date: info.date, dateStr: info.dateStr, allDay: info.allDay });
	},
	eventDrop: (info) => {
		hidePopoverImmediately();
		emit('event-drop', { event: { ...info.event.extendedProps }, start: info.event.start });
	},
	datesSet: (info) => {
		hidePopoverImmediately();
		currentTitle.value = info.view.title;
		currentView.value = info.view.type;
		const now = new Date();
		isTodayInView.value = now >= info.start && now < info.end;
		emit('range-change', { fromUtc: info.start.toISOString(), toUtc: info.end.toISOString() });
	},
}));

onMounted(() => {
	window.addEventListener('scroll', hidePopoverImmediately, true);
});

onUnmounted(() => {
	clearHideTimer();
	window.removeEventListener('scroll', hidePopoverImmediately, true);
});
</script>

<template>
	<section ref="calendarRootRef" class="automation-calendar">
		<!-- Awwwards-caliber Floating Editorial Control Bar -->
		<header class="ac-toolbar">
			<div class="ac-nav-cluster">
				<div class="ac-nav-arrows">
					<button type="button" class="ac-nav-btn" title="上一个周期" aria-label="上一个周期" @click="handlePrev">
						<ChevronLeft :size="15" :stroke-width="2.2" />
					</button>
					<button type="button" class="ac-nav-btn" title="下一个周期" aria-label="下一个周期" @click="handleNext">
						<ChevronRight :size="15" :stroke-width="2.2" />
					</button>
				</div>
				<button
					type="button"
					class="ac-today-btn"
					:class="{ 'is-current': isTodayInView }"
					:title="isTodayInView ? '已在当前日期' : '返回今天'"
					@click="handleToday"
				>
					<span class="ac-today-dot" :class="{ 'is-pulsing': !isTodayInView }" aria-hidden="true"></span>
					<span>今天</span>
				</button>
			</div>

			<div class="ac-title-cluster">
				<span class="ac-title-kicker">{{ parsedTitle.kicker }}</span>
				<h2 class="ac-title-main">{{ parsedTitle.title }}</h2>
			</div>

			<div class="ac-controls-cluster">
				<div class="ac-view-switcher" role="tablist" aria-label="视图切换">
					<button
						v-for="v in viewOptions"
						:key="v.id"
						type="button"
						role="tab"
						class="ac-view-tab"
						:class="{ 'is-active': currentView === v.id }"
						:aria-selected="currentView === v.id"
						@click="handleViewChange(v.id)"
					>
						{{ v.label }}
					</button>
				</div>
				<slot name="actions" />
			</div>
		</header>

		<div v-if="truncated" class="automation-calendar-hint" role="status">
			投影数量已达上限，仅显示前 300 条；缩小时间范围可查看更完整的结果。
		</div>

		<div class="ac-stage">
			<FullCalendar ref="calendarRef" :options="calendarOptions">
				<template #eventContent="arg">
					<AutomationEventChip
						:event="arg.event.extendedProps"
						:time-text="arg.timeText"
						@hover="handleChipHover(arg.event.extendedProps, $event)"
						@leave="handleChipLeave"
					/>
				</template>
			</FullCalendar>
		</div>

		<!-- Awwwards-Caliber Event Hover Popover -->
		<Transition name="ac-popover-fade">
			<div
				v-if="popoverState.visible && popoverState.event"
				class="ac-popover-anchor"
				:class="`is-${popoverState.placement}`"
				:style="{ top: `${popoverState.top}px`, left: `${popoverState.left}px` }"
				@mouseenter="clearHideTimer"
				@mouseleave="scheduleHidePopover"
			>
				<div class="ac-hover-popover" role="tooltip" aria-live="polite">
					<div class="ac-popover-head">
						<div class="ac-popover-badges">
							<span class="ac-popover-badge ac-badge-kind">
								{{ getBadgeKindText(popoverState.event, popoverState.task) }}
							</span>
							<span
								v-if="getBadgeStatusText(popoverState.event, popoverState.task)"
								class="ac-popover-badge ac-badge-status"
								:class="`is-${popoverState.event.status || 'default'}`"
							>
								{{ getBadgeStatusText(popoverState.event, popoverState.task) }}
							</span>
						</div>
						<h4 class="ac-popover-title">{{ popoverState.task?.title || popoverState.event.title }}</h4>
					</div>

					<div class="ac-popover-body">
						<div class="ac-popover-item">
							<div class="ac-popover-item-label">
								<Clock :size="12" class="ac-popover-icon" aria-hidden="true" />
								<span>{{ getExecutionTimeInfo(popoverState.event, popoverState.task).label }}</span>
							</div>
							<div class="ac-popover-item-val">
								<span class="ac-popover-time">
									{{ getExecutionTimeInfo(popoverState.event, popoverState.task).time }}
								</span>
								<span
									v-if="getExecutionTimeInfo(popoverState.event, popoverState.task).sub"
									class="ac-popover-sub"
								>
									{{ getExecutionTimeInfo(popoverState.event, popoverState.task).sub }}
								</span>
							</div>
						</div>

						<div class="ac-popover-item ac-popover-notes-item">
							<div class="ac-popover-item-label">
								<StickyNote :size="12" class="ac-popover-icon" aria-hidden="true" />
								<span>备注信息</span>
							</div>
							<div class="ac-popover-notes-val">
								<p v-if="popoverState.task?.notes" class="ac-popover-notes-text">
									{{ popoverState.task.notes }}
								</p>
								<p v-else class="ac-popover-notes-empty">暂无备注</p>
							</div>
						</div>

						<div v-if="popoverState.task?.prompt" class="ac-popover-item ac-popover-prompt-item">
							<div class="ac-popover-item-label">
								<Sparkles :size="12" class="ac-popover-icon" aria-hidden="true" />
								<span>任务指令</span>
							</div>
							<div class="ac-popover-prompt-val">
								<span class="ac-popover-prompt-text">{{ popoverState.task.prompt }}</span>
							</div>
						</div>
					</div>

					<footer class="ac-popover-footer" @click="handlePopoverClick">
						<div class="ac-popover-action">
							<component :is="popoverState.event.kind === 'run' ? History : Pencil" :size="12" aria-hidden="true" />
							<span>{{ popoverState.event.kind === 'run' ? '查看运行详情' : '打开任务设置' }}</span>
						</div>
						<ArrowRight :size="12" class="ac-popover-action-arrow" aria-hidden="true" />
					</footer>
				</div>
			</div>
		</Transition>
	</section>
</template>

<style scoped>
.automation-calendar {
	position: relative;
	container-type: inline-size;
	container-name: calendar;
	display: flex;
	min-width: 0;
	min-height: 0;
	flex: 1 1 auto;
	flex-direction: column;
	padding: 12px 16px 16px;
	/* FullCalendar v7 classic 主题 CSS 变量覆盖 */
	--fc-classic-primary: var(--accent);
	--fc-classic-primary-foreground: var(--accent-ink-on, #fff);
	--fc-classic-foreground: var(--text);
	--fc-classic-background: var(--panel);
	--fc-classic-border: color-mix(in srgb, var(--line-1) 75%, transparent);
	--fc-classic-border-style: 1px solid color-mix(in srgb, var(--line-1) 75%, transparent);
	--fc-classic-muted: var(--panel-muted);
	--fc-classic-muted-foreground: var(--muted);
	--fc-classic-faint: var(--panel-soft);
	--fc-classic-faint-foreground: var(--muted-soft);
	--fc-classic-today: transparent;
	--fc-classic-now: var(--accent);
	--fc-classic-highlight: color-mix(in srgb, var(--accent) 12%, transparent);
	--fc-classic-button: var(--panel);
	--fc-classic-button-foreground: var(--text);
	--fc-classic-button-border: var(--line-1);
	--fc-classic-button-strong: var(--accent);
	--fc-classic-button-strong-border: var(--accent);
	--fc-classic-event: transparent;
	--fc-classic-event-contrast: var(--text);
	--fc-classic-background-event: color-mix(in srgb, var(--muted) 16%, transparent);
	--fc-classic-background-event-opacity: 1;
	--fc-classic-background-event-foreground-opacity: 1;
}

/* ═══ Awwwards-level Editorial Control Bar ═══ */
.ac-toolbar {
	display: flex;
	align-items: center;
	justify-content: space-between;
	gap: 14px;
	padding: 4px 4px 14px;
	flex: none;
	min-width: 0;
}

.ac-controls-cluster {
	display: flex;
	align-items: center;
	gap: 8px;
	flex: none;
}

.ac-nav-cluster {
	display: flex;
	align-items: center;
	gap: 8px;
	flex: none;
}

.ac-nav-arrows {
	display: inline-flex;
	align-items: center;
	padding: 2px;
	border: 1px solid var(--line-1);
	border-radius: 9px;
	background: var(--panel);
	box-shadow: 0 1px 3px rgba(var(--shadow-ink), 0.04);
	flex: none;
}

.ac-nav-btn {
	display: grid;
	place-items: center;
	width: 28px;
	height: 28px;
	border: none;
	border-radius: 7px;
	background: transparent;
	color: var(--muted);
	cursor: pointer;
	flex: none;
	transition: background 0.15s, color 0.15s, transform 0.15s var(--ease-spring);
}

.ac-nav-btn:hover {
	background: var(--panel-hover);
	color: var(--text);
}

.ac-nav-btn:active {
	transform: scale(0.92);
}

.ac-today-btn {
	display: inline-flex;
	align-items: center;
	gap: 6px;
	height: 32px;
	padding: 0 12px;
	border: 1px solid var(--line-1);
	border-radius: 9px;
	background: var(--panel);
	color: var(--text);
	font-size: var(--fs-12);
	font-weight: 600;
	white-space: nowrap;
	flex: none;
	cursor: pointer;
	box-shadow: 0 1px 3px rgba(var(--shadow-ink), 0.04);
	transition: background 0.15s, border-color 0.15s, transform 0.15s var(--ease-spring);
}

.ac-today-btn:hover {
	background: var(--panel-hover);
	border-color: var(--line-2);
}

.ac-today-btn:active {
	transform: scale(0.96);
}

.ac-today-btn.is-current {
	color: var(--muted);
}

.ac-today-dot {
	width: 6px;
	height: 6px;
	border-radius: 50%;
	background: var(--muted);
	flex: none;
	transition: background 0.2s, box-shadow 0.2s;
}

.ac-today-dot.is-pulsing {
	background: var(--accent);
	box-shadow: 0 0 8px var(--accent);
	animation: ac-pulse 2s ease-in-out infinite;
}

@keyframes ac-pulse {
	0%, 100% {
		opacity: 1;
		transform: scale(1);
	}
	50% {
		opacity: 0.5;
		transform: scale(1.3);
	}
}

.ac-title-cluster {
	display: flex;
	flex-direction: column;
	align-items: center;
	justify-content: center;
	text-align: center;
	min-width: 0;
	flex: 1 1 auto;
	padding: 0 6px;
}

.ac-title-kicker {
	font-family: var(--font-code, monospace);
	font-size: var(--fs-10);
	font-weight: 600;
	letter-spacing: 0.22em;
	color: var(--muted);
	text-transform: uppercase;
	margin-bottom: 2px;
	white-space: nowrap;
}

.ac-title-main {
	margin: 0;
	font-family: var(--font-display);
	font-size: var(--fs-17);
	font-weight: 680;
	letter-spacing: -0.015em;
	color: var(--text);
	line-height: 1.2;
	white-space: nowrap;
	overflow: hidden;
	text-overflow: ellipsis;
	max-width: 100%;
}

.ac-view-switcher {
	display: inline-flex;
	padding: 3px;
	gap: 2px;
	border: 1px solid var(--line-1);
	border-radius: 10px;
	background: var(--panel-muted);
	box-shadow: inset 0 1px 2px rgba(var(--shadow-ink), 0.04);
	flex: none;
}

.ac-view-tab {
	height: 28px;
	padding: 0 12px;
	border: none;
	border-radius: 7px;
	background: transparent;
	color: var(--muted);
	font-size: var(--fs-12);
	font-weight: 600;
	white-space: nowrap;
	flex: none;
	cursor: pointer;
	transition: background 0.16s, color 0.16s, box-shadow 0.16s, transform 0.16s var(--ease-spring);
}

.ac-view-tab:hover:not(.is-active) {
	color: var(--text);
	background: color-mix(in srgb, var(--panel) 50%, transparent);
}

.ac-view-tab.is-active {
	background: var(--panel);
	color: var(--text);
	box-shadow: 0 1px 4px rgba(var(--shadow-ink), 0.1);
}

.ac-view-tab:active {
	transform: scale(0.96);
}

/* ═══ Responsive Toolbar Rules ═══ */
@container calendar (max-width: 580px) {
	.ac-toolbar {
		gap: 8px;
		padding: 4px 0 10px;
	}

	.ac-today-btn {
		height: 28px;
		padding: 0 8px;
		font-size: var(--fs-11);
	}

	.ac-nav-btn {
		width: 24px;
		height: 24px;
	}

	.ac-title-main {
		font-size: var(--fs-14);
	}

	.ac-title-kicker {
		font-size: 9px;
		letter-spacing: 0.14em;
	}

	.ac-view-switcher {
		padding: 2px;
		gap: 1px;
	}

	.ac-view-tab {
		height: 24px;
		padding: 0 7px;
		font-size: var(--fs-11);
	}
}

@container calendar (max-width: 440px) {
	.ac-toolbar {
		flex-wrap: wrap;
		justify-content: space-between;
		row-gap: 8px;
	}

	.ac-title-cluster {
		order: -1;
		width: 100%;
		padding: 0;
		margin-bottom: 2px;
	}
}

.automation-calendar-hint {
	flex: none;
	margin: 0 0 12px;
	padding: 8px 14px;
	border: 1px solid color-mix(in srgb, var(--danger) 30%, transparent);
	border-radius: 10px;
	background: color-mix(in srgb, var(--danger) 8%, transparent);
	color: var(--danger);
	font-size: var(--fs-12);
	font-weight: 600;
}

/* ═══ Calendar Stage Framing ═══ */
.ac-stage {
	position: relative;
	min-width: 0;
	min-height: 0;
	flex: 1 1 auto;
	overflow: hidden;
}

.automation-calendar :deep(.fc-1P) {
	height: 100%;
	min-height: 0;
}

/* ═══ Deep Grid Styling (Eliminating Stiff Lines) ═══ */
.automation-calendar :deep([role="table"]) {
	border: 1px solid var(--line-1) !important;
	border-radius: 14px !important;
	background: var(--panel) !important;
	overflow: hidden;
	box-shadow: 0 2px 16px rgba(var(--shadow-ink), 0.04);
}

/* Weekday header row */
.automation-calendar :deep([role="columnheader"]) {
	border-inline: none !important;
	border-bottom: 1px solid var(--line-1) !important;
	background: var(--panel-muted) !important;
	padding: 10px 0 !important;
}

.automation-calendar :deep([role="columnheader"] .fc-classic-rVY) {
	font-family: var(--font-code, monospace);
	font-size: var(--fs-11);
	font-weight: 650;
	letter-spacing: 0.12em;
	color: var(--muted);
	text-transform: uppercase;
}

/* Day cells */
.automation-calendar :deep([role="cell"]) {
	border-color: color-mix(in srgb, var(--line-1) 65%, transparent) !important;
	background: var(--panel);
	transition: background 0.18s cubic-bezier(0.16, 1, 0.3, 1);
}

.automation-calendar :deep([role="cell"]:hover) {
	background: color-mix(in srgb, var(--panel-muted) 60%, transparent) !important;
}

/* Day date numbers */
.automation-calendar :deep([role="cell"] .fc-classic-84e) {
	padding: 6px 8px 3px !important;
}

.automation-calendar :deep([role="cell"] .fc-classic-V9v) {
	font-family: var(--font-code, monospace);
	font-variant-numeric: tabular-nums;
	font-size: 12px;
	font-weight: 600;
	color: var(--text-soft);
	letter-spacing: -0.01em;
}

/* Other month days */
.automation-calendar :deep([role="cell"] .fc-classic-taq) {
	opacity: 0.35;
	color: var(--faint) !important;
}

/* Today cell: ethereal radial glow & accent badge */
.automation-calendar :deep([role="cell"][aria-current="date"]) {
	background: radial-gradient(ellipse at 50% 12%, color-mix(in srgb, var(--accent) 14%, transparent), transparent 75%), var(--panel) !important;
}

.automation-calendar :deep([role="cell"][aria-current="date"] .fc-classic-V9v) {
	display: inline-flex;
	align-items: center;
	justify-content: center;
	min-width: 22px;
	height: 22px;
	padding: 0 4px;
	border-radius: 999px;
	background: var(--accent);
	color: var(--accent-ink-on, #fff) !important;
	font-weight: 700;
	box-shadow: 0 0 12px color-mix(in srgb, var(--accent) 45%, transparent);
}

/* Event micro-card container inside FullCalendar */
.automation-calendar :deep([role="button"][tabindex="0"]) {
	margin: 1px 3px !important;
	border-radius: 6px !important;
	border: 1px solid color-mix(in srgb, var(--line-1) 85%, transparent) !important;
	background: color-mix(in srgb, var(--panel-muted) 85%, var(--panel)) !important;
	box-shadow: 0 1px 3px rgba(var(--shadow-ink), 0.03);
	transition: transform 0.18s cubic-bezier(0.16, 1, 0.3, 1), box-shadow 0.18s ease, border-color 0.18s ease !important;
	outline: none !important;
}

.automation-calendar :deep([role="button"][tabindex="0"]:hover) {
	transform: translateY(-1px);
	box-shadow: 0 4px 14px rgba(var(--shadow-ink), 0.12);
	border-color: var(--accent) !important;
	z-index: 10;
}

/* TimeGrid & List View Overrides */
.automation-calendar :deep(.fc-classic-C1x) {
	border-color: color-mix(in srgb, var(--line-1) 65%, transparent) !important;
}

.automation-calendar :deep(.fc-classic-sYT) {
	border-color: var(--accent) !important;
	box-shadow: 0 0 8px var(--accent);
}

/* ═══ Event Hover Popover (Awwwards-Level Floating Glass Card) ═══ */
.ac-popover-anchor {
	position: absolute;
	width: 300px;
	z-index: 60;
	pointer-events: auto;
	filter: drop-shadow(0 12px 28px rgba(var(--shadow-ink), 0.16)) drop-shadow(0 2px 8px rgba(var(--shadow-ink), 0.08));
}

.ac-popover-anchor.is-top {
	transform: translateY(-100%);
}

.ac-hover-popover {
	display: flex;
	flex-direction: column;
	border: 1px solid var(--line-1);
	border-radius: 12px;
	background: color-mix(in srgb, var(--panel) 92%, var(--bg));
	backdrop-filter: blur(18px) saturate(160%);
	-webkit-backdrop-filter: blur(18px) saturate(160%);
	overflow: hidden;
	box-shadow: inset 0 1px 0 rgba(255, 255, 255, 0.08);
}

.ac-popover-head {
	display: flex;
	flex-direction: column;
	gap: 6px;
	padding: 12px 14px 10px;
	border-bottom: 1px solid color-mix(in srgb, var(--line-1) 60%, transparent);
	background: color-mix(in srgb, var(--panel-muted) 40%, transparent);
}

.ac-popover-badges {
	display: flex;
	align-items: center;
	gap: 6px;
}

.ac-popover-badge {
	display: inline-flex;
	align-items: center;
	height: 18px;
	padding: 0 6px;
	border-radius: 4px;
	font-size: 10.5px;
	font-weight: 600;
	line-height: 18px;
	letter-spacing: -0.01em;
}

.ac-badge-kind {
	background: color-mix(in srgb, var(--accent) 14%, transparent);
	color: var(--accent);
}

.ac-badge-status {
	background: color-mix(in srgb, var(--line-1) 80%, transparent);
	color: var(--muted);
}

.ac-badge-status.is-completed,
.ac-badge-status.is-done {
	background: color-mix(in srgb, var(--success) 14%, transparent);
	color: var(--success);
}

.ac-badge-status.is-failed,
.ac-badge-status.is-cancelled,
.ac-badge-status.is-interrupted {
	background: color-mix(in srgb, var(--danger) 14%, transparent);
	color: var(--danger);
}

.ac-badge-status.is-running {
	background: color-mix(in srgb, var(--accent) 14%, transparent);
	color: var(--accent);
}

.ac-popover-title {
	margin: 0;
	font-size: var(--fs-13);
	font-weight: 650;
	color: var(--text);
	line-height: 1.35;
	letter-spacing: -0.01em;
	word-break: break-word;
}

.ac-popover-body {
	display: flex;
	flex-direction: column;
	gap: 10px;
	padding: 12px 14px;
}

.ac-popover-item {
	display: flex;
	flex-direction: column;
	gap: 3px;
}

.ac-popover-item-label {
	display: flex;
	align-items: center;
	gap: 5px;
	color: var(--muted);
	font-size: var(--fs-11);
	font-weight: 600;
	text-transform: uppercase;
	letter-spacing: 0.04em;
}

.ac-popover-icon {
	flex: none;
	opacity: 0.8;
}

.ac-popover-item-val {
	display: flex;
	flex-wrap: wrap;
	align-items: baseline;
	gap: 6px;
}

.ac-popover-time {
	font-family: var(--font-code, monospace);
	font-size: var(--fs-12);
	font-weight: 600;
	color: var(--text);
	font-variant-numeric: tabular-nums;
	letter-spacing: -0.02em;
}

.ac-popover-sub {
	font-size: var(--fs-11);
	color: var(--muted);
}

.ac-popover-notes-val {
	max-height: 80px;
	overflow-y: auto;
}

.ac-popover-notes-text {
	margin: 0;
	font-size: var(--fs-12);
	color: var(--text-soft);
	line-height: 1.45;
	white-space: pre-wrap;
	word-break: break-word;
}

.ac-popover-notes-empty {
	margin: 0;
	font-size: var(--fs-11);
	color: var(--faint);
	font-style: italic;
}

.ac-popover-prompt-val {
	padding: 5px 8px;
	border-radius: 6px;
	background: color-mix(in srgb, var(--panel-muted) 80%, transparent);
	border: 1px solid color-mix(in srgb, var(--line-1) 60%, transparent);
	max-height: 60px;
	overflow-y: auto;
}

.ac-popover-prompt-text {
	display: block;
	font-family: var(--font-code, monospace);
	font-size: 11px;
	color: var(--text-soft);
	line-height: 1.4;
	white-space: pre-wrap;
	word-break: break-word;
}

.ac-popover-footer {
	display: flex;
	align-items: center;
	justify-content: space-between;
	padding: 9px 14px;
	border-top: 1px solid color-mix(in srgb, var(--line-1) 60%, transparent);
	background: color-mix(in srgb, var(--panel-muted) 50%, transparent);
	cursor: pointer;
	transition: background 0.16s ease, color 0.16s ease;
}

.ac-popover-footer:hover {
	background: var(--panel-hover);
}

.ac-popover-action {
	display: flex;
	align-items: center;
	gap: 6px;
	font-size: var(--fs-11);
	font-weight: 600;
	color: var(--muted);
	transition: color 0.15s ease;
}

.ac-popover-footer:hover .ac-popover-action {
	color: var(--accent);
}

.ac-popover-action-arrow {
	color: var(--muted);
	transition: transform 0.18s var(--ease-spring), color 0.15s ease;
}

.ac-popover-footer:hover .ac-popover-action-arrow {
	transform: translateX(3px);
	color: var(--accent);
}

/* Transitions */
.ac-popover-fade-enter-active,
.ac-popover-fade-leave-active {
	transition: opacity 0.18s cubic-bezier(0.16, 1, 0.3, 1), transform 0.18s cubic-bezier(0.16, 1, 0.3, 1);
}

.ac-popover-fade-enter-from,
.ac-popover-fade-leave-to {
	opacity: 0;
	transform: translateY(6px) scale(0.97);
}

.ac-popover-anchor.is-top.ac-popover-fade-enter-from,
.ac-popover-anchor.is-top.ac-popover-fade-leave-to {
	opacity: 0;
	transform: translateY(-100%) translateY(-6px) scale(0.97);
}

@media (prefers-reduced-motion: reduce) {
	.ac-popover-fade-enter-active,
	.ac-popover-fade-leave-active {
		transition: none;
	}
}
</style>
