import { mount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'
import { ApiError } from '@/lib/api/http'
import ErrorState from '../ErrorState.vue'

describe('ErrorState', () => {
  it('shows the profile crawl cooldown message returned by the API', () => {
    const error = new ApiError(
      429,
      {
        title: 'Report crawl cooldown',
        detail: 'This profile was crawled recently. Try again in 5h 30m.',
        code: 'crawl_cooldown',
      },
      19_800,
    )

    const wrapper = mount(ErrorState, { props: { error } })

    expect(wrapper.text()).toContain('This profile was crawled recently. Try again in 5h 30m.')
    expect(wrapper.text()).not.toContain('Too many requests')
  })
})
