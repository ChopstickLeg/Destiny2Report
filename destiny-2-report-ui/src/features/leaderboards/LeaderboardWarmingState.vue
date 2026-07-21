<script setup lang="ts">
import { computed } from 'vue'
import { formatInteger } from '@/lib/formatting/numbers'

const props = defineProps<{
  completedPlayerCount: number
  minimumCompletedPlayers: number
}>()

const progress = computed(() => {
  if (props.minimumCompletedPlayers <= 0) return 100
  return Math.min(100, (props.completedPlayerCount / props.minimumCompletedPlayers) * 100)
})
</script>

<template>
  <section class="warming" aria-labelledby="warming-title">
    <h2 id="warming-title">Rankings are gathering</h2>
    <p>
      Rankings open automatically once enough public Guardian reports have completed their first
      crawl.
    </p>
    <div
      class="progress-track"
      role="progressbar"
      :aria-valuenow="completedPlayerCount"
      :aria-valuemax="minimumCompletedPlayers"
    >
      <span :style="{ width: `${progress}%` }" />
    </div>
    <p class="progress-label tnum">
      {{ formatInteger(completedPlayerCount) }} / {{ formatInteger(minimumCompletedPlayers) }}
      Guardians ready
    </p>
  </section>
</template>

<style scoped>
.warming {
  max-width: 42rem;
  min-height: 18rem;
  margin-inline: auto;
  padding-block: var(--space-7);
  text-align: center;
}
.warming h2 {
  font-size: var(--text-xl);
}
.warming > p {
  max-width: 36rem;
  margin: var(--space-3) auto 0;
  color: var(--color-text-secondary);
}
.progress-track {
  width: min(100%, 32rem);
  height: 0.5rem;
  margin: var(--space-5) auto 0;
  overflow: hidden;
  background: var(--color-surface-sunken);
  border-radius: var(--radius-full);
}
.progress-track span {
  display: block;
  height: 100%;
  background: var(--color-accent);
}
.warming > .progress-label {
  margin-top: var(--space-2);
  font-size: var(--text-sm);
}
</style>
