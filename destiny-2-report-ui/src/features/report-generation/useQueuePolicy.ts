import { computed, onMounted, onUnmounted, ref } from 'vue'
import { fetchQueuePolicy, type QueuePolicyResponse } from '@/lib/api/reports'

const RETRY_DELAYS_MS = [500, 1_000] as const

export function useQueuePolicy() {
  const policy = ref<QueuePolicyResponse | null>(null)
  const status = ref<'idle' | 'loading' | 'ready' | 'error'>('idle')
  let retryTimer: ReturnType<typeof setTimeout> | null = null
  let requestVersion = 0

  function clearRetryTimer() {
    if (retryTimer !== null) {
      clearTimeout(retryTimer)
      retryTimer = null
    }
  }

  async function loadAttempt(attempt: number, version: number) {
    try {
      const result = await fetchQueuePolicy()
      if (version !== requestVersion) return
      policy.value = result
      status.value = 'ready'
    } catch {
      if (version !== requestVersion) return

      const retryDelay = RETRY_DELAYS_MS[attempt]
      if (retryDelay !== undefined) {
        retryTimer = setTimeout(() => {
          retryTimer = null
          void loadAttempt(attempt + 1, version)
        }, retryDelay)
        return
      }

      status.value = 'error'
    }
  }

  function retry() {
    clearRetryTimer()
    requestVersion += 1
    policy.value = null
    status.value = 'loading'
    void loadAttempt(0, requestVersion)
  }

  onMounted(retry)
  onUnmounted(() => {
    requestVersion += 1
    clearRetryTimer()
  })

  return {
    policy,
    isLoading: computed(() => status.value === 'idle' || status.value === 'loading'),
    hasError: computed(() => status.value === 'error'),
    retry,
  }
}
