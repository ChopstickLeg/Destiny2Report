/**
 * A small fetch-based Server-Sent Events client for the report queue
 * endpoint. A fetch reader (rather than native EventSource) gives us
 * cancellation via AbortSignal, access to error status codes, bounded
 * reconnection, and testability.
 *
 * The API emits events named "position", "queued", "running", "completed",
 * "failed", "private", and "not_found", each carrying a JSON
 * ReportQueueStatusResponse payload. The stream ends after a terminal
 * status (completed / failed / private) or not_found.
 */

import { API_BASE_URL, ApiError, parseApiJson } from './http'
import type { ReportIdentity } from './reports'
import type { QueueStatus, ReportQueueStatusResponse } from './types'

interface SseEvent {
  event: string
  data: string
}

/**
 * Parse the accumulated text of an SSE stream chunk-by-chunk.
 * Returns complete events and the unconsumed remainder of the buffer.
 */
export function drainSseBuffer(buffer: string): { events: SseEvent[]; rest: string } {
  const events: SseEvent[] = []
  // Events are separated by a blank line. Keep any trailing partial event.
  const normalized = buffer.replace(/\r\n/g, '\n')
  const blocks = normalized.split('\n\n')
  const rest = blocks.pop() ?? ''

  for (const block of blocks) {
    let event = 'message'
    const dataLines: string[] = []
    for (const line of block.split('\n')) {
      if (line.startsWith('event:')) {
        event = line.slice(6).trim()
      } else if (line.startsWith('data:')) {
        dataLines.push(line.slice(5).trimStart())
      }
      // id: and retry: fields are not needed by this endpoint.
    }
    if (dataLines.length > 0) {
      events.push({ event, data: dataLines.join('\n') })
    }
  }

  return { events, rest }
}

const TERMINAL_STATUSES: ReadonlySet<QueueStatus> = new Set(['completed', 'failed', 'private'])

export function isTerminalStatus(status: QueueStatus): boolean {
  return TERMINAL_STATUSES.has(status)
}

interface QueueWatchHandlers {
  onStatus: (status: ReportQueueStatusResponse) => void
  /** Fired when the queue endpoint returns 404 or emits not_found. */
  onNotFound: () => void
  /** Fired on transient stream trouble; informational only, a retry follows. */
  onReconnecting?: (attempt: number) => void
}

const MAX_RECONNECT_ATTEMPTS = 5
const BACKOFF_BASE_MS = 1_000
const BACKOFF_CAP_MS = 15_000

function delay(ms: number, signal: AbortSignal): Promise<void> {
  return new Promise((resolve, reject) => {
    const timer = setTimeout(resolve, ms)
    signal.addEventListener(
      'abort',
      () => {
        clearTimeout(timer)
        reject(new DOMException('Aborted', 'AbortError'))
      },
      { once: true },
    )
  })
}

/**
 * Watch a queue until it reaches a terminal state, the report disappears,
 * or the signal aborts. Reconnects with bounded exponential backoff after
 * transient disconnects; rethrows once attempts are exhausted so callers
 * can fall back to a plain report lookup before declaring failure.
 */
export async function watchQueue(
  identity: ReportIdentity,
  signal: AbortSignal,
  handlers: QueueWatchHandlers,
): Promise<void> {
  let attempt = 0

  while (!signal.aborted) {
    try {
      const finished = await streamOnce(identity, signal, handlers)
      if (finished) return
      // Stream ended without a terminal event: treat as transient.
    } catch (error) {
      if (signal.aborted || (error instanceof DOMException && error.name === 'AbortError')) return
      if (error instanceof ApiError && error.isNotFound) {
        handlers.onNotFound()
        return
      }
      if (attempt >= MAX_RECONNECT_ATTEMPTS) throw error
    }

    attempt += 1
    if (attempt > MAX_RECONNECT_ATTEMPTS) {
      throw new Error('Lost connection to crawl progress.')
    }
    handlers.onReconnecting?.(attempt)
    await delay(Math.min(BACKOFF_BASE_MS * 2 ** (attempt - 1), BACKOFF_CAP_MS), signal)
  }
}

/** Returns true when a terminal/not_found event closed the stream. */
async function streamOnce(
  identity: ReportIdentity,
  signal: AbortSignal,
  handlers: QueueWatchHandlers,
): Promise<boolean> {
  const response = await fetch(
    `${API_BASE_URL}/reports/${identity.membershipTypeId}/${identity.membershipId}/queue`,
    { headers: { Accept: 'text/event-stream' }, credentials: 'include', signal },
  )

  if (!response.ok || !response.body) {
    throw new ApiError(response.status, null, null)
  }

  const reader = response.body.getReader()
  const decoder = new TextDecoder()
  let buffer = ''

  try {
    for (;;) {
      const { done, value } = await reader.read()
      if (done) return false

      buffer += decoder.decode(value, { stream: true })
      const { events, rest } = drainSseBuffer(buffer)
      buffer = rest

      for (const { data } of events) {
        const status = parseApiJson<ReportQueueStatusResponse>(data)
        if (status.status === 'not_found') {
          handlers.onNotFound()
          return true
        }
        handlers.onStatus(status)
        if (isTerminalStatus(status.status)) return true
      }
    }
  } finally {
    reader.releaseLock()
  }
}
