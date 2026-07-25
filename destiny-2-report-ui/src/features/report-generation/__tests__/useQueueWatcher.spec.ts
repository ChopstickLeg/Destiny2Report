import { mount } from '@vue/test-utils'
import { computed, defineComponent, h } from 'vue'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { ApiError } from '@/lib/api/http'
import { useQueueWatcher } from '../useQueueWatcher'

const { queueReport, watchQueue } = vi.hoisted(() => ({
  queueReport: vi.fn<() => Promise<unknown>>(),
  watchQueue: vi.fn<() => Promise<void>>(),
}))

vi.mock('@/lib/api/reports', () => ({
  queueReport,
}))

vi.mock('@/lib/api/sse', () => ({
  watchQueue,
}))

function mountWatcher() {
  let watcher!: ReturnType<typeof useQueueWatcher>
  const wrapper = mount(
    defineComponent({
      setup() {
        watcher = useQueueWatcher(
          computed(() => ({
            membershipTypeId: 3,
            membershipId: '4611686018487421905',
          })),
          { onCompleted: vi.fn<() => void>() },
        )
        return () => h('div')
      },
    }),
  )

  return { watcher, wrapper }
}

describe('useQueueWatcher submission errors', () => {
  beforeEach(() => {
    queueReport.mockReset()
    watchQueue.mockReset()
  })

  it('quietly ignores a crawl cooldown for an automatic refresh', async () => {
    queueReport.mockRejectedValue(
      new ApiError(429, { code: 'crawl_cooldown', detail: 'Try again later.' }, 3600),
    )
    const { watcher, wrapper } = mountWatcher()

    await watcher.submitAndWatch({ suppressCooldownError: true })

    expect(watcher.phase.value).toBe('idle')
    expect(watcher.submitError.value).toBeNull()
    expect(watchQueue).not.toHaveBeenCalled()
    wrapper.unmount()
  })

  it('falls back to watching a queued profile when its automatic re-submit hits cooldown', async () => {
    queueReport.mockRejectedValue(
      new ApiError(429, { code: 'crawl_cooldown', detail: 'Try again later.' }, 3600),
    )
    watchQueue.mockResolvedValue()
    const { watcher, wrapper } = mountWatcher()

    await watcher.submitAndWatch({
      suppressCooldownError: true,
      watchOnSuppressedCooldown: true,
    })

    expect(watcher.submitError.value).toBeNull()
    expect(watchQueue).toHaveBeenCalledOnce()
    expect(watcher.phase.value).not.toBe('submit-error')
    wrapper.unmount()
  })

  it('keeps the cooldown visible for a manual refresh', async () => {
    const error = new ApiError(429, { code: 'crawl_cooldown', detail: 'Try again later.' }, 3600)
    queueReport.mockRejectedValue(error)
    const { watcher, wrapper } = mountWatcher()

    await watcher.submitAndWatch()

    expect(watcher.phase.value).toBe('submit-error')
    expect(watcher.submitError.value).toBe(error)
    wrapper.unmount()
  })
})
