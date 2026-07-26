import { flushPromises, mount } from '@vue/test-utils'
import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query'
import { createMemoryHistory, createRouter } from 'vue-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import LeaderboardsView from '../LeaderboardsView.vue'

const { fetchLeaderboard, fetchLeaderboardCatalog } = vi.hoisted(() => ({
  fetchLeaderboard: vi.fn<(key: string, offset: number) => Promise<unknown>>(),
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

  it('loads the next 250 Guardians after scrolling through 150 entries', async () => {
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
    fetchLeaderboard.mockImplementation(async (key, offset) => ({
      key,
      category: 'Time',
      title: 'Raid playtime',
      description: '',
      unit: 'seconds',
      offset,
      limit: 250,
      retainedEntryCount: 1000,
      updatedAtUtc: '2026-01-01T00:00:00Z',
      isRepairing: false,
      entries: Array.from({ length: 250 }, (_, index) => {
        const rank = offset + index + 1
        return {
          rank,
          membershipTypeId: 3,
          membershipId: String(rank),
          displayName: `Guardian ${rank}`,
          displayCode: rank,
          fullDisplayName: `Guardian ${rank}#${rank}`,
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

    const list = wrapper.get('.ranking-list')
    const trigger = wrapper.get('[data-entry-index="150"]')
    Object.defineProperty(list.element, 'clientHeight', { value: 600 })
    Object.defineProperty(list.element, 'scrollTop', { value: 9_600, writable: true })
    Object.defineProperty(trigger.element, 'offsetTop', { value: 10_200 })

    await list.trigger('scroll')
    await flushPromises()

    expect(fetchLeaderboard).toHaveBeenNthCalledWith(1, 'time.mode.4', 0, expect.any(AbortSignal))
    expect(fetchLeaderboard).toHaveBeenNthCalledWith(2, 'time.mode.4', 250, expect.any(AbortSignal))
    expect(wrapper.findAll('.ranking-row')).toHaveLength(500)
    expect(wrapper.get('[data-entry-index="499"]').text()).toContain('Guardian 500')
    wrapper.unmount()
  })
})
