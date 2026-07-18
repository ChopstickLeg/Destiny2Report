<script setup lang="ts">
import { computed } from 'vue'
import ReportSection from '@/components/base/ReportSection.vue'
import type { DestinyReport } from '@/lib/api/types'
import { formatInteger } from '@/lib/formatting/numbers'

const props = defineProps<{ report: DestinyReport }>()

interface Oddity {
  key: string
  value: number
  label: string
  caption: string
}

const oddities = computed<Oddity[]>(() => {
  const r = props.report
  return [
    {
      key: 'good-boy',
      value: r.goodBoyProtocol,
      label: 'Good Boy Protocol',
      caption: "Visits to the Tower's best boy.",
    },
    {
      key: 'fish',
      value: r.fishCaught,
      label: 'Fish caught',
      caption: 'Gotta catch them all.',
    },
    {
      key: 'misadventures',
      value: r.misadventures,
      label: 'Misadventures',
      caption: 'Deaths with nobody to blame, Warlock jump included.',
    },
    {
      key: 'zero-kill',
      value: r.zeroKillActivities,
      label: 'Zero-kill activities',
      caption: 'Completed without harming a single combatant.',
    },
  ].filter((item) => item.value > 0)
})
</script>

<template>
  <ReportSection id="guardian-oddities" title="Guardian oddities">
    <dl class="oddity-grid">
      <div v-for="item in oddities" :key="item.key" class="oddity">
        <dt class="oddity-label">{{ item.label }}</dt>
        <dd class="oddity-value display tnum">{{ formatInteger(item.value) }}</dd>
        <dd class="oddity-caption">{{ item.caption }}</dd>
      </div>
    </dl>
  </ReportSection>
</template>

<style scoped>
.oddity-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(min(11rem, 100%), 1fr));
  gap: var(--space-4);
}

.oddity {
  display: flex;
  flex-direction: column;
  padding: var(--space-4);
  background: var(--color-surface);
  border: 1px solid var(--color-border);
  border-radius: var(--radius-md);
}

.oddity-value {
  order: -1;
  font-size: var(--text-xl);
  font-weight: 600;
}

.oddity-label {
  margin-top: var(--space-1);
  font-size: var(--text-sm);
  font-weight: 550;
}

.oddity-caption {
  margin-top: var(--space-1);
  font-size: var(--text-xs);
  color: var(--color-text-muted);
}
</style>
