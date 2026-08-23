import { shallowMount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'
import LeaderboardStandingBadge from '../LeaderboardStandingBadge.vue'

describe('LeaderboardStandingBadge', () => {
  it('links directly to its leaderboard metric', () => {
    const wrapper = shallowMount(LeaderboardStandingBadge, {
      props: {
        standing: {
          metricKey: 'combat.kills.mode.81',
          category: 'Combat',
          title: 'Relic kills',
          unit: 'count',
          score: 79,
          tier: 'top-1000',
          rank: 68,
        },
      },
    })

    expect(wrapper.getComponent({ name: 'RouterLink' }).props('to')).toEqual({
      name: 'leaderboards',
      query: { board: 'combat.kills.mode.81' },
    })
  })
})
