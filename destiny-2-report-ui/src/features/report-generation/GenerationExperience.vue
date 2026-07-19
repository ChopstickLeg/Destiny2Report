<script setup lang="ts">
import { computed, onMounted } from 'vue'
import AppButton from '@/components/base/AppButton.vue'
import ErrorState from '@/components/base/ErrorState.vue'
import type { ReportIdentity } from '@/lib/api/reports'
import type { CrawlState } from '@/lib/api/types'
import CrawlProgressPanel from './CrawlProgressPanel.vue'
import ReportReadyNotification from './ReportReadyNotification.vue'
import { useQueueWatcher } from './useQueueWatcher'

const props = defineProps<{
  identity: ReportIdentity
  /** Server-known state when the page opened; 'missing' means 404. */
  initialState: 'missing' | CrawlState
  playerName: string | null
  crawlError: string
}>()

const emit = defineEmits<{ refresh: [] }>()

const identityRef = computed(() => props.identity)

const watcher = useQueueWatcher(identityRef, {
  onCompleted: () => emit('refresh'),
})

onMounted(() => {
  // A crawl is already in flight server-side; attach to it immediately.
  if (props.initialState === 'queued' || props.initialState === 'running') {
    void watcher.watch()
  }
})

type PanelKind = 'generate' | 'progress' | 'failed' | 'private' | 'lost'

const panel = computed<PanelKind>(() => {
  switch (watcher.phase.value) {
    case 'submitting':
    case 'watching':
      return 'progress'
    case 'failed':
      return 'failed'
    case 'private':
      return 'private'
    case 'connection-lost':
    case 'not-found':
      return 'lost'
    case 'submit-error':
      return 'generate'
    case 'completed':
      return 'progress' // parent is refetching; keep the panel stable
    case 'idle':
      break
  }
  if (props.initialState === 'failed') return 'failed'
  if (props.initialState === 'private') return 'private'
  return 'generate'
})

const failureDetail = computed(() => watcher.latest.value?.error || props.crawlError || null)

const heading = computed(() => {
  if (props.playerName) return props.playerName
  return 'This player'
})
</script>

<template>
  <div class="generation container">
    <div class="generation-panel">
      <template v-if="panel === 'generate'">
        <h1 class="generation-title">No report yet</h1>
        <p class="generation-copy">
          {{ heading }} hasn't been crawled. Generating a report walks their entire public Destiny 2
          history, including every activity, weapon, and teammate, and stores it for anyone to view.
        </p>
        <ErrorState
          v-if="watcher.submitError.value"
          class="generation-error"
          :error="watcher.submitError.value"
          context="Couldn't queue the report"
          @retry="watcher.submitAndWatch()"
        />
        <div class="generation-actions">
          <AppButton variant="primary" @click="watcher.submitAndWatch()">Generate report</AppButton>
        </div>
      </template>

      <template v-else-if="panel === 'progress'">
        <h1 class="generation-title">Building the report</h1>
        <CrawlProgressPanel
          :latest="watcher.latest.value"
          :reconnect-attempt="watcher.reconnectAttempt.value"
          :started-at="watcher.startedAt.value"
        />
        <ReportReadyNotification :identity="identity" />
      </template>

      <template v-else-if="panel === 'failed'">
        <h1 class="generation-title">The crawl failed</h1>
        <p class="generation-copy">
          Something went wrong while walking this player's history. This is usually temporary.
          Bungie's API may have been unavailable partway through.
        </p>
        <p v-if="failureDetail" class="generation-detail">{{ failureDetail }}</p>
        <div class="generation-actions">
          <AppButton variant="primary" @click="watcher.submitAndWatch()">Try again</AppButton>
        </div>
      </template>

      <template v-else-if="panel === 'private'">
        <h1 class="generation-title">This profile is private</h1>
        <p class="generation-copy">
          Bungie's privacy settings prevent reading this account's activity history, so a report
          can't be built. If this is your account, you can allow it: on bungie.net go to
          <strong>Settings → Privacy</strong> and enable
          <em>“Show my Destiny game Activity feed on Bungie.net.”</em>
        </p>
        <div class="generation-actions">
          <AppButton variant="secondary" @click="watcher.submitAndWatch()">
            I've updated my settings. Try again
          </AppButton>
        </div>
      </template>

      <template v-else>
        <h1 class="generation-title">Lost track of the crawl</h1>
        <p class="generation-copy">
          The live progress connection couldn't be re-established. The crawl may still have finished
          on the server.
        </p>
        <div class="generation-actions">
          <AppButton variant="primary" @click="emit('refresh')">Check for the report</AppButton>
        </div>
      </template>
    </div>
  </div>
</template>

<style scoped>
.generation {
  display: flex;
  justify-content: center;
  padding-top: var(--space-8);
}

.generation-panel {
  width: 100%;
  max-width: 34rem;
  padding: var(--space-6);
  background: var(--color-surface);
  border: 1px solid var(--color-border);
  border-radius: var(--radius-md);
}

.generation-title {
  font-size: var(--text-xl);
  margin-bottom: var(--space-3);
}

.generation-copy {
  color: var(--color-text-secondary);
  font-size: var(--text-sm);
}

.generation-detail {
  margin-top: var(--space-3);
  padding: var(--space-3);
  background: var(--color-surface-sunken);
  border-radius: var(--radius-sm);
  font-size: var(--text-xs);
  color: var(--color-text-muted);
  font-family: ui-monospace, monospace;
  overflow-wrap: anywhere;
}

.generation-error {
  margin-top: var(--space-4);
}

.generation-actions {
  margin-top: var(--space-5);
  display: flex;
  gap: var(--space-3);
}
</style>
