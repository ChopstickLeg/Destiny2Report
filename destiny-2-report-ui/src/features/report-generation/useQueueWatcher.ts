/**
 * Queue submission + live SSE watching as one composable state machine.
 *
 * Phases:
 *   idle → submitting (POST /reports/queue) → watching (SSE) → terminal
 *
 * The watcher reconnects with bounded backoff on transient drops; when
 * attempts are exhausted it reports `connection-lost` so the caller can
 * fall back to a plain report lookup instead of declaring the crawl dead.
 */

import { computed, onBeforeUnmount, ref, type ComputedRef } from 'vue'
import { queueReport, type ReportIdentity } from '@/lib/api/reports'
import { watchQueue } from '@/lib/api/sse'
import type { ReportQueueStatusResponse } from '@/lib/api/types'

export type QueueWatchPhase =
  | 'idle'
  | 'submitting'
  | 'watching'
  | 'completed'
  | 'failed'
  | 'private'
  | 'not-found'
  | 'connection-lost'
  | 'submit-error'

export function useQueueWatcher(
  identity: ComputedRef<ReportIdentity>,
  options: { onCompleted: () => void },
) {
  const phase = ref<QueueWatchPhase>('idle')
  const latest = ref<ReportQueueStatusResponse | null>(null)
  const submitError = ref<unknown>(null)
  const reconnectAttempt = ref(0)
  const startedAt = ref<number | null>(null)

  let controller: AbortController | null = null

  const isActive = computed(() => phase.value === 'submitting' || phase.value === 'watching')

  function stop() {
    controller?.abort()
    controller = null
  }

  async function watch() {
    stop()
    controller = new AbortController()
    phase.value = 'watching'
    startedAt.value ??= Date.now()
    reconnectAttempt.value = 0

    try {
      await watchQueue(identity.value, controller.signal, {
        onStatus(status) {
          latest.value = status
          reconnectAttempt.value = 0
          if (status.status === 'completed') {
            phase.value = 'completed'
            options.onCompleted()
          } else if (status.status === 'failed') {
            phase.value = 'failed'
          } else if (status.status === 'private') {
            phase.value = 'private'
          }
        },
        onNotFound() {
          phase.value = 'not-found'
        },
        onReconnecting(attempt) {
          reconnectAttempt.value = attempt
        },
      })
    } catch {
      if (!controller?.signal.aborted) {
        phase.value = 'connection-lost'
      }
    }
  }

  /** POST a new crawl request, then watch it. */
  async function submitAndWatch() {
    phase.value = 'submitting'
    submitError.value = null
    startedAt.value = Date.now()
    try {
      await queueReport(identity.value)
    } catch (error) {
      submitError.value = error
      phase.value = 'submit-error'
      return
    }
    await watch()
  }

  onBeforeUnmount(stop)

  return {
    phase,
    latest,
    submitError,
    reconnectAttempt,
    startedAt,
    isActive,
    watch,
    submitAndWatch,
    stop,
  }
}
