import { mount } from '@vue/test-utils'
import { nextTick } from 'vue'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import type { AdminOverview } from '@/lib/api/types'
import AdminView from '../AdminView.vue'

const { watchAdminOverview } = vi.hoisted(() => ({
  watchAdminOverview:
    vi.fn<(signal: AbortSignal, onOverview: (value: AdminOverview) => void) => Promise<void>>(),
}))

vi.mock('@/lib/api/admin', () => ({
  flushMongoQueue: vi.fn<() => Promise<void>>(),
  flushRedisQueue: vi.fn<() => Promise<void>>(),
  setAllFullRecrawl: vi.fn<() => Promise<void>>(),
  watchAdminOverview,
}))

describe('AdminView', () => {
  beforeEach(() => {
    watchAdminOverview.mockReset()
  })

  it('renders the display name for an active crawl', async () => {
    const overview: AdminOverview = {
      updatedAtUtc: '2026-07-24T19:06:43.9956736+00:00',
      activeCrawls: [
        {
          membershipTypeId: 3,
          membershipId: '4611686018518440725',
          displayName: 'Guardian',
          queuedAtUtc: '2026-07-24T18:43:35.295+00:00',
          startedAtUtc: '2026-07-24T18:50:21.018+00:00',
          leaseExpiresAtUtc: '2026-07-24T19:11:29.327+00:00',
          leaseOwner: 'worker-1',
          queuedInRedis: true,
          progress: {
            phase: 'activities',
            label: 'Reading activity history',
            current: 125,
            total: 500,
            startedAtUtc: '2026-07-24T18:50:21.018+00:00',
            updatedAtUtc: '2026-07-24T19:06:40.018+00:00',
          },
        },
      ],
      statusCounts: [],
    }

    watchAdminOverview.mockImplementation(
      (_signal: AbortSignal, onOverview: (value: AdminOverview) => void) => {
        onOverview(overview)
        return new Promise<void>(() => {})
      },
    )

    const wrapper = mount(AdminView, {
      global: {
        stubs: {
          RouterLink: { template: '<a><slot /></a>' },
        },
      },
    })
    await nextTick()

    expect(wrapper.find('.player-link').text()).toBe('Guardian')
    expect(wrapper.find('.crawl-progress-heading').text()).toContain('Reading activity history')
    expect(wrapper.find('.crawl-progress-heading').text()).toContain('125 of 500')
    expect(wrapper.find('[role="progressbar"]').attributes('aria-valuenow')).toBe('25')

    wrapper.unmount()
  })

  it('renders a legacy active crawl with a null display name', async () => {
    const overview: AdminOverview = {
      updatedAtUtc: '2026-07-19T19:06:43.9956736+00:00',
      activeCrawls: [
        {
          membershipTypeId: 5,
          membershipId: '4611686018518440725',
          displayName: null,
          queuedAtUtc: '2026-07-19T18:43:35.295+00:00',
          startedAtUtc: '2026-07-19T18:50:21.018+00:00',
          leaseExpiresAtUtc: '2026-07-19T19:11:29.327+00:00',
          leaseOwner: 'worker-1',
          queuedInRedis: false,
          progress: null,
        },
      ],
      statusCounts: [
        { status: 'queued', count: 33_800 },
        { status: 'running', count: 1 },
        { status: 'completed', count: 3 },
        { status: 'failed', count: 0 },
        { status: 'private', count: 0 },
      ],
    }

    watchAdminOverview.mockImplementation(
      (_signal: AbortSignal, onOverview: (value: AdminOverview) => void) => {
        onOverview(overview)
        return new Promise<void>(() => {})
      },
    )

    const wrapper = mount(AdminView, {
      global: {
        stubs: {
          RouterLink: { template: '<a><slot /></a>' },
        },
      },
    })
    await nextTick()

    expect(wrapper.text()).toContain('4611686018518440725')
    expect(wrapper.text()).toContain('33,800')
    expect(wrapper.text()).not.toContain('Waiting for the first worker snapshot')

    wrapper.unmount()
  })
})
