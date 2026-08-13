<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { useRoute } from 'vue-router'
import AppButton from '@/components/base/AppButton.vue'
import ErrorState from '@/components/base/ErrorState.vue'
import { isApiError } from '@/lib/api/http'
import { fetchQueuePolicy, type QueuePolicyResponse, type ReportIdentity } from '@/lib/api/reports'
import type { CrawlState } from '@/lib/api/types'
import { useSessionStore } from '@/stores/session'
import CrawlProgressPanel from './CrawlProgressPanel.vue'
import ReportReadyNotification from './ReportReadyNotification.vue'
import { useQueueWatcher } from './useQueueWatcher'

const props = defineProps<{
  identity: ReportIdentity
  /** Server-known state when the page opened; 'missing' means 404. */
  initialState: 'missing' | CrawlState
  playerName: string | null
  crawlError: string
  /** Start a genuinely missing report immediately when navigation expressed that intent. */
  autoStart?: boolean
}>()

const emit = defineEmits<{ refresh: [] }>()

const identityRef = computed(() => props.identity)
const route = useRoute()
const session = useSessionStore()
const queuePolicy = ref<QueuePolicyResponse | null>(null)
const initialQueueActionStarted = ref(false)

const watcher = useQueueWatcher(identityRef, {
  onCompleted: () => emit('refresh'),
})

const serverRequiresSignIn = computed(
  () =>
    queuePolicy.value?.authenticationRequired === true ||
    (isApiError(watcher.submitError.value) &&
      watcher.submitError.value.problem?.code === 'queue_authentication_required'),
)
const sessionResolved = computed(
  () => session.status !== 'unknown' && session.status !== 'resolving',
)
const queueAccessReady = computed(() => queuePolicy.value !== null && sessionResolved.value)
const queueAccessPending = computed(() => !queueAccessReady.value)
const needsSignIn = computed(
  () => queueAccessReady.value && serverRequiresSignIn.value && !session.isSignedIn,
)

function signIn() {
  session.beginSignIn(route.fullPath)
}

function requestQueue() {
  if (!queueAccessReady.value) return
  if (needsSignIn.value) {
    signIn()
    return
  }
  void watcher.submitAndWatch()
}

function startInitialQueueAction() {
  if (!queueAccessReady.value || initialQueueActionStarted.value) return

  if (props.initialState === 'queued') {
    initialQueueActionStarted.value = true
    if (needsSignIn.value) {
      void watcher.watch()
    } else {
      void watcher.submitAndWatch({
        suppressCooldownError: true,
        watchOnSuppressedCooldown: true,
      })
    }
  } else if (props.initialState === 'running') {
    initialQueueActionStarted.value = true
    void watcher.watch()
  } else if (props.initialState === 'missing' && props.autoStart && !needsSignIn.value) {
    initialQueueActionStarted.value = true
    void watcher.submitAndWatch()
  }
}

watch([queueAccessReady, needsSignIn], startInitialQueueAction, { immediate: true })

onMounted(async () => {
  // Watching an existing crawl is read-only and must remain available while
  // queue access policy or session state is still resolving.
  if (props.initialState === 'running') {
    initialQueueActionStarted.value = true
    void watcher.watch()
  }

  try {
    queuePolicy.value = await fetchQueuePolicy()
  } catch {
    // Fail closed. Queue controls remain disabled until policy discovery succeeds.
    if (props.initialState === 'queued') void watcher.watch()
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
    <div class="generation-content">
      <template v-if="panel === 'generate'">
        <h1 class="generation-title">No report yet</h1>
        <p class="generation-copy">
          {{ heading }} hasn't been crawled. Generating a report walks their entire public Destiny 2
          history, including every activity, weapon, and teammate, and stores it for anyone to view.
        </p>
        <p v-if="serverRequiresSignIn" class="generation-auth-note">
          You must sign in with Bungie before you can queue a player for a report crawl.
        </p>
        <ErrorState
          v-if="watcher.submitError.value && !needsSignIn"
          class="generation-error"
          :error="watcher.submitError.value"
          context="Couldn't queue the report"
          @retry="requestQueue"
        />
        <div class="generation-actions">
          <AppButton v-if="queueAccessPending" variant="primary" disabled>
            Checking queue access…
          </AppButton>
          <AppButton v-else-if="needsSignIn" variant="primary" @click="signIn">
            Sign in with Bungie
          </AppButton>
          <AppButton v-else variant="primary" @click="requestQueue"> Generate report </AppButton>
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
          <AppButton variant="primary" :disabled="queueAccessPending" @click="requestQueue">
            {{
              queueAccessPending
                ? 'Checking queue access…'
                : needsSignIn
                  ? 'Sign in to retry'
                  : 'Try again'
            }}
          </AppButton>
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
          <AppButton variant="secondary" :disabled="queueAccessPending" @click="requestQueue">
            {{
              queueAccessPending
                ? 'Checking queue access…'
                : needsSignIn
                  ? 'Sign in to retry'
                  : "I've updated my settings. Try again"
            }}
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
  display: grid;
  min-height: min(42rem, calc(100vh - 3.5rem));
  place-items: center;
  padding-block: var(--space-8);
}

.generation-content {
  width: 100%;
  max-width: 42rem;
  text-align: center;
}

.generation-title {
  margin-bottom: var(--space-4);
  font-size: clamp(var(--text-xl), 4vw, var(--text-2xl));
  line-height: 1.1;
}

.generation-copy {
  max-width: 38rem;
  margin-inline: auto;
  color: var(--color-text-secondary);
  line-height: 1.65;
}

.generation-detail {
  margin: var(--space-4) auto 0;
  padding-left: var(--space-3);
  color: var(--color-text-muted);
  font-family: ui-monospace, monospace;
  font-size: var(--text-xs);
  text-align: left;
  border-left: 2px solid var(--color-border-strong);
  overflow-wrap: anywhere;
}

.generation-auth-note {
  max-width: 38rem;
  margin: var(--space-4) auto 0;
  color: var(--color-text-secondary);
  font-size: var(--text-sm);
}

.generation-error {
  margin-top: var(--space-4);
  text-align: left;
}

.generation-actions {
  display: flex;
  justify-content: center;
  gap: var(--space-3);
  margin-top: var(--space-5);
}

.generation-content :deep(.progress) {
  margin-top: var(--space-5);
  text-align: left;
}

.generation-content :deep(.ready-notice) {
  text-align: left;
}
</style>
