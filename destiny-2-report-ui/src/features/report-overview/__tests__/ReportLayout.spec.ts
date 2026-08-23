import { flushPromises, shallowMount } from '@vue/test-utils'
import { computed, ref } from 'vue'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { makeReport } from '@/test/fixtures/report'
import ReportLayout from '../ReportLayout.vue'
import {
  useInvalidateReport,
  usePlayerStandings,
  useReportIdentity,
  useReportQuery,
} from '../useReport'

const { submitAndWatch, watchQueue, fetchQueuePolicy, session } = vi.hoisted(() => ({
  submitAndWatch: vi.fn<() => Promise<void>>(),
  watchQueue: vi.fn<() => Promise<void>>(),
  fetchQueuePolicy: vi.fn<() => Promise<{ authenticationRequired: boolean }>>(),
  session: {
    status: 'signed-in',
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
    fetchQueuePolicy.mockReset()
    fetchQueuePolicy.mockResolvedValue({ authenticationRequired: false })
    session.status = 'signed-in'
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
    vi.mocked(usePlayerStandings).mockReturnValue({
      data: ref(null),
      isPending: ref(false),
      isError: ref(false),
    } as never)
  })

  afterEach(() => {
    vi.useRealTimers()
  })

  it('submits an existing report through the normal watcher and suppresses only cooldown errors', async () => {
    const wrapper = shallowMount(ReportLayout)

    await flushPromises()

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

  it('does not auto-refresh for a signed-out visitor when authentication is required', async () => {
    fetchQueuePolicy.mockResolvedValue({ authenticationRequired: true })
    session.isSignedIn = false

    const wrapper = shallowMount(ReportLayout)
    await flushPromises()

    expect(submitAndWatch).not.toHaveBeenCalled()
    expect(wrapper.findComponent({ name: 'ReportMasthead' }).props('signInRequired')).toBe(true)
    wrapper.unmount()
  })

  it('ignores refresh requests until queue policy discovery completes', async () => {
    let resolvePolicy!: (policy: { authenticationRequired: boolean }) => void
    fetchQueuePolicy.mockReturnValue(
      new Promise((resolve) => {
        resolvePolicy = resolve
      }),
    )

    const wrapper = shallowMount(ReportLayout)
    const masthead = wrapper.findComponent({ name: 'ReportMasthead' })
    expect(masthead.props('queueAccessPending')).toBe(true)
    masthead.vm.$emit('refresh')
    await flushPromises()
    expect(submitAndWatch).not.toHaveBeenCalled()

    resolvePolicy({ authenticationRequired: false })
    await flushPromises()
    expect(submitAndWatch).toHaveBeenCalledExactlyOnceWith({ suppressCooldownError: true })
    wrapper.unmount()
  })

  it('offers a manual retry when queue-policy discovery remains unavailable', async () => {
    vi.useFakeTimers()
    fetchQueuePolicy.mockRejectedValue(new Error('network unavailable'))

    const wrapper = shallowMount(ReportLayout)
    await vi.runAllTimersAsync()
    await flushPromises()

    expect(fetchQueuePolicy).toHaveBeenCalledTimes(3)
    expect(wrapper.text()).toContain("Queue access couldn't be verified")
    const masthead = wrapper.findComponent({ name: 'ReportMasthead' })
    expect(masthead.props('queueAccessPending')).toBe(true)
    expect(masthead.props('queueAccessError')).toBe(true)
    expect(submitAndWatch).not.toHaveBeenCalled()

    fetchQueuePolicy.mockResolvedValue({ authenticationRequired: false })
    await wrapper.get('app-button-stub').trigger('click')
    await flushPromises()

    expect(fetchQueuePolicy).toHaveBeenCalledTimes(4)
    expect(submitAndWatch).toHaveBeenCalledExactlyOnceWith({ suppressCooldownError: true })
    wrapper.unmount()
  })
})
