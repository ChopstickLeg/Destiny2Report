<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import AppButton from '@/components/base/AppButton.vue'
import type { ReportIdentity } from '@/lib/api/reports'
import {
  fetchPushNotificationConfig,
  fetchReportPushSubscriptionStatus,
  registerReportPushSubscription,
  removeReportPushSubscription,
} from '@/lib/api/push-notifications'
import {
  canUseWebPush,
  ensurePushServiceWorker,
  subscriptionUsesKey,
  urlBase64ToUint8Array,
} from '@/lib/push-service-worker'

const props = defineProps<{ identity: ReportIdentity }>()

type NotificationState =
  | 'checking'
  | 'hidden'
  | 'idle'
  | 'enabling'
  | 'enabled'
  | 'disabling'
  | 'denied'
  | 'error'

const state = ref<NotificationState>('checking')
const publicKey = ref<string | null>(null)
const errorMessage = ref('')

const busy = computed(() => state.value === 'enabling' || state.value === 'disabling')

onMounted(async () => {
  if (!canUseWebPush()) {
    state.value = 'hidden'
    return
  }

  try {
    const config = await fetchPushNotificationConfig()
    if (!config.enabled || !config.publicKey) {
      state.value = 'hidden'
      return
    }

    publicKey.value = config.publicKey
    const registration = await ensurePushServiceWorker()
    const subscription = await registration.pushManager.getSubscription()
    if (!subscription) {
      state.value = Notification.permission === 'denied' ? 'denied' : 'idle'
      return
    }

    const status = await fetchReportPushSubscriptionStatus(props.identity, subscription.endpoint)
    state.value = status.registered
      ? 'enabled'
      : Notification.permission === 'denied'
        ? 'denied'
        : 'idle'
  } catch {
    // A progress connection should remain useful even if optional push setup is unavailable.
    state.value = 'hidden'
  }
})

async function enableNotifications() {
  if (!publicKey.value) return

  state.value = 'enabling'
  errorMessage.value = ''
  let newlyCreated: PushSubscription | null = null

  try {
    const permission =
      Notification.permission === 'default'
        ? await Notification.requestPermission()
        : Notification.permission
    if (permission !== 'granted') {
      state.value = 'denied'
      return
    }

    const registration = await ensurePushServiceWorker()
    const applicationServerKey = urlBase64ToUint8Array(publicKey.value)
    let subscription = await registration.pushManager.getSubscription()

    if (subscription && !subscriptionUsesKey(subscription, applicationServerKey)) {
      await removeReportPushSubscription(props.identity, subscription.endpoint).catch(() => undefined)
      await subscription.unsubscribe()
      subscription = null
    }

    if (!subscription) {
      newlyCreated = await registration.pushManager.subscribe({
        userVisibleOnly: true,
        applicationServerKey,
      })
      subscription = newlyCreated
    }

    await registerReportPushSubscription(props.identity, subscription)
    state.value = 'enabled'
  } catch (error) {
    if (newlyCreated) await newlyCreated.unsubscribe().catch(() => false)
    errorMessage.value = error instanceof Error ? error.message : 'Notifications could not be enabled.'
    state.value = 'error'
  }
}

async function disableNotifications() {
  state.value = 'disabling'
  errorMessage.value = ''

  try {
    const registration = await ensurePushServiceWorker()
    const subscription = await registration.pushManager.getSubscription()
    if (subscription) {
      await removeReportPushSubscription(props.identity, subscription.endpoint)
    }
    state.value = 'idle'
  } catch (error) {
    errorMessage.value = error instanceof Error ? error.message : 'Notifications could not be changed.'
    state.value = 'error'
  }
}
</script>

<template>
  <aside v-if="state !== 'hidden' && state !== 'checking'" class="ready-notice" aria-live="polite">
    <div class="ready-icon" aria-hidden="true">
      <svg viewBox="0 0 24 24" fill="none">
        <path d="M7.5 9.75a4.5 4.5 0 0 1 9 0c0 5.25 2.25 5.25 2.25 6.75H5.25c0-1.5 2.25-1.5 2.25-6.75Z" />
        <path d="M9.75 19.25h4.5" />
      </svg>
    </div>

    <div class="ready-copy">
      <p class="ready-title">
        {{ state === 'enabled' ? 'We’ll let you know' : 'You don’t have to wait here' }}
      </p>
      <p v-if="state === 'enabled'" class="ready-detail">
        You can close this tab. This browser will notify you when the report is complete.
      </p>
      <p v-else-if="state === 'denied'" class="ready-detail">
        Notifications are blocked for this site. You can allow them from your browser’s site settings.
      </p>
      <p v-else class="ready-detail">
        Get one browser notification when this crawl finishes, even if you close the tab.
      </p>
      <p v-if="state === 'error' && errorMessage" class="ready-error">{{ errorMessage }}</p>
    </div>

    <AppButton
      v-if="state === 'enabled' || state === 'disabling'"
      variant="ghost"
      size="sm"
      :disabled="busy"
      @click="disableNotifications"
    >
      {{ state === 'disabling' ? 'Turning off…' : 'Turn off' }}
    </AppButton>
    <AppButton
      v-else-if="state !== 'denied'"
      variant="secondary"
      size="sm"
      :disabled="busy"
      @click="enableNotifications"
    >
      {{ state === 'enabling' ? 'Enabling…' : state === 'error' ? 'Try again' : 'Notify me' }}
    </AppButton>
  </aside>
</template>

<style scoped>
.ready-notice {
  margin-top: var(--space-5);
  padding: var(--space-3);
  display: grid;
  grid-template-columns: auto minmax(0, 1fr) auto;
  align-items: center;
  gap: var(--space-3);
  border: 1px solid color-mix(in srgb, var(--color-accent) 28%, var(--color-border));
  border-radius: var(--radius-md);
  background:
    linear-gradient(100deg, color-mix(in srgb, var(--color-accent) 7%, transparent), transparent 56%),
    var(--color-surface-sunken);
}

.ready-icon {
  width: 2rem;
  height: 2rem;
  display: grid;
  place-items: center;
  border: 1px solid color-mix(in srgb, var(--color-accent) 35%, transparent);
  border-radius: var(--radius-full);
  color: var(--color-accent);
  background: var(--color-surface);
}

.ready-icon svg {
  width: 1rem;
  stroke: currentColor;
  stroke-width: 1.7;
  stroke-linecap: round;
  stroke-linejoin: round;
}

.ready-title {
  font-size: var(--text-sm);
  font-weight: 600;
}

.ready-detail,
.ready-error {
  margin-top: 0.15rem;
  font-size: var(--text-xs);
  color: var(--color-text-secondary);
}

.ready-error {
  color: var(--color-negative);
}

@media (max-width: 34rem) {
  .ready-notice {
    grid-template-columns: auto minmax(0, 1fr);
  }

  .ready-notice :deep(.btn) {
    grid-column: 1 / -1;
    width: 100%;
  }
}
</style>
