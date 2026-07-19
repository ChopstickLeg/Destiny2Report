import { API_BASE_URL, ApiError, apiFetch, parseApiJson } from './http'
import { drainSseBuffer } from './sse'
import type { AdminMutationResponse, AdminOverview } from './types'

export function flushRedisQueue(): Promise<AdminMutationResponse> {
  return apiFetch('/admin/queues/redis/flush', { method: 'POST' })
}

export function flushMongoQueue(): Promise<AdminMutationResponse> {
  return apiFetch('/admin/queues/mongo/flush', { method: 'POST' })
}

export function setAllFullRecrawl(reason: string): Promise<AdminMutationResponse> {
  return apiFetch('/admin/reports/full-recrawl', {
    method: 'POST',
    body: { reason },
  })
}

export async function watchAdminOverview(
  signal: AbortSignal,
  onOverview: (overview: AdminOverview) => void,
): Promise<void> {
  const response = await fetch(`${API_BASE_URL}/admin/stream`, {
    headers: { Accept: 'text/event-stream' },
    credentials: 'include',
    signal,
  })

  if (!response.ok || !response.body) {
    throw new ApiError(response.status, null, null)
  }

  const reader = response.body.getReader()
  const decoder = new TextDecoder()
  let buffer = ''

  try {
    for (;;) {
      const { done, value } = await reader.read()
      if (done) throw new Error('Admin event stream closed.')
      buffer += decoder.decode(value, { stream: true })
      const drained = drainSseBuffer(buffer)
      buffer = drained.rest
      for (const event of drained.events) {
        if (event.event === 'overview' || event.event === 'message') {
          onOverview(parseApiJson<AdminOverview>(event.data))
        }
      }
    }
  } finally {
    reader.releaseLock()
  }
}
