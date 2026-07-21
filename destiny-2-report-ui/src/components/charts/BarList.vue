<script setup lang="ts">
import { computed } from 'vue'
import ExplainedLabel from '@/components/base/ExplainedLabel.vue'

export interface BarListItem {
  key: string
  label: string
  value: number
  /** Pre-formatted value shown to users (units included). */
  display: string
  sublabel?: string
  /** Small qualifier tag, e.g. "Deleted". */
  tag?: string
  /** Stable CSS color; defaults to the neutral bar color. */
  color?: string
  muted?: boolean
  /** Explanation shown when the label is hovered or focused. */
  tooltip?: string
}

const props = withDefaults(
  defineProps<{
    items: BarListItem[]
    /** Scale bars against this value; defaults to the largest item. */
    max?: number
    /** Accessible description of what the values measure, e.g. "kills". */
    unit: string
  }>(),
  { max: undefined },
)

const scale = computed(() => {
  const provided = props.max
  if (provided !== undefined && provided > 0) return provided
  return Math.max(...props.items.map((item) => item.value), 1)
})

function widthFor(item: BarListItem): string {
  const fraction = Math.max(0, Math.min(1, item.value / scale.value))
  // Keep a sliver visible for tiny non-zero values so they read as present.
  if (item.value > 0 && fraction < 0.005) return '0.5%'
  return `${(fraction * 100).toFixed(2)}%`
}
</script>

<template>
  <ol class="bar-list">
    <li
      v-for="item in items"
      :key="item.key"
      class="bar-row"
      :class="{ 'bar-row--muted': item.muted }"
    >
      <div class="bar-row-top">
        <span class="bar-label">
          <ExplainedLabel v-if="item.tooltip" :text="item.label" :explanation="item.tooltip" />
          <template v-else>{{ item.label }}</template>
          <span v-if="item.tag" class="bar-tag">{{ item.tag }}</span>
          <span v-if="item.sublabel" class="bar-sublabel">{{ item.sublabel }}</span>
        </span>
        <span class="bar-value tnum">{{ item.display }}</span>
      </div>
      <div class="bar-track" aria-hidden="true">
        <div
          class="bar-fill"
          :style="{ width: widthFor(item), background: item.color ?? 'var(--color-bar)' }"
        />
      </div>
      <span class="visually-hidden">{{ item.label }}: {{ item.display }} {{ unit }}</span>
    </li>
  </ol>
</template>

<style scoped>
.bar-list {
  list-style: none;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: var(--space-3);
}

.bar-row--muted {
  opacity: 0.55;
}

.bar-row-top {
  display: flex;
  align-items: baseline;
  justify-content: space-between;
  gap: var(--space-3);
  margin-bottom: var(--space-1);
}

.bar-label {
  font-size: var(--text-sm);
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.bar-tag {
  margin-left: var(--space-1);
  padding: 0 var(--space-1);
  border: 1px solid var(--color-border-strong);
  border-radius: var(--radius-sm);
  font-size: var(--text-xs);
  color: var(--color-text-muted);
  text-transform: uppercase;
  letter-spacing: 0.04em;
}

.bar-sublabel {
  margin-left: var(--space-1);
  font-size: var(--text-xs);
  color: var(--color-text-muted);
}

.bar-value {
  font-size: var(--text-sm);
  color: var(--color-text-secondary);
  flex: none;
}

.bar-track {
  height: 6px;
  border-radius: var(--radius-full);
  background: var(--color-bar-track);
  overflow: hidden;
}

.bar-fill {
  height: 100%;
  border-radius: var(--radius-full);
  transition: width var(--transition-medium);
}
</style>
