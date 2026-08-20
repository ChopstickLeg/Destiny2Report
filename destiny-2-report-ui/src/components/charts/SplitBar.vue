<script setup lang="ts">
import { computed } from 'vue'
import { formatInteger, formatShare } from '@/lib/formatting/numbers'
import LeaderboardStandingBadge from '@/components/base/LeaderboardStandingBadge.vue'

interface SplitSegment {
  label: string
  value: number
  color: string
  metricKey?: string
}

const props = defineProps<{
  segments: SplitSegment[]
  /** What the values count, for assistive summaries, e.g. "matches". */
  unit: string
}>()

const total = computed(() => props.segments.reduce((sum, s) => sum + s.value, 0))
</script>

<template>
  <div class="split">
    <div class="split-bar" aria-hidden="true">
      <div
        v-for="segment in segments"
        :key="segment.label"
        class="split-segment"
        :style="{
          width: total > 0 ? `${(segment.value / total) * 100}%` : '0%',
          background: segment.color,
        }"
      />
    </div>
    <dl class="split-legend">
      <div v-for="segment in segments" :key="segment.label" class="split-item">
        <dt class="split-label">
          <span class="split-swatch" :style="{ background: segment.color }" aria-hidden="true" />
          {{ segment.label }}
        </dt>
        <dd class="split-value tnum">
          <LeaderboardStandingBadge v-if="segment.metricKey" :metric-key="segment.metricKey" />
          {{ formatInteger(segment.value) }}
          <span class="split-share">({{ formatShare(segment.value, total, 0) }})</span>
        </dd>
      </div>
    </dl>
    <span class="visually-hidden">
      {{ segments.map((s) => `${s.label}: ${formatInteger(s.value)} ${unit}`).join(', ') }}
    </span>
  </div>
</template>

<style scoped>
.split-bar {
  display: flex;
  height: 8px;
  border-radius: var(--radius-full);
  overflow: hidden;
  background: var(--color-bar-track);
}

.split-legend {
  display: flex;
  gap: var(--space-4);
  margin-top: var(--space-2);
  flex-wrap: wrap;
}

.split-item {
  display: flex;
  align-items: baseline;
  gap: var(--space-2);
}

.split-label {
  display: inline-flex;
  align-items: center;
  gap: var(--space-1);
  font-size: var(--text-xs);
  color: var(--color-text-secondary);
}

.split-swatch {
  width: 0.5rem;
  height: 0.5rem;
  border-radius: 2px;
}

.split-value {
  display: inline-flex;
  align-items: center;
  gap: var(--space-1);
  font-size: var(--text-sm);
}

.split-share {
  color: var(--color-text-muted);
  font-size: var(--text-xs);
}
</style>
