import { shallowMount } from '@vue/test-utils'
import { computed, ref } from 'vue'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { makeReport } from '@/test/fixtures/report'
import ReportLayout from '../ReportLayout.vue'
import {
  useInvalidateReport,
  usePlayerStandings,
  useReportIdentity,
  useReportQuery,
} from '../useReport'

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
  playerStandingsKey: Symbol('playerStandings'),
  useReportIdentity: vi.fn<() => unknown>(),
  useReportQuery: vi.fn<() => unknown>(),
  usePlayerStandings: vi.fn<() => unknown>(),
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
    vi.mocked(usePlayerStandings).mockReturnValue({
      data: ref(null),
      isPending: ref(false),
      isError: ref(false),
    } as never)
  })

  it('submits an existing report through the normal watcher and suppresses only cooldown errors', () => {
    const wrapper = shallowMount(ReportLayout)

    expect(submitAndWatch).toHaveBeenCalledExactlyOnceWith({ suppressCooldownError: true })
    expect(watchQueue).not.toHaveBeenCalled()
    wrapper.unmount()
  })

  it('holds the report body in a stable skeleton while rankings load', () => {
    vi.mocked(usePlayerStandings).mockReturnValue({
      data: ref(null),
      isPending: ref(true),
      isError: ref(false),
    } as never)

    const wrapper = shallowMount(ReportLayout)

    expect(wrapper.find('.rankings-loading').exists()).toBe(true)
    expect(wrapper.findComponent({ name: 'GlobalStandings' }).exists()).toBe(false)
    wrapper.unmount()
  })
})
