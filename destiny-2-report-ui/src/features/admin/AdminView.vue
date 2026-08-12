<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import AppButton from '@/components/base/AppButton.vue'
import {
  flushMongoQueue,
  flushRedisQueue,
  queuePriorityCrawls,
  setAllFullRecrawl,
  watchAdminOverview,
} from '@/lib/api/admin'
import type { AdminMutationResponse, AdminOverview } from '@/lib/api/types'
import { getErrorMessage } from '@/lib/api/http'
import { formatInteger } from '@/lib/formatting/numbers'

const overview = ref<AdminOverview | null>(null)
const streamState = ref<'connecting' | 'live' | 'retrying'>('connecting')
const busyAction = ref<string | null>(null)
const actionMessage = ref<string | null>(null)
const actionError = ref<string | null>(null)
const recrawlReason = ref('')
const priorityMemberships = ref('')
const priorityForceFullCrawl = ref(true)

let controller: AbortController | null = null
let retryTimer: ReturnType<typeof setTimeout> | null = null

const statusCounts = computed(() => overview.value?.statusCounts ?? [])
const activeCrawls = computed(() => overview.value?.activeCrawls ?? [])

function connect() {
  controller?.abort()
  controller = new AbortController()
  streamState.value = overview.value ? 'retrying' : 'connecting'
  void watchAdminOverview(controller.signal, (nextOverview) => {
    overview.value = nextOverview
    streamState.value = 'live'
  }).catch(() => {
    if (controller?.signal.aborted) return
    streamState.value = 'retrying'
    retryTimer = setTimeout(connect, 2_000)
  })
}

function formatTimestamp(value: string | null): string {
  if (!value) return '—'
  return new Intl.DateTimeFormat(undefined, {
    hour: 'numeric',
    minute: '2-digit',
    second: '2-digit',
  }).format(new Date(value))
}

function playerLabel(displayName: string | null, membershipId: string): string {
  return displayName?.trim() || membershipId
}

function progressPercent(current: number | null, total: number | null): number | null {
  if (current === null || total === null || total <= 0) return null
  return Math.min(100, Math.max(0, (current / total) * 100))
}

function progressDetail(current: number | null, total: number | null): string | null {
  if (current === null || total === null) return null
  return `${formatInteger(current)} of ${formatInteger(total)}`
}

async function runAction(
  key: string,
  confirmation: string,
  action: () => Promise<AdminMutationResponse>,
) {
  if (!window.confirm(confirmation)) return
  busyAction.value = key
  actionMessage.value = null
  actionError.value = null
  try {
    const result = await action()
    actionMessage.value = result.message
  } catch (error) {
    actionError.value = getErrorMessage(error, 'The admin action failed.')
  } finally {
    busyAction.value = null
  }
}

function submitFullRecrawl() {
  const reason = recrawlReason.value.trim()
  if (!reason) {
    actionError.value = 'Enter a reason before marking reports for recrawl.'
    return
  }
  void runAction(
    'recrawl',
    `Mark every stored player for a full recrawl?\n\nReason: ${reason}`,
    async () => {
      const result = await setAllFullRecrawl(reason)
      recrawlReason.value = ''
      return result
    },
  )
}

function parsePriorityMemberships():
  | { memberships: { membershipTypeId: number; membershipId: string }[]; error: null }
  | { memberships: []; error: string } {
  const lines = priorityMemberships.value
    .split(/\r?\n/)
    .map((line) => line.trim())
    .filter(Boolean)
  if (lines.length === 0) return { memberships: [], error: 'Enter at least one membership.' }
  if (lines.length > 100) return { memberships: [], error: 'Enter no more than 100 memberships.' }

  const memberships: { membershipTypeId: number; membershipId: string }[] = []
  const seen = new Set<string>()
  for (const [index, line] of lines.entries()) {
    const parts = line.split(/[\s,/]+/)
    const membershipTypeId = Number(parts[0])
    const membershipId = parts[1] ?? ''
    if (
      parts.length !== 2 ||
      !Number.isInteger(membershipTypeId) ||
      membershipTypeId <= 0 ||
      !/^\d+$/.test(membershipId) ||
      membershipId === '0'
    ) {
      return {
        memberships: [],
        error: `Line ${index + 1} must contain a positive membership type and membership ID.`,
      }
    }
    const key = `${membershipTypeId}:${membershipId}`
    if (!seen.has(key)) {
      seen.add(key)
      memberships.push({ membershipTypeId, membershipId })
    }
  }
  return { memberships, error: null }
}

async function submitPriorityCrawls() {
  const parsed = parsePriorityMemberships()
  if (parsed.error) {
    actionError.value = parsed.error
    return
  }

  busyAction.value = 'priority'
  actionMessage.value = null
  actionError.value = null
  try {
    const responses = await queuePriorityCrawls(
      parsed.memberships.map((membership) => ({
        ...membership,
        forceFullCrawl: priorityForceFullCrawl.value,
      })),
    )
    actionMessage.value = `Scheduled ${responses.length} player${responses.length === 1 ? '' : 's'} for priority crawling.`
    priorityMemberships.value = ''
  } catch (error) {
    actionError.value = getErrorMessage(error, 'The priority crawls could not be scheduled.')
  } finally {
    busyAction.value = null
  }
}

onMounted(connect)
onBeforeUnmount(() => {
  controller?.abort()
  if (retryTimer) clearTimeout(retryTimer)
})
</script>

<template>
  <div class="admin container">
    <header class="admin-masthead">
      <div>
        <p class="eyebrow">Restricted systems console</p>
        <h1 class="display">Crawl operations</h1>
        <p class="lede">Live worker state, queue pressure, and fleet-wide maintenance controls.</p>
      </div>
      <div class="connection" :class="`connection--${streamState}`" role="status">
        <span class="connection-dot" aria-hidden="true" />
        {{
          streamState === 'live'
            ? 'Live feed'
            : streamState === 'retrying'
              ? 'Reconnecting'
              : 'Connecting'
        }}
      </div>
    </header>

    <section class="status-strip" aria-labelledby="queue-status-heading">
      <h2 id="queue-status-heading" class="visually-hidden">Players by crawl status</h2>
      <article v-for="item in statusCounts" :key="item.status" class="status-cell">
        <span class="status-label">{{ item.status }}</span>
        <strong class="status-value display tnum">{{ formatInteger(item.count) }}</strong>
      </article>
      <article v-if="statusCounts.length === 0" class="status-cell status-cell--loading">
        Loading queue totals…
      </article>
    </section>

    <div class="operations-grid">
      <section class="panel active-panel" aria-labelledby="active-heading">
        <div class="panel-heading">
          <div>
            <p class="panel-kicker">Workers</p>
            <h2 id="active-heading">Actively crawling</h2>
          </div>
          <span class="active-count tnum">{{ activeCrawls.length }} active</span>
        </div>

        <div class="table-wrap">
          <table>
            <thead>
              <tr>
                <th>Player</th>
                <th>Source</th>
                <th>Started</th>
                <th>Progress</th>
                <th>Lease / worker</th>
              </tr>
            </thead>
            <tbody>
              <tr
                v-for="crawl in activeCrawls"
                :key="`${crawl.membershipTypeId}:${crawl.membershipId}`"
              >
                <td>
                  <RouterLink
                    class="player-link"
                    :to="{
                      name: 'report-overview',
                      params: {
                        membershipTypeId: crawl.membershipTypeId,
                        membershipId: crawl.membershipId,
                      },
                    }"
                  >
                    {{ playerLabel(crawl.displayName, crawl.membershipId) }}
                  </RouterLink>
                  <span class="membership tnum"
                    >{{ crawl.membershipTypeId }} / {{ crawl.membershipId }}</span
                  >
                </td>
                <td>
                  <span class="source-chip" :class="{ 'source-chip--priority': crawl.isPriority }">
                    {{ crawl.isPriority ? 'Priority' : crawl.queuedInRedis ? 'Redis' : 'Mongo' }}
                  </span>
                </td>
                <td class="tnum">{{ formatTimestamp(crawl.startedAtUtc) }}</td>
                <td class="progress-cell">
                  <template v-if="crawl.progress">
                    <div class="crawl-progress-heading">
                      <span>{{ crawl.progress.label }}</span>
                      <span
                        v-if="progressDetail(crawl.progress.current, crawl.progress.total)"
                        class="crawl-progress-detail tnum"
                      >
                        {{ progressDetail(crawl.progress.current, crawl.progress.total) }}
                      </span>
                    </div>
                    <div
                      class="crawl-progress-track"
                      role="progressbar"
                      aria-label="Player crawl progress"
                      :aria-valuemin="0"
                      :aria-valuemax="100"
                      :aria-valuenow="
                        progressPercent(crawl.progress.current, crawl.progress.total) ?? undefined
                      "
                      :aria-valuetext="
                        progressPercent(crawl.progress.current, crawl.progress.total) === null
                          ? crawl.progress.label
                          : undefined
                      "
                    >
                      <span
                        v-if="
                          progressPercent(crawl.progress.current, crawl.progress.total) !== null
                        "
                        class="crawl-progress-fill"
                        :style="{
                          width: `${progressPercent(crawl.progress.current, crawl.progress.total)}%`,
                        }"
                      />
                      <span v-else class="crawl-progress-fill crawl-progress-fill--indeterminate" />
                    </div>
                  </template>
                  <span v-else class="progress-unavailable">Waiting for worker…</span>
                </td>
                <td>
                  <span class="lease tnum">
                    {{
                      crawl.queuedInRedis
                        ? 'stream heartbeat'
                        : `until ${formatTimestamp(crawl.leaseExpiresAtUtc)}`
                    }}
                  </span>
                  <span v-if="crawl.leaseOwner" class="worker">{{ crawl.leaseOwner }}</span>
                </td>
              </tr>
              <tr v-if="overview && activeCrawls.length === 0">
                <td colspan="5" class="empty-row">No workers hold an active crawl.</td>
              </tr>
              <tr v-if="!overview">
                <td colspan="5" class="empty-row">Waiting for the first worker snapshot…</td>
              </tr>
            </tbody>
          </table>
        </div>
      </section>

      <aside class="control-stack" aria-label="Administrative controls">
        <section class="panel control-panel priority-panel">
          <p class="panel-kicker">Expedite lane</p>
          <h2>Priority crawl</h2>
          <p>
            Moves affected players ahead of public API requests. Enter one membership per line as
            <code>type / membership</code>.
          </p>
          <label for="priority-memberships">Memberships</label>
          <textarea
            id="priority-memberships"
            v-model="priorityMemberships"
            rows="5"
            spellcheck="false"
            placeholder="3 / 4611686018517092651&#10;2 / 4611686018472005626"
          />
          <label class="checkbox-row">
            <input v-model="priorityForceFullCrawl" type="checkbox" />
            <span>
              Force a full crawl
              <small>Recommended when investigating incorrect or incomplete report data.</small>
            </span>
          </label>
          <AppButton
            variant="primary"
            :disabled="busyAction !== null || !priorityMemberships.trim()"
            @click="submitPriorityCrawls"
          >
            {{ busyAction === 'priority' ? 'Scheduling…' : 'Schedule priority crawl' }}
          </AppButton>
        </section>

        <section class="panel control-panel">
          <p class="panel-kicker">Queue controls</p>
          <h2>Flush waiting jobs</h2>
          <p>Removes queued work only. Crawls already held by a worker continue to completion.</p>
          <div class="button-stack">
            <AppButton
              :disabled="busyAction !== null"
              @click="
                runAction(
                  'redis',
                  'Remove every waiting job from the Redis crawl queue?',
                  flushRedisQueue,
                )
              "
            >
              {{ busyAction === 'redis' ? 'Flushing Redis…' : 'Flush Redis queue' }}
            </AppButton>
            <AppButton
              :disabled="busyAction !== null"
              @click="
                runAction(
                  'mongo',
                  'Remove every waiting job from the Mongo background queue?',
                  flushMongoQueue,
                )
              "
            >
              {{ busyAction === 'mongo' ? 'Flushing Mongo…' : 'Flush Mongo queue' }}
            </AppButton>
          </div>
        </section>

        <section class="panel control-panel control-panel--danger">
          <p class="panel-kicker">Fleet maintenance</p>
          <h2>Force full recrawl</h2>
          <p>
            Marks every stored player so their next crawl rebuilds their complete activity history.
          </p>
          <label for="recrawl-reason">Reason</label>
          <textarea
            id="recrawl-reason"
            v-model="recrawlReason"
            maxlength="500"
            rows="3"
            placeholder="Schema migration, aggregate repair…"
          />
          <div class="reason-meta">
            <span>Required</span><span class="tnum">{{ recrawlReason.length }} / 500</span>
          </div>
          <AppButton
            variant="primary"
            :disabled="busyAction !== null || !recrawlReason.trim()"
            @click="submitFullRecrawl"
          >
            {{ busyAction === 'recrawl' ? 'Applying flag…' : 'Mark all for full recrawl' }}
          </AppButton>
        </section>

        <p v-if="actionMessage" class="action-feedback action-feedback--success" role="status">
          {{ actionMessage }}
        </p>
        <p v-if="actionError" class="action-feedback action-feedback--error" role="alert">
          {{ actionError }}
        </p>
      </aside>
    </div>
  </div>
</template>

<style scoped>
.admin {
  padding-top: var(--space-7);
}
.admin-masthead {
  display: flex;
  align-items: flex-end;
  justify-content: space-between;
  gap: var(--space-5);
  margin-bottom: var(--space-6);
}
.eyebrow,
.panel-kicker {
  color: var(--color-accent);
  font-size: var(--text-xs);
  font-weight: 650;
  letter-spacing: 0.12em;
  text-transform: uppercase;
}
h1 {
  margin-top: var(--space-1);
  font-size: clamp(2rem, 5vw, 3.5rem);
  letter-spacing: -0.04em;
}
.lede {
  margin-top: var(--space-2);
  color: var(--color-text-secondary);
}
.connection {
  display: inline-flex;
  align-items: center;
  gap: var(--space-2);
  padding: var(--space-2) var(--space-3);
  border: 1px solid var(--color-border);
  border-radius: var(--radius-full);
  color: var(--color-text-secondary);
  font-size: var(--text-xs);
  white-space: nowrap;
}
.connection-dot {
  width: 0.5rem;
  height: 0.5rem;
  border-radius: 50%;
  background: var(--color-warning);
}
.connection--live .connection-dot {
  background: var(--color-positive);
  box-shadow: 0 0 0 4px rgb(112 183 133 / 0.12);
}
.status-strip {
  display: grid;
  grid-template-columns: repeat(5, 1fr);
  border-block: 1px solid var(--color-border-strong);
  margin-bottom: var(--space-6);
}
.status-cell {
  display: flex;
  flex-direction: column;
  gap: var(--space-1);
  padding: var(--space-4);
  border-right: 1px solid var(--color-border);
}
.status-cell:last-child {
  border-right: 0;
}
.status-label {
  color: var(--color-text-muted);
  font-size: var(--text-xs);
  text-transform: uppercase;
  letter-spacing: 0.08em;
}
.status-value {
  font-size: var(--text-2xl);
}
.status-cell--loading {
  grid-column: 1 / -1;
  color: var(--color-text-muted);
}
.operations-grid {
  display: grid;
  grid-template-columns: minmax(0, 1fr) 20rem;
  gap: var(--space-5);
  align-items: start;
}
.panel {
  background:
    linear-gradient(145deg, rgb(255 255 255 / 0.025), transparent 45%), var(--color-surface);
  border: 1px solid var(--color-border);
  border-radius: var(--radius-md);
}
.panel-heading {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: var(--space-4);
  padding: var(--space-5);
  border-bottom: 1px solid var(--color-border);
}
h2 {
  margin-top: var(--space-1);
  font-size: var(--text-lg);
}
.active-count {
  color: var(--color-positive);
  font-size: var(--text-sm);
}
.table-wrap {
  overflow-x: auto;
}
th {
  padding: var(--space-3) var(--space-4);
  color: var(--color-text-muted);
  border-bottom: 1px solid var(--color-border);
  font-size: var(--text-xs);
  text-transform: uppercase;
  letter-spacing: 0.06em;
}
td {
  padding: var(--space-4);
  border-bottom: 1px solid var(--color-border);
  color: var(--color-text-secondary);
  font-size: var(--text-sm);
  vertical-align: top;
}
tbody tr:last-child td {
  border-bottom: 0;
}
.player-link {
  color: var(--color-text);
  font-weight: 600;
}
.membership,
.worker {
  display: block;
  margin-top: var(--space-1);
  color: var(--color-text-muted);
  font-size: var(--text-xs);
}
.source-chip {
  display: inline-flex;
  padding: 0.15rem 0.45rem;
  border: 1px solid var(--color-border-strong);
  border-radius: var(--radius-sm);
  color: var(--color-text);
  font-size: var(--text-xs);
}
.source-chip--priority {
  color: var(--color-accent);
  border-color: color-mix(in srgb, var(--color-accent) 55%, transparent);
  background: color-mix(in srgb, var(--color-accent) 9%, transparent);
}
.lease {
  white-space: nowrap;
}
.progress-cell {
  min-width: 13rem;
}
.crawl-progress-heading {
  display: flex;
  justify-content: space-between;
  gap: var(--space-3);
  color: var(--color-text);
  font-size: var(--text-xs);
}
.crawl-progress-detail,
.progress-unavailable {
  color: var(--color-text-muted);
  white-space: nowrap;
}
.crawl-progress-track {
  height: 0.35rem;
  margin-top: var(--space-2);
  overflow: hidden;
  background: var(--color-surface-sunken);
  border-radius: var(--radius-full);
}
.crawl-progress-fill {
  display: block;
  height: 100%;
  background: var(--color-accent);
  border-radius: inherit;
  transition: width 300ms ease;
}
.crawl-progress-fill--indeterminate {
  width: 35%;
  animation: crawl-progress-slide 1.4s ease-in-out infinite;
}
@keyframes crawl-progress-slide {
  from {
    transform: translateX(-100%);
  }
  to {
    transform: translateX(300%);
  }
}
.worker {
  max-width: 16rem;
  overflow: hidden;
  text-overflow: ellipsis;
}
.empty-row {
  padding-block: var(--space-7);
  text-align: center;
  color: var(--color-text-muted);
}
.control-stack {
  display: grid;
  gap: var(--space-4);
}
.control-panel {
  padding: var(--space-5);
}
.control-panel p:not(.panel-kicker) {
  margin-top: var(--space-2);
  color: var(--color-text-secondary);
  font-size: var(--text-sm);
}
.button-stack {
  display: grid;
  gap: var(--space-2);
  margin-top: var(--space-4);
}
.control-panel--danger {
  border-top-color: var(--color-negative);
}
.priority-panel {
  border-top-color: var(--color-accent);
}
.priority-panel code {
  color: var(--color-text);
  font-size: var(--text-xs);
}
.checkbox-row {
  display: flex;
  align-items: flex-start;
  gap: var(--space-2);
  margin: var(--space-3) 0 var(--space-4);
  cursor: pointer;
}
.checkbox-row input {
  margin-top: 0.2rem;
  accent-color: var(--color-accent);
}
.checkbox-row span {
  display: grid;
  gap: 0.15rem;
}
.checkbox-row small {
  color: var(--color-text-muted);
  font-size: var(--text-xs);
  font-weight: 400;
  line-height: 1.4;
}
label {
  display: block;
  margin-top: var(--space-4);
  font-size: var(--text-sm);
  font-weight: 600;
}
textarea {
  width: 100%;
  margin-top: var(--space-2);
  padding: var(--space-3);
  resize: vertical;
  background: var(--color-surface-sunken);
  border: 1px solid var(--color-border-strong);
  border-radius: var(--radius-sm);
  color: var(--color-text);
  font: inherit;
}
.reason-meta {
  display: flex;
  justify-content: space-between;
  margin: var(--space-1) 0 var(--space-3);
  color: var(--color-text-muted);
  font-size: var(--text-xs);
}
.action-feedback {
  padding: var(--space-3);
  border: 1px solid;
  border-radius: var(--radius-sm);
  font-size: var(--text-sm);
}
.action-feedback--success {
  color: var(--color-positive);
  border-color: rgb(112 183 133 / 0.35);
  background: rgb(112 183 133 / 0.08);
}
.action-feedback--error {
  color: var(--color-negative);
  border-color: rgb(216 102 94 / 0.35);
  background: rgb(216 102 94 / 0.08);
}
@media (max-width: 52rem) {
  .operations-grid {
    grid-template-columns: 1fr;
  }
  .control-stack {
    grid-template-columns: repeat(2, minmax(0, 1fr));
  }
  .action-feedback {
    grid-column: 1 / -1;
  }
}
@media (max-width: 40rem) {
  .admin-masthead {
    align-items: flex-start;
    flex-direction: column;
  }
  .status-strip {
    grid-template-columns: repeat(2, 1fr);
  }
  .status-cell {
    border-bottom: 1px solid var(--color-border);
  }
  .control-stack {
    grid-template-columns: 1fr;
  }
  .action-feedback {
    grid-column: auto;
  }
}
@media (prefers-reduced-motion: reduce) {
  .crawl-progress-fill--indeterminate {
    animation: none;
  }
}
</style>
