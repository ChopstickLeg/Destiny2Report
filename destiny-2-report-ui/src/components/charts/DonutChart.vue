<script setup lang="ts">
import { computed } from 'vue'
import { formatInteger, formatShare } from '@/lib/formatting/numbers'
import ExplainedLabel from '@/components/base/ExplainedLabel.vue'

export interface DonutSegment {
  label: string
  value: number
  color: string
  /** Explanation shown when the legend label is hovered or focused. */
  tooltip?: string
}

const props = defineProps<{
  /** Mutually exclusive parts of one whole (already grouped into "Other"). */
  segments: DonutSegment[]
  unit: string
}>()

const SIZE = 132
const THICKNESS = 16
const RADIUS = (SIZE - THICKNESS) / 2
const CIRCUMFERENCE = 2 * Math.PI * RADIUS

const total = computed(() => props.segments.reduce((sum, s) => sum + s.value, 0))

const arcs = computed(() => {
  let offset = 0
  return props.segments.map((segment) => {
    const fraction = total.value > 0 ? segment.value / total.value : 0
    const arc = {
      ...segment,
      dasharray: `${fraction * CIRCUMFERENCE} ${CIRCUMFERENCE}`,
      dashoffset: -offset * CIRCUMFERENCE,
    }
    offset += fraction
    return arc
  })
})
</script>

<template>
  <div class="donut">
    <svg
      :width="SIZE"
      :height="SIZE"
      :viewBox="`0 0 ${SIZE} ${SIZE}`"
      aria-hidden="true"
      class="donut-svg"
    >
      <circle
        :cx="SIZE / 2"
        :cy="SIZE / 2"
        :r="RADIUS"
        fill="none"
        stroke="var(--color-bar-track)"
        :stroke-width="THICKNESS"
      />
      <circle
        v-for="arc in arcs"
        :key="arc.label"
        :cx="SIZE / 2"
        :cy="SIZE / 2"
        :r="RADIUS"
        fill="none"
        :stroke="arc.color"
        :stroke-width="THICKNESS"
        :stroke-dasharray="arc.dasharray"
        :stroke-dashoffset="arc.dashoffset"
        transform-origin="center"
        transform="rotate(-90)"
      />
    </svg>
    <dl class="donut-legend">
      <div v-for="segment in segments" :key="segment.label" class="legend-row">
        <dt class="legend-label">
          <span class="legend-swatch" :style="{ background: segment.color }" aria-hidden="true" />
          <ExplainedLabel
            v-if="segment.tooltip"
            :text="segment.label"
            :explanation="segment.tooltip"
          />
          <template v-else>{{ segment.label }}</template>
        </dt>
        <dd class="legend-value tnum">
          {{ formatInteger(segment.value) }}
          <span class="legend-share">{{ formatShare(segment.value, total, 0) }}</span>
        </dd>
      </div>
    </dl>
    <span class="visually-hidden">
      {{ segments.map((s) => `${s.label}: ${formatInteger(s.value)} ${unit}`).join(', ') }}
    </span>
  </div>
</template>

<style scoped>
.donut {
  display: flex;
  align-items: center;
  gap: var(--space-5);
  flex-wrap: wrap;
}

.donut-svg {
  flex: none;
}

.donut-legend {
  display: flex;
  flex-direction: column;
  gap: var(--space-2);
  min-width: 12rem;
}

.legend-row {
  display: flex;
  align-items: baseline;
  justify-content: space-between;
  gap: var(--space-4);
}

.legend-label {
  display: inline-flex;
  align-items: center;
  gap: var(--space-2);
  font-size: var(--text-sm);
  color: var(--color-text-secondary);
}

.legend-swatch {
  width: 0.625rem;
  height: 0.625rem;
  border-radius: 2px;
  flex: none;
}

.legend-value {
  font-size: var(--text-sm);
}

.legend-share {
  margin-left: var(--space-1);
  color: var(--color-text-muted);
  font-size: var(--text-xs);
}
</style>
