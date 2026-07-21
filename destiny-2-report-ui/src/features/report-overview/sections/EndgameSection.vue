<script setup lang="ts">
import { computed, ref } from 'vue'
import ReportSection from '@/components/base/ReportSection.vue'
import SegmentedControl from '@/components/base/SegmentedControl.vue'
import type { ActivityCompletionSummary, DestinyReport } from '@/lib/api/types'
import { formatClock, parseTimeSpan } from '@/lib/formatting/duration'
import { formatInteger, formatPercent } from '@/lib/formatting/numbers'
import { formatDate, parseApiDate } from '@/lib/formatting/dates'
import { distinctions, sortCompletions } from '../report-view'

const props = defineProps<{ report: DestinyReport }>()

type Bucket = 'raids' | 'dungeons' | 'conquests'

const buckets = computed(() => {
  const available: Array<{ value: Bucket; label: string }> = []
  if (props.report.raidCompletions.length > 0) available.push({ value: 'raids', label: 'Raids' })
  if (props.report.dungeonCompletions.length > 0)
    available.push({ value: 'dungeons', label: 'Dungeons' })
  if (props.report.conquestCompletions.length > 0)
    available.push({ value: 'conquests', label: 'Conquests' })
  return available
})

const selected = ref<Bucket>('raids')
const active = computed<Bucket>(() =>
  buckets.value.some((b) => b.value === selected.value)
    ? selected.value
    : (buckets.value[0]?.value ?? 'raids'),
)

const rows = computed<ActivityCompletionSummary[]>(() => {
  const source =
    active.value === 'raids'
      ? props.report.raidCompletions
      : active.value === 'dungeons'
        ? props.report.dungeonCompletions
        : props.report.conquestCompletions
  return sortCompletions(source)
})

function fastest(summary: ActivityCompletionSummary): string | null {
  const seconds = parseTimeSpan(summary.fastestCompletion?.duration)
  return seconds && seconds > 0 ? formatClock(seconds) : null
}

function completionDate(value: string | undefined | null): string | null {
  const date = parseApiDate(value)
  return date ? formatDate(date) : null
}
</script>

<template>
  <ReportSection
    id="endgame"
    title="Endgame record"
    subtitle="Cleared activities first, ranked by completions"
  >
    <template #actions>
      <SegmentedControl
        v-if="buckets.length > 1"
        :model-value="active"
        :options="buckets"
        label="Endgame activity type"
        @update:model-value="selected = $event"
      />
    </template>

    <ul class="endgame-list">
      <li v-for="row in rows" :key="row.activityName" class="endgame-row">
        <div class="endgame-main">
          <span class="endgame-name">{{ row.activityName }}</span>
          <span v-if="distinctions(row).length > 0" class="endgame-badges">
            <span v-for="badge in distinctions(row)" :key="badge.key" class="badge">
              {{ badge.label }}
            </span>
          </span>
        </div>

        <dl class="endgame-stats">
          <div class="stat">
            <dt>Clears</dt>
            <dd class="tnum">
              {{ formatInteger(row.completionCount)
              }}<span class="stat-of">/{{ formatInteger(row.activityCount) }}</span>
            </dd>
          </div>
          <div class="stat">
            <dt>Clear rate</dt>
            <dd class="tnum">{{ formatPercent(row.clearRate, 0) }}</dd>
          </div>
          <div v-if="completionDate(row.firstCompletion?.completedAt)" class="stat stat--wide">
            <dt>First clear</dt>
            <dd class="tnum">{{ completionDate(row.firstCompletion?.completedAt) }}</dd>
          </div>
          <div v-if="completionDate(row.lastCompletion?.completedAt)" class="stat stat--wide">
            <dt>Last clear</dt>
            <dd class="tnum">{{ completionDate(row.lastCompletion?.completedAt) }}</dd>
          </div>
          <div v-if="fastest(row)" class="stat">
            <dt>Fastest</dt>
            <dd class="tnum">{{ fastest(row) }}</dd>
          </div>
        </dl>
      </li>
    </ul>
  </ReportSection>
</template>

<style scoped>
.endgame-list {
  list-style: none;
  padding: 0;
}

.endgame-row {
  display: flex;
  align-items: baseline;
  justify-content: space-between;
  gap: var(--space-4);
  padding: var(--space-3) 0;
  border-bottom: 1px solid var(--color-border);
  flex-wrap: wrap;
}

.endgame-main {
  display: flex;
  align-items: baseline;
  gap: var(--space-3);
  min-width: 14rem;
  flex: 1;
}

.endgame-name {
  font-weight: 550;
}

.endgame-badges {
  display: inline-flex;
  gap: var(--space-1);
}

.badge {
  font-size: var(--text-xs);
  padding: 0 var(--space-2);
  border: 1px solid var(--color-accent);
  border-radius: var(--radius-full);
  color: var(--color-accent-strong);
  white-space: nowrap;
}

.endgame-stats {
  display: flex;
  gap: var(--space-4);
  flex-wrap: wrap;
}

.stat dt {
  font-size: var(--text-xs);
  color: var(--color-text-muted);
}

.stat dd {
  font-size: var(--text-sm);
}

.stat-of {
  color: var(--color-text-muted);
}

@media (max-width: 40rem) {
  .endgame-row {
    flex-direction: column;
    gap: var(--space-2);
  }

  .endgame-main {
    min-width: 0;
  }
}
</style>
