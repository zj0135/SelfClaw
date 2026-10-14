<script setup>
import { computed, ref } from 'vue';
import FullCalendar from '@fullcalendar/vue3';
import dayGridPlugin from '@fullcalendar/vue3/daygrid';
import timeGridPlugin from '@fullcalendar/vue3/timegrid';
import listPlugin from '@fullcalendar/vue3/list';
import interactionPlugin from '@fullcalendar/vue3/interaction';
import classicThemePlugin from '@fullcalendar/vue3/themes/classic';
import zhCnLocale from '@fullcalendar/vue3/locales/zh-cn';
import { ChevronLeft, ChevronRight } from 'lucide-vue-next';
import AutomationEventChip from './AutomationEventChip.vue';
import { toCalendarEvents } from '../../renderers/automationEvents';

const props = defineProps({
	events: { type: Array, default: () => [] },
	truncated: { type: Boolean, default: false },
});

const emit = defineEmits(['range-change', 'event-click', 'date-click', 'event-drop']);

const calendarRef = ref(null);
const currentTitle = ref('');
const currentView = ref('dayGridMonth');
const isTodayInView = ref(true);

const calendarEvents = computed(() => toCalendarEvents(props.events).events);

const viewOptions = [
	{ id: 'dayGridMonth', label: '月' },
	{ id: 'timeGridWeek', label: '周' },
	{ id: 'timeGridDay', label: '日' },
	{ id: 'listWeek', label: '日程' },
];

const parsedTitle = computed(() => {
	const raw = currentTitle.value || '';
	const match = raw.match(/^(\d{4})年\s*(.+)$/);
	if (match) {
		return { kicker: match[1], title: match[2] };
	}
	return { kicker: 'CALENDAR', title: raw || '日历' };
});

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
	eventClick: (info) => emit('event-click', { ...info.event.extendedProps }),
	dateClick: (info) => emit('date-click', { date: info.date, dateStr: info.dateStr, allDay: info.allDay }),
	eventDrop: (info) => emit('event-drop', { event: { ...info.event.extendedProps }, start: info.event.start }),
	datesSet: (info) => {
		currentTitle.value = info.view.title;
		currentView.value = info.view.type;
		const now = new Date();
		isTodayInView.value = now >= info.start && now < info.end;
		emit('range-change', { fromUtc: info.start.toISOString(), toUtc: info.end.toISOString() });
	},
}));
</script>

<template>
	<section class="automation-calendar">
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
					<AutomationEventChip :event="arg.event.extendedProps" :time-text="arg.timeText" />
				</template>
			</FullCalendar>
		</div>
	</section>
</template>

<style scoped>
.automation-calendar {
	position: relative;
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
	gap: 16px;
	padding: 4px 4px 14px;
	flex: none;
}

.ac-controls-cluster {
	display: flex;
	align-items: center;
	gap: 8px;
}

.ac-nav-cluster {
	display: flex;
	align-items: center;
	gap: 8px;
}

.ac-nav-arrows {
	display: inline-flex;
	align-items: center;
	padding: 2px;
	border: 1px solid var(--line-1);
	border-radius: 9px;
	background: var(--panel);
	box-shadow: 0 1px 3px rgba(var(--shadow-ink), 0.04);
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
}

.ac-title-kicker {
	font-family: var(--font-code, monospace);
	font-size: var(--fs-10);
	font-weight: 600;
	letter-spacing: 0.24em;
	color: var(--muted);
	text-transform: uppercase;
	margin-bottom: 2px;
}

.ac-title-main {
	margin: 0;
	font-family: var(--font-display);
	font-size: var(--fs-18);
	font-weight: 680;
	letter-spacing: -0.015em;
	color: var(--text);
	line-height: 1.2;
}

.ac-view-switcher {
	display: inline-flex;
	padding: 3px;
	gap: 2px;
	border: 1px solid var(--line-1);
	border-radius: 10px;
	background: var(--panel-muted);
	box-shadow: inset 0 1px 2px rgba(var(--shadow-ink), 0.04);
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
</style>
