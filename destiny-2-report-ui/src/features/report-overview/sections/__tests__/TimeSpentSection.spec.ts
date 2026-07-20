import { mount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'
import { veteranReport } from '@/test/fixtures/report'
import TimeSpentSection from '../TimeSpentSection.vue'

describe('TimeSpentSection', () => {
  it('promotes activity time and streaks with player-facing labels', () => {
    const wrapper = mount(TimeSpentSection, {
      props: { report: veteranReport },
    })

    const highlights = wrapper.findAll('.time-stat')

    expect(highlights).toHaveLength(3)
    expect(highlights[0]?.text()).toContain('Non-orbit time')
    expect(highlights[0]?.text()).toContain('Time spent playing activities')
    expect(highlights[1]?.text()).toContain('Longest play streak')
    expect(highlights[1]?.text()).toContain('14 days in a row')
    expect(highlights[2]?.text()).toContain('Current play streak')
    expect(highlights[2]?.text()).toContain('Still going')
    expect(wrapper.text()).not.toContain('inside recorded activities')
  })
})
