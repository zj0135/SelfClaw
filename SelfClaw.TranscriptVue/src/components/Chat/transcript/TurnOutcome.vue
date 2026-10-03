<script setup>
import { computed } from 'vue';
import { formatTurnOutcome } from '../../../renderers/turnOutcome.js';

const props = defineProps({ outcome: { type: Object, default: null } });
const presentation = computed(() => formatTurnOutcome(props.outcome));
</script>

<template>
    <footer v-if="presentation" class="turn-outcome" :class="outcome.status" :data-turn-id="outcome.turnId" role="status">
        <div class="outcome-meta">
            <span>{{ presentation.label }}</span>
            <span v-if="presentation.details" class="outcome-details">{{ presentation.details }}</span>
        </div>
        <p v-if="outcome.errorMessage" class="outcome-error">{{ outcome.errorMessage }}</p>
    </footer>
</template>

<style scoped>
.turn-outcome { margin-top: 10px; color: var(--text-muted, #777); font-size: 12px; line-height: 1.6; }
.outcome-meta { display: flex; flex-wrap: wrap; gap: 12px; }
.outcome-details { font-variant-numeric: tabular-nums; }
.outcome-error { margin: 4px 0 0; white-space: pre-wrap; overflow-wrap: anywhere; }
.failed .outcome-error, .blocked .outcome-error { color: var(--color-danger, #c65d57); }
</style>
