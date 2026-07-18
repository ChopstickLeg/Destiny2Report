<script setup lang="ts">
import { computed, watch } from 'vue'
import { RouterView } from 'vue-router'
import SkeletonBlock from '@/components/base/SkeletonBlock.vue'
import ErrorState from '@/components/base/ErrorState.vue'
import GenerationExperience from '@/features/report-generation/GenerationExperience.vue'
import { useQueueWatcher } from '@/features/report-generation/useQueueWatcher'
import { rememberPlayer, loadRecentPlayers } from '@/lib/recent-players'
import ReportMasthead from './ReportMasthead.vue'
import { useInvalidateReport, useReportIdentity, useReportQuery } from './useReport'

const identity = useReportIdentity()
const reportQuery = useReportQuery(identity)
const invalidate = useInvalidateReport(identity)

const report = computed(() => reportQuery.data.value ?? null)

/**
 * A crawl document can describe a queued/failed/private *re*-crawl while a
 * previous successful crawl's data is still present and worth showing.
 * `lastCrawledAtUtc` marks that a crawl has completed at least once.
 */
const hasReadableReport = computed(() => {
  const r = report.value
  if (!r) return false
  return r.crawlState === 'completed' || r.lastCrawledAtUtc !== null
})

const pendingState = computed(() => {
  if (reportQuery.isPending.value || reportQuery.isError.value) return null
  if (!report.value) return 'missing' as const
  if (!hasReadableReport.value) return report.value.crawlState
  return null
})

// Refresh flow: keep the stale report visible, watch progress in a banner.
const refreshWatcher = useQueueWatcher(identity, {
  onCompleted: () => void invalidate(),
})

const refreshing = computed(() => refreshWatcher.isActive.value)

function startRefresh() {
  void refreshWatcher.submitAndWatch()
}

// If the loaded report shows an in-flight recrawl, attach to it quietly.
watch(
  () => report.value?.crawlState,
  (state) => {
    if (
      hasReadableReport.value &&
      (state === 'queued' || state === 'running') &&
      !refreshWatcher.isActive.value
    ) {
      void refreshWatcher.watch()
    }
  },
  { immediate: true },
)

// Remember visited reports for the home page's quick-return list.
watch(report, (r) => {
  if (r && hasReadableReport.value) {
    rememberPlayer({
      membershipTypeId: r.platformId,
      membershipId: r.playerMembershipId,
      displayName: r.displayName,
      displayCode: r.displayCode,
      emblemIconUrl: r.mostUsedEmblems[0]?.iconUrl ?? '',
    })
  }
})

// Best-effort name for the generation screen when no report exists yet.
const knownName = computed(() => {
  if (report.value?.displayName) return report.value.fullDisplayName
  const recent = loadRecentPlayers().find(
    (p) =>
      p.membershipId === identity.value.membershipId &&
      p.membershipTypeId === identity.value.membershipTypeId,
  )
  return recent ? recent.displayName : null
})

function refetchReport() {
  void invalidate()
  void reportQuery.refetch()
}
</script>

<template>
  <div class="report-page">
    <!-- Loading -->
    <div v-if="reportQuery.isPending.value" class="container report-loading">
      <SkeletonBlock height="2.5rem" width="18rem" />
      <SkeletonBlock height="1rem" width="10rem" />
      <SkeletonBlock height="14rem" radius="var(--radius-md)" />
    </div>

    <!-- Request failure (distinct from "no report exists") -->
    <div v-else-if="reportQuery.isError.value" class="container report-error">
      <ErrorState
        :error="reportQuery.error.value"
        context="Couldn't load this report"
        @retry="reportQuery.refetch()"
      />
    </div>

    <!-- No readable report: generation / queue / failure / privacy flow -->
    <GenerationExperience
      v-else-if="pendingState"
      :identity="identity"
      :initial-state="pendingState"
      :player-name="knownName"
      :crawl-error="report?.crawlError ?? ''"
      @refresh="refetchReport"
    />

    <!-- Readable report -->
    <template v-else-if="report">
      <ReportMasthead :report="report" :refreshing="refreshing" @refresh="startRefresh" />

      <div v-if="refreshing" class="refresh-banner" role="status" aria-live="polite">
        <div class="container refresh-banner-row">
          <span class="refresh-dot" aria-hidden="true" />
          <span>
            Refreshing this report:
            {{ refreshWatcher.latest.value?.progress?.label ?? 'Queued - waiting for available crawler' }}.
            Existing data stays visible until it finishes.
          </span>
        </div>
      </div>

      <RouterView />
    </template>
  </div>
</template>

<style scoped>
.report-loading {
  padding-top: var(--space-7);
  display: flex;
  flex-direction: column;
  gap: var(--space-3);
}

.report-error {
  padding-top: var(--space-7);
  max-width: 40rem;
}

.refresh-banner {
  border-bottom: 1px solid var(--color-border);
  background: var(--color-surface);
}

.refresh-banner-row {
  display: flex;
  align-items: center;
  gap: var(--space-2);
  padding-block: var(--space-2);
  font-size: var(--text-xs);
  color: var(--color-text-secondary);
}

.refresh-dot {
  width: 0.5rem;
  height: 0.5rem;
  border-radius: var(--radius-full);
  background: var(--color-accent);
  flex: none;
  animation: pulse 1.6s ease-in-out infinite;
}

@keyframes pulse {
  50% {
    opacity: 0.35;
  }
}

@media (prefers-reduced-motion: reduce) {
  .refresh-dot {
    animation: none;
  }
}
</style>
