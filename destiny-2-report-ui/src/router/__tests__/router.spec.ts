import { describe, expect, it } from 'vitest'
import router from '../index'

describe('story routes', () => {
  it('builds a public, membership-specific story link', () => {
    const token = '5cAFpklO0tjpaO6yfK8yP9xGShNnWRWb8g_HrQDBWjs'
    const route = router.resolve({
      name: 'shared-story',
      params: { shareToken: token },
    })

    expect(route.href).toBe(`/story/${token}`)
    expect(route.href).not.toContain('4611686018467000000')
  })
})
