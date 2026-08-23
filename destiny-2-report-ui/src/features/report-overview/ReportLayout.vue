<script setup lang="ts">
import { computed, provide, ref, watch } from 'vue'
import { RouterView, useRoute } from 'vue-router'
import AppButton from '@/components/base/AppButton.vue'
import SkeletonBlock from '@/components/base/SkeletonBlock.vue'
import ErrorState from '@/components/base/ErrorState.vue'
import GenerationExperience from '@/features/report-generation/GenerationExperience.vue'
import { useQueuePolicy } from '@/features/report-generation/useQueuePolicy'
import { useQueueWatcher } from '@/features/report-generation/useQueueWatcher'
import { rememberPlayer, loadRecentPlayers } from '@/lib/recent-players'
import { getErrorMessage } from '@/lib/api/http'
import { useSessionStore } from '@/stores/session'
import ReportMasthead from './ReportMasthead.vue'
import GlobalStandings from './GlobalStandings.vue'
import {
  playerStandingsKey,
  useInvalidateReport,
  usePlayerStandings,
  useReportIdentity,
  useReportQuery,
} from './useReport'

const identity = useReportIdentity()
const route = useRoute()
const session = useSessionStore()
const {
  policy: queuePolicy,
  isLoading: queuePolicyLoading,
  hasError: queuePolicyFailed,
  retry: retryQueuePolicy,
} = useQueuePolicy()
const reportQuery = useReportQuery(identity)
const standingsQuery = usePlayerStandings(identity)
provide(
  playerStandingsKey,
  computed(() => standingsQuery.data.value?.standings ?? []),
)
const invalidate = useInvalidateReport(identity)

const report = computed(() => reportQuery.data.value ?? null)
const rankingsLoading = computed(
  () => standingsQuery.isPending.value && !standingsQuery.isError.value,
)

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
const sessionResolved = computed(
  () => session.status !== 'unknown' && session.status !== 'resolving',
)
const queueAccessReady = computed(() => queuePolicy.value !== null && sessionResolved.value)
const queueAccessPending = computed(
  () => queuePolicyLoading.value || queuePolicyFailed.value || !sessionResolved.value,
)
const signInRequired = computed(
  () =>
    queueAccessReady.value &&
    queuePolicy.value?.authenticationRequired === true &&
    !session.isSignedIn,
)

const refreshError = computed(() => {
  const error = refreshWatcher.submitError.value
  return error ? getErrorMessage(error, 'The report could not be queued. Please try again.') : null
})

function startRefresh() {
  if (!queueAccessReady.value) return
  if (signInRequired.value) {
    session.beginSignIn(route.fullPath)
    return
  }
  void refreshWatcher.submitAndWatch()
}

// Request one refresh when each readable report is visited. The server owns
// the six-hour cooldown; an automatic cooldown response should leave the
// existing report quietly visible, while manual refresh errors remain visible.
const lastAutoRefreshKey = ref<string | null>(null)
watch(
  [
    () => identity.value.membershipTypeId,
    () => identity.value.membershipId,
    report,
    queuePolicy,
    () => session.status,
    () => session.isSignedIn,
  ],
  ([membershipTypeId, membershipId, currentReport, currentQueuePolicy, , isSignedIn]) => {
    if (
      !queueAccessReady.value ||
      currentQueuePolicy === null ||
      (currentQueuePolicy.authenticationRequired && !isSignedIn) ||
      !currentReport ||
      !hasReadableReport.value ||
      currentReport.platformId !== membershipTypeId ||
      currentReport.playerMembershipId !== membershipId ||
      currentReport.crawlState === 'queued' ||
      currentReport.crawlState === 'running'
    ) {
      return
    }

    const refreshKey = `${membershipTypeId}:${membershipId}`
    if (lastAutoRefreshKey.value === refreshKey) return

    lastAutoRefreshKey.value = refreshKey
    void refreshWatcher.submitAndWatch({ suppressCooldownError: true })
  },
  { immediate: true },
)

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
      :auto-start="route.query.generate === '1'"
      @refresh="refetchReport"
    />

    <!-- Readable report -->
    <template v-else-if="report">
      <ReportMasthead
        :report="report"
        :refreshing="refreshing"
        :queue-access-pending="queueAccessPending"
        :queue-access-error="queuePolicyFailed"
        :sign-in-required="signInRequired"
        @refresh="startRefresh"
      />

      <div v-if="rankingsLoading" class="rankings-loading" role="status" aria-live="polite">
        <span class="visually-hidden">Loading report rankings</span>
        <div class="rankings-loading-rail">
          <div class="container rankings-loading-rail-inner">
            <div class="rankings-loading-title">
              <SkeletonBlock width="7rem" height="0.55rem" />
              <SkeletonBlock width="4.5rem" height="1.35rem" />
            </div>
            <div v-for="n in 5" :key="n" class="rankings-loading-entry">
              <SkeletonBlock width="3.5rem" height="0.5rem" />
              <SkeletonBlock width="7rem" height="0.85rem" />
              <SkeletonBlock width="3rem" height="0.65rem" />
              <SkeletonBlock width="3.75rem" height="1.1rem" radius="var(--radius-full)" />
            </div>
          </div>
        </div>
        <div class="container rankings-loading-body">
          <div class="rankings-loading-heading">
            <SkeletonBlock width="13rem" height="1.35rem" />
            <SkeletonBlock width="min(24rem, 80%)" height="0.75rem" />
          </div>
          <div class="rankings-loading-grid">
            <SkeletonBlock height="10rem" radius="var(--radius-md)" />
            <SkeletonBlock height="10rem" radius="var(--radius-md)" />
          </div>
        </div>
      </div>

      <template v-else>
        <GlobalStandings :standings="standingsQuery.data.value?.standings ?? []" />

        <div v-if="refreshing" class="refresh-banner" role="status" aria-live="polite">
          <div class="container refresh-banner-row">
            <span class="refresh-dot" aria-hidden="true" />
            <span>
              Refreshing this report:
              {{
                refreshWatcher.latest.value?.progress?.label ??
                'Queued - waiting for available crawler'
              }}. Existing data stays visible until it finishes.
            </span>
          </div>
        </div>

        <div v-else-if="queuePolicyFailed" class="refresh-banner refresh-banner--error" role="alert">
          <div class="container refresh-banner-row">
            <span>Queue access couldn't be verified. Refreshing is disabled until it succeeds.</span>
            <AppButton size="sm" variant="secondary" @click="retryQueuePolicy">Try again</AppButton>
          </div>
        </div>

        <div v-else-if="refreshError" class="refresh-banner refresh-banner--error" role="alert">
          <div class="container refresh-banner-row">{{ refreshError }}</div>
        </div>

        <RouterView />
      </template>
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

.rankings-loading-rail {
  border-bottom: 1px solid var(--color-border);
  background: var(--color-surface);
}

.rankings-loading-rail-inner {
  display: grid;
  grid-template-columns: 8.5rem repeat(5, minmax(8rem, 1fr));
  min-height: 6.5rem;
  padding-block: var(--space-3);
}

.rankings-loading-title,
.rankings-loading-entry {
  display: flex;
  flex-direction: column;
  justify-content: center;
  gap: var(--space-2);
  padding-inline: var(--space-3);
}

.rankings-loading-title {
  padding-left: 0;
}

.rankings-loading-entry {
  border-left: 1px solid var(--color-border);
}

.rankings-loading-body {
  padding-top: var(--space-7);
}

.rankings-loading-heading {
  display: flex;
  flex-direction: column;
  gap: var(--space-2);
  padding-bottom: var(--space-3);
  border-bottom: 1px solid var(--color-border);
}

.rankings-loading-grid {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: var(--space-4);
  margin-top: var(--space-4);
}

@media (max-width: 64rem) {
  .rankings-loading-rail-inner {
    grid-template-columns: repeat(5, minmax(10rem, 1fr));
    overflow: hidden;
  }

  .rankings-loading-title {
    display: none;
  }
}

@media (max-width: 40rem) {
  .rankings-loading-grid {
    grid-template-columns: 1fr;
  }
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

.refresh-banner-row .btn {
  margin-left: auto;
}

.refresh-banner--error {
  border-bottom-color: var(--color-negative);
}

.refresh-banner--error .refresh-banner-row {
  color: var(--color-negative);
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
