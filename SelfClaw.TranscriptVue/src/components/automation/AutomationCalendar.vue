<script setup>
import { computed } from 'vue';
import FullCalendar from '@fullcalendar/vue3';
import dayGridPlugin from '@fullcalendar/vue3/daygrid';
import timeGridPlugin from '@fullcalendar/vue3/timegrid';
import listPlugin from '@fullcalendar/vue3/list';
import interactionPlugin from '@fullcalendar/vue3/interaction';
import classicThemePlugin from '@fullcalendar/vue3/themes/classic';
import zhCnLocale from '@fullcalendar/vue3/locales/zh-cn';
import AutomationEventChip from './AutomationEventChip.vue';
import { toCalendarEvents } from '../../renderers/automationEvents';

const props = defineProps({
	events: { type: Array, default: () => [] },
	truncated: { type: Boolean, default: false },
});

const emit = defineEmits(['range-change', 'event-click', 'date-click', 'event-drop']);

const calendarEvents = computed(() => toCalendarEvents(props.events).events);

const calendarOptions = computed(() => ({
	plugins: [classicThemePlugin, dayGridPlugin, timeGridPlugin, listPlugin, interactionPlugin],
	initialView: 'dayGridMonth',
	locale: zhCnLocale,
	height: '100%',
	expandRows: true,
	dayMaxEvents: 3,
	nowIndicator: true,
	selectable: true,
	selectMirror: true,
	headerToolbar: {
		left: 'prev,next today',
		center: 'title',
		right: 'dayGridMonth,timeGridWeek,timeGridDay,listWeek',
	},
	// 位置与标签都由 locale 提供（zh-cn），不再逐项覆盖，避免未知选项告警。
	events: calendarEvents.value,
	eventClick: (info) => emit('event-click', { ...info.event.extendedProps }),
	dateClick: (info) => emit('date-click', { date: info.date, dateStr: info.dateStr, allDay: info.allDay }),
	eventDrop: (info) => emit('event-drop', { event: { ...info.event.extendedProps }, start: info.event.start }),
	datesSet: (info) => emit('range-change', { fromUtc: info.start.toISOString(), toUtc: info.end.toISOString() }),
}));
</script>

<template>
	<section class="automation-calendar">
		<div v-if="truncated" class="automation-calendar-hint" role="status">
			投影数量已达上限，仅显示前 300 条；缩小时间范围可查看更完整的结果。
		</div>
		<FullCalendar :options="calendarOptions">
			<template #eventContent="arg">
				<AutomationEventChip :event="arg.event.extendedProps" :time-text="arg.timeText" />
			</template>
		</FullCalendar>
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
	/* 主题 token：FullCalendar v7 的 classic 主题由这些变量驱动，覆盖它们即可贴合本应用配色。
	   类名带版本化后缀（fc-classic-xxxx），因此优先用变量而不是 :deep 选择器。 */
	--fc-classic-primary: var(--accent);
	--fc-classic-primary-foreground: #fff;
	--fc-classic-foreground: var(--text);
	--fc-classic-background: var(--panel);
	--fc-classic-border: var(--border);
	--fc-classic-border-style: 1px solid var(--border);
	--fc-classic-muted: var(--panel-muted);
	--fc-classic-muted-foreground: var(--muted);
	--fc-classic-faint: var(--panel-soft);
	--fc-classic-faint-foreground: var(--muted-soft);
	--fc-classic-today: color-mix(in srgb, var(--accent) 10%, transparent);
	--fc-classic-now: var(--accent);
	--fc-classic-highlight: color-mix(in srgb, var(--accent) 14%, transparent);
	--fc-classic-button: var(--panel);
	--fc-classic-button-foreground: var(--text);
	--fc-classic-button-border: var(--border);
	--fc-classic-button-strong: var(--accent);
	--fc-classic-button-strong-border: var(--accent);
	--fc-classic-event: color-mix(in srgb, var(--accent) 14%, transparent);
	--fc-classic-event-contrast: var(--text);
	--fc-classic-background-event: color-mix(in srgb, var(--muted) 22%, transparent);
	--fc-classic-background-event-opacity: 1;
	--fc-classic-background-event-foreground-opacity: 1;
}

.automation-calendar-hint {
	flex: none;
	margin: 0 12px 8px;
	padding: 7px 12px;
	border: 1px solid color-mix(in srgb, var(--danger) 34%, transparent);
	border-radius: 10px;
	background: color-mix(in srgb, var(--danger) 10%, transparent);
	color: var(--danger);
	font-size: var(--fs-12);
	font-weight: 600;
}

/* v7 日历根节点需要显式撑满，否则 height:100% 落在内容高度上；类名取自 classic 主题的
   calendarScreenRoot（版本锁定 7.1.2），升级时随变量一起回归验证。 */
.automation-calendar :deep(.fc-1P) {
	height: 100%;
	min-height: 0;
}
</style>
