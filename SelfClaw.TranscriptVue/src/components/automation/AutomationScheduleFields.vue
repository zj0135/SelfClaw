<script setup>
import { taskKindLabel } from '../../renderers/automationEvents';

const props = defineProps({
	form: { type: Object, required: true },
});

const kinds = ['none', 'once', 'daily', 'weekly', 'monthly', 'cron'];
const weekdays = [
	{ value: 1, label: '一' },
	{ value: 2, label: '二' },
	{ value: 3, label: '三' },
	{ value: 4, label: '四' },
	{ value: 5, label: '五' },
	{ value: 6, label: '六' },
	{ value: 0, label: '日' },
];

function toggleWeekday(value) {
	const current = new Set(props.form.schedule.weekdays || []);
	if (current.has(value)) current.delete(value);
	else current.add(value);
	props.form.schedule.weekdays = [...current].sort((left, right) => left - right);
}
</script>

<template>
	<span class="ac-field-label">触发器</span>
	<div class="ac-kind-row">
		<button v-for="kind in kinds" :key="kind" type="button" class="ac-kind"
			:class="{ 'is-on': form.kind === kind }" @click="form.kind = kind">
			{{ taskKindLabel(kind) }}
		</button>
	</div>

	<div v-if="form.kind === 'once'" class="ac-schedule-row">
		<label class="ac-field">
			<span class="ac-field-label">触发时间（本地时间）</span>
			<input v-model="form.schedule.runAtLocal" type="datetime-local" />
		</label>
	</div>

	<div v-else-if="form.kind === 'daily' || form.kind === 'weekly' || form.kind === 'monthly'" class="ac-schedule-row">
		<label class="ac-field">
			<span class="ac-field-label">时间（本地时间）</span>
			<input v-model="form.schedule.time" type="time" />
		</label>
		<div v-if="form.kind === 'weekly'" class="ac-field">
			<span class="ac-field-label">星期</span>
			<div class="ac-weekdays">
				<button v-for="day in weekdays" :key="day.value" type="button" class="ac-weekday"
					:class="{ 'is-on': (form.schedule.weekdays || []).includes(day.value) }"
					@click="toggleWeekday(day.value)">{{ day.label }}</button>
			</div>
		</div>
		<label v-if="form.kind === 'monthly'" class="ac-field">
			<span class="ac-field-label">每月第几天</span>
			<input v-model.number="form.schedule.dayOfMonth" type="number" min="1" max="31" />
		</label>
	</div>

	<div v-else-if="form.kind === 'cron'" class="ac-schedule-row">
		<label class="ac-field ac-field-wide">
			<span class="ac-field-label">Cron 表达式（最小间隔 5 分钟）</span>
			<input v-model="form.schedule.cron" type="text" placeholder="*/15 * * * *" spellcheck="false" />
		</label>
	</div>
</template>

<style scoped>
.ac-field {
	display: flex;
	min-width: 0;
	flex-direction: column;
	gap: 5px;
}

.ac-field-wide {
	grid-column: 1 / -1;
}

.ac-field-label {
	color: var(--muted);
	font-size: var(--fs-11);
	font-weight: 650;
	letter-spacing: 0.04em;
	text-transform: uppercase;
}

.ac-field input[type='text'],
.ac-field input[type='time'],
.ac-field input[type='number'],
.ac-field input[type='datetime-local'] {
	width: 100%;
	padding: 8px 10px;
	border: 1px solid var(--border);
	border-radius: 9px;
	background: var(--panel-soft);
	color: var(--text);
	font-family: inherit;
	font-size: var(--fs-13);
}

.ac-kind-row {
	display: flex;
	flex-wrap: wrap;
	gap: 6px;
}

.ac-kind {
	padding: 6px 11px;
	border: 1px solid var(--border);
	border-radius: 999px;
	background: var(--panel-soft);
	color: var(--muted);
	font-size: var(--fs-12);
	font-weight: 650;
}

.ac-kind.is-on {
	border-color: var(--accent);
	background: color-mix(in srgb, var(--accent) 16%, transparent);
	color: var(--accent-2);
}

.ac-schedule-row {
	display: grid;
	grid-template-columns: repeat(auto-fit, minmax(180px, 1fr));
	gap: 10px;
}

.ac-weekdays {
	display: flex;
	gap: 4px;
}

.ac-weekday {
	width: 30px;
	height: 30px;
	border: 1px solid var(--border);
	border-radius: 8px;
	background: var(--panel-soft);
	color: var(--muted);
	font-size: var(--fs-12);
	font-weight: 650;
}

.ac-weekday.is-on {
	border-color: var(--accent);
	background: color-mix(in srgb, var(--accent) 16%, transparent);
	color: var(--accent-2);
}
</style>
