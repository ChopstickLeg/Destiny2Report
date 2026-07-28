import { flushPromises, mount } from '@vue/test-utils'
import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query'
import { createMemoryHistory, createRouter } from 'vue-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import LeaderboardsView from '../LeaderboardsView.vue'

const { fetchLeaderboard, fetchLeaderboardCatalog } = vi.hoisted(() => ({
  fetchLeaderboard: vi.fn<(key: string) => Promise<unknown>>(),
  fetchLeaderboardCatalog: vi.fn<() => Promise<unknown>>(),
}))

vi.mock('@/lib/api/leaderboards', () => ({
  leaderboardKeys: {
    catalog: ['leaderboards'],
    board: (key: string) => ['leaderboards', key],
  },
  fetchLeaderboardCatalog,
  fetchLeaderboard,
}))

describe('LeaderboardsView', () => {
  beforeEach(() => {
    vi.clearAllMocks()
  })

  it('shows completed-player progress while rankings are locked', async () => {
    fetchLeaderboardCatalog.mockResolvedValue({
      isReady: false,
      completedPlayerCount: 250,
      minimumCompletedPlayers: 1000,
      leaderboards: [],
    })
    const router = createRouter({
      history: createMemoryHistory(),
      routes: [{ path: '/leaderboards', component: LeaderboardsView }],
    })
    await router.push('/leaderboards')
    await router.isReady()

    const wrapper = mount(LeaderboardsView, {
      global: {
        plugins: [
          [
            VueQueryPlugin,
            { queryClient: new QueryClient({ defaultOptions: { queries: { retry: false } } }) },
          ],
          router,
        ],
      },
    })
    await flushPromises()

    expect(wrapper.text()).toContain('Rankings are gathering')
    expect(wrapper.text()).not.toContain('Leaderboards are warming up')
    expect(wrapper.find('.leaderboards-hero').exists()).toBe(false)
    expect(wrapper.text()).toContain('250 / 1,000 Guardians ready')
    expect(wrapper.find('[role="progressbar"]').attributes('aria-valuenow')).toBe('250')
    wrapper.unmount()
  })

  it('keeps the leaderboard selected from search after search is cleared', async () => {
    fetchLeaderboardCatalog.mockResolvedValue({
      isReady: true,
      completedPlayerCount: 1000,
      minimumCompletedPlayers: 1000,
      leaderboards: [
        {
          key: 'time.mode.4',
          category: 'Time',
          title: 'Raid playtime',
          description: 'Time spent in raids.',
          unit: 'seconds',
          displayOrder: 1,
          rankedPlayerCount: 10,
          isRepairing: false,
        },
        {
          key: 'time.mode.10',
          category: 'Time',
          title: 'Control playtime',
          description: 'Time spent in Control.',
          unit: 'seconds',
          displayOrder: 1,
          rankedPlayerCount: 10,
          isRepairing: false,
        },
      ],
    })
    fetchLeaderboard.mockImplementation(async (key) => ({
      key,
      category: 'Time',
      title: key === 'time.mode.10' ? 'Control playtime' : 'Raid playtime',
      description: '',
      unit: 'seconds',
      offset: 0,
      limit: 250,
      retainedEntryCount: 0,
      updatedAtUtc: '2026-01-01T00:00:00Z',
      isRepairing: false,
      entries: [],
    }))

    const router = createRouter({
      history: createMemoryHistory(),
      routes: [{ path: '/leaderboards', component: LeaderboardsView }],
    })
    await router.push('/leaderboards?board=time.mode.4')
    await router.isReady()

    const wrapper = mount(LeaderboardsView, {
      global: {
        plugins: [
          [
            VueQueryPlugin,
            { queryClient: new QueryClient({ defaultOptions: { queries: { retry: false } } }) },
          ],
          router,
        ],
      },
    })
    await flushPromises()

    await wrapper.get('input[type="search"]').setValue('Control')
    await flushPromises()
    expect(router.currentRoute.value.query.board).toBe('time.mode.10')

    await wrapper.get('input[type="search"]').setValue('')
    await flushPromises()
    expect(router.currentRoute.value.query.board).toBe('time.mode.10')
    expect(wrapper.get('.record-select').text()).toContain('Control')
    wrapper.unmount()
  })

  it('loads all retained Guardians and filters them by display name', async () => {
    fetchLeaderboardCatalog.mockResolvedValue({
      isReady: true,
      completedPlayerCount: 1000,
      minimumCompletedPlayers: 1000,
      leaderboards: [
        {
          key: 'time.mode.4',
          category: 'Time',
          title: 'Raid playtime',
          description: 'Time spent in raids.',
          unit: 'seconds',
          displayOrder: 1,
          rankedPlayerCount: 1000,
          isRepairing: false,
        },
      ],
    })
    fetchLeaderboard.mockImplementation(async (key) => ({
      key,
      category: 'Time',
      title: 'Raid playtime',
      description: '',
      unit: 'seconds',
      offset: 0,
      limit: 1000,
      retainedEntryCount: 1000,
      updatedAtUtc: '2026-01-01T00:00:00Z',
      isRepairing: false,
      entries: Array.from({ length: 1000 }, (_, index) => {
        const rank = index + 1
        return {
          rank,
          membershipTypeId: 3,
          membershipId: String(rank),
          displayName: rank === 777 ? 'Needle Guardian' : `Guardian ${rank}`,
          displayCode: rank,
          fullDisplayName: rank === 777 ? `Needle Guardian#${rank}` : `Guardian ${rank}#${rank}`,
          emblemBackgroundUrl: '',
          score: 10_000 - rank,
        }
      }),
    }))

    const router = createRouter({
      history: createMemoryHistory(),
      routes: [{ path: '/leaderboards', component: LeaderboardsView }],
    })
    router.addRoute({
      path: '/report/:membershipTypeId/:membershipId',
      name: 'report-overview',
      component: { template: '<div />' },
    })
    await router.push('/leaderboards?board=time.mode.4')
    await router.isReady()

    const wrapper = mount(LeaderboardsView, {
      global: {
        plugins: [
          [
            VueQueryPlugin,
            { queryClient: new QueryClient({ defaultOptions: { queries: { retry: false } } }) },
          ],
          router,
        ],
      },
    })
    await flushPromises()

    expect(fetchLeaderboard).toHaveBeenCalledWith('time.mode.4', expect.any(AbortSignal))
    expect(wrapper.findAll('.ranking-row')).toHaveLength(1000)

    await wrapper.get('input[aria-label="Search players by display name"]').setValue('needle')
    await flushPromises()

    expect(fetchLeaderboard).toHaveBeenCalledTimes(1)
    expect(wrapper.findAll('.ranking-row')).toHaveLength(1)
    expect(wrapper.get('.ranking-row').text()).toContain('Needle Guardian#777')
    expect(wrapper.get('.ranking-row').text()).toContain('#777')
    wrapper.unmount()
  })
})
