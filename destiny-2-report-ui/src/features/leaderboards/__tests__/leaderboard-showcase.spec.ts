import { flushPromises, mount } from '@vue/test-utils'
import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query'
import { describe, expect, it, vi } from 'vitest'
import LeaderboardShowcase from '../LeaderboardShowcase.vue'

const { fetchLeaderboardCatalog } = vi.hoisted(() => ({
  fetchLeaderboardCatalog: vi.fn<() => Promise<unknown>>(),
}))

vi.mock('@/lib/api/leaderboards', () => ({
  leaderboardKeys: { catalog: ['leaderboards'] },
  fetchLeaderboardCatalog,
}))

describe('LeaderboardShowcase', () => {
  it('shows gathering progress instead of leaderboard promotion until ready', async () => {
    fetchLeaderboardCatalog.mockResolvedValue({
      isReady: false,
      completedPlayerCount: 250,
      minimumCompletedPlayers: 1000,
      leaderboards: [],
    })

    const wrapper = mount(LeaderboardShowcase, {
      global: {
        plugins: [
          [
            VueQueryPlugin,
            { queryClient: new QueryClient({ defaultOptions: { queries: { retry: false } } }) },
          ],
        ],
      },
    })
    await flushPromises()

    expect(wrapper.find('.showcase').exists()).toBe(false)
    expect(wrapper.text()).toContain('Rankings are gathering')
    expect(wrapper.text()).toContain('250 / 1,000 Guardians ready')
    expect(wrapper.text()).not.toContain('See who leads the pack')
    wrapper.unmount()
  })
})
