import { shallowMount } from '@vue/test-utils'
import { computed, ref } from 'vue'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { makeReport } from '@/test/fixtures/report'
import ReportLayout from '../ReportLayout.vue'
import { useInvalidateReport, useReportIdentity, useReportQuery } from '../useReport'

const { submitAndWatch, watchQueue } = vi.hoisted(() => ({
  submitAndWatch: vi.fn<() => Promise<void>>(),
  watchQueue: vi.fn<() => Promise<void>>(),
}))

vi.mock('vue-router', () => ({
  RouterView: { template: '<div />' },
  useRoute: () => ({ query: {} }),
}))

vi.mock('@/features/report-generation/useQueueWatcher', () => ({
  useQueueWatcher: () => ({
    phase: ref('idle'),
    latest: ref(null),
    submitError: ref(null),
    reconnectAttempt: ref(0),
    startedAt: ref(null),
    isActive: ref(false),
    submitAndWatch,
    watch: watchQueue,
  }),
}))

vi.mock('../useReport', () => ({
  useReportIdentity: vi.fn<() => unknown>(),
  useReportQuery: vi.fn<() => unknown>(),
  useInvalidateReport: vi.fn<() => unknown>(),
}))

describe('ReportLayout automatic refresh', () => {
  beforeEach(() => {
    submitAndWatch.mockReset()
    watchQueue.mockReset()

    const identity = computed(() => ({
      membershipTypeId: 3,
      membershipId: '4611686018467284386',
    }))
    vi.mocked(useReportIdentity).mockReturnValue(identity)
    vi.mocked(useInvalidateReport).mockReturnValue(vi.fn<() => Promise<void>>())
    vi.mocked(useReportQuery).mockReturnValue({
      data: ref(makeReport()),
      isPending: ref(false),
      isError: ref(false),
      error: ref(null),
      refetch: vi.fn<() => Promise<unknown>>(),
    } as never)
  })

  it('submits an existing report through the normal watcher and suppresses only cooldown errors', () => {
    const wrapper = shallowMount(ReportLayout)

    expect(submitAndWatch).toHaveBeenCalledExactlyOnceWith({ suppressCooldownError: true })
    expect(watchQueue).not.toHaveBeenCalled()
    wrapper.unmount()
  })
})
