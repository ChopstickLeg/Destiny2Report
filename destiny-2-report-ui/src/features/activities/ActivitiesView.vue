<script setup lang="ts">
import { computed } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useQueries } from '@tanstack/vue-query'
import ReportSection from '@/components/base/ReportSection.vue'
import SegmentedControl from '@/components/base/SegmentedControl.vue'
import ErrorState from '@/components/base/ErrorState.vue'
import EmptyState from '@/components/base/EmptyState.vue'
import SkeletonBlock from '@/components/base/SkeletonBlock.vue'
import BarList from '@/components/charts/BarList.vue'
import SplitBar from '@/components/charts/SplitBar.vue'
import { fetchPlaytime, reportKeys } from '@/lib/api/reports'
import type { ActivityModeParam } from '@/lib/api/types'
import { formatHours, parseTimeSpan } from '@/lib/formatting/duration'
import { useReportIdentity } from '@/features/report-overview/useReport'
import { humanizeModeName } from '@/features/report-overview/report-view'
import { MODE_COLOR } from '@/features/combat/combat-view'
import { MISSING_ACTIVITY_MODE_EXPLANATION, isMissingActivityMode } from '@/lib/stat-explanations'
import LeaderboardStandingBadge from '@/components/base/LeaderboardStandingBadge.vue'

const identity = useReportIdentity()
const route = useRoute()
const router = useRouter()

const BUCKETS: ActivityModeParam[] = ['PvE', 'PvP', 'Gambit']
const BUCKET_METRICS: Record<ActivityModeParam, string> = {
  PvE: 'time.mode.pve',
  PvP: 'time.mode.crucible',
  Gambit: 'time.mode.gambit',
}

const selected = computed<ActivityModeParam>(() => {
  const q = route.query.mode
  return q === 'PvP' || q === 'Gambit' ? q : 'PvE'
})

function select(mode: ActivityModeParam) {
  void router.replace({ query: mode === 'PvE' ? {} : { mode } })
}

// The three buckets load in parallel with independent error/retry states.
const queries = useQueries({
  queries: computed(() =>
    BUCKETS.map((mode) => ({
      queryKey: reportKeys.playtime(identity.value, mode),
      queryFn: ({ signal }: { signal?: AbortSignal }) =>
        fetchPlaytime(identity.value, mode, signal),
      staleTime: 5 * 60_000,
    })),
  ),
})

const buckets = computed(() =>
  BUCKETS.map((mode, index) => {
    const query = queries.value[index]!
    const totalSeconds = query.data ? (parseTimeSpan(query.data.totalPlaytime) ?? 0) : null
    return { mode, query, totalSeconds }
  }),
)

const comparisonSegments = computed(() =>
  buckets.value
    .filter((bucket) => bucket.totalSeconds !== null && bucket.totalSeconds > 0)
    .map((bucket) => ({
      label: bucket.mode,
      value: bucket.totalSeconds ?? 0,
      color: MODE_COLOR[bucket.mode],
    })),
)

const comparisonReady = computed(() => buckets.value.every((bucket) => !bucket.query.isPending))

const selectedBucket = computed(() =>
  buckets.value.find((bucket) => bucket.mode === selected.value)!,
)

const modeBars = computed(() =>
  [...(selectedBucket.value.query.data?.modes ?? [])]
    .map((mode) => ({ ...mode, seconds: parseTimeSpan(mode.playtime) ?? 0 }))
    .filter((mode) => mode.seconds > 0)
    .sort((a, b) => b.seconds - a.seconds)
    .map((mode) => {
      const label = humanizeModeName(mode.modeName)
      return {
        key: String(mode.mode),
        label,
        value: mode.seconds,
        display: formatHours(mode.seconds),
        color: MODE_COLOR[selected.value],
        tooltip: isMissingActivityMode(label) ? MISSING_ACTIVITY_MODE_EXPLANATION : undefined,
        metricKey: `time.mode.${mode.mode}`,
      }
    }),
)
</script>

<template>
  <div class="container activities">
    <ReportSection
      title="Where the hours went"
      subtitle="Time inside recorded activities, split by bucket"
    >
      <div v-if="!comparisonReady" class="loading-stack">
        <SkeletonBlock height="2rem" />
        <SkeletonBlock height="1rem" width="60%" />
      </div>

      <template v-else>
        <SplitBar
          v-if="comparisonSegments.length > 0"
          :segments="comparisonSegments"
          unit="seconds played"
        />
        <dl class="bucket-totals">
          <div v-for="bucket in buckets" :key="bucket.mode" class="bucket-total">
            <dt class="bucket-label">
              <span
                class="bucket-dot"
                :style="{ background: MODE_COLOR[bucket.mode] }"
                aria-hidden="true"
              />
              {{ bucket.mode }}
            </dt>
            <dd class="bucket-value tnum">
              <template v-if="bucket.query.isError">N/A</template>
              <template v-else>{{ formatHours(bucket.totalSeconds ?? 0) }}</template>
            </dd>
            <LeaderboardStandingBadge :metric-key="BUCKET_METRICS[bucket.mode]" />
          </div>
        </dl>
      </template>
    </ReportSection>

    <ReportSection
      title="Breakdown by activity"
      subtitle="Sorted by time spent in each specific mode"
    >
      <template #actions>
        <SegmentedControl
          :model-value="selected"
          :options="BUCKETS.map((mode) => ({ value: mode, label: mode }))"
          label="Activity bucket"
          @update:model-value="select"
        />
      </template>

      <div v-if="selectedBucket.query.isPending" class="loading-stack">
        <SkeletonBlock v-for="n in 5" :key="n" height="2rem" />
      </div>

      <ErrorState
        v-else-if="selectedBucket.query.isError"
        :error="selectedBucket.query.error"
        :context="`Couldn't load ${selected} playtime`"
        @retry="selectedBucket.query.refetch()"
      />

      <EmptyState
        v-else-if="modeBars.length === 0"
        title="No time recorded"
        :description="`No ${selected} playtime shows up in this player's history.`"
      />

      <BarList v-else :items="modeBars" unit="hours played" />
    </ReportSection>
  </div>
</template>

<style scoped>
.activities {
  padding-top: var(--space-2);
}

.loading-stack {
  display: flex;
  flex-direction: column;
  gap: var(--space-2);
}

.bucket-totals {
  display: flex;
  gap: var(--space-6);
  margin-top: var(--space-4);
  flex-wrap: wrap;
}

.bucket-label {
  display: flex;
  align-items: center;
  gap: var(--space-2);
  font-size: var(--text-xs);
  text-transform: uppercase;
  letter-spacing: 0.06em;
  color: var(--color-text-muted);
}

.bucket-dot {
  width: 0.5rem;
  height: 0.5rem;
  border-radius: var(--radius-full);
}

.bucket-value {
  font-size: var(--text-lg);
  font-weight: 600;
  font-family: var(--font-display);
}
</style>
