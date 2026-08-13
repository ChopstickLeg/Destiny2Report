import { flushPromises, shallowMount } from '@vue/test-utils'
import { computed, ref } from 'vue'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { makeReport } from '@/test/fixtures/report'
import ReportLayout from '../ReportLayout.vue'
import { useInvalidateReport, useReportIdentity, useReportQuery } from '../useReport'

const { submitAndWatch, watchQueue, fetchQueuePolicy, session } = vi.hoisted(() => ({
  submitAndWatch: vi.fn<() => Promise<void>>(),
  watchQueue: vi.fn<() => Promise<void>>(),
  fetchQueuePolicy: vi.fn<() => Promise<{ authenticationRequired: boolean }>>(),
  session: {
    isSignedIn: true,
    beginSignIn: vi.fn<(returnTo: string) => void>(),
  },
}))

vi.mock('vue-router', () => ({
  RouterView: { template: '<div />' },
  useRoute: () => ({ query: {}, fullPath: '/report/3/4611686018467284386' }),
}))

vi.mock('@/lib/api/reports', () => ({ fetchQueuePolicy }))

vi.mock('@/stores/session', () => ({
  useSessionStore: () => session,
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
    fetchQueuePolicy.mockReset()
    fetchQueuePolicy.mockResolvedValue({ authenticationRequired: false })
    session.isSignedIn = true
    session.beginSignIn.mockReset()

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

  it('submits an existing report through the normal watcher and suppresses only cooldown errors', async () => {
    const wrapper = shallowMount(ReportLayout)

    await flushPromises()

    expect(submitAndWatch).toHaveBeenCalledExactlyOnceWith({ suppressCooldownError: true })
    expect(watchQueue).not.toHaveBeenCalled()
    wrapper.unmount()
  })

  it('does not auto-refresh for a signed-out visitor when authentication is required', async () => {
    fetchQueuePolicy.mockResolvedValue({ authenticationRequired: true })
    session.isSignedIn = false

    const wrapper = shallowMount(ReportLayout)
    await flushPromises()

    expect(submitAndWatch).not.toHaveBeenCalled()
    expect(wrapper.findComponent({ name: 'ReportMasthead' }).props('signInRequired')).toBe(true)
    wrapper.unmount()
  })
})
