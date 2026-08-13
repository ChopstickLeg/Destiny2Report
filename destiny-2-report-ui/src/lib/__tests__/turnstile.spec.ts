import { describe, expect, it } from 'vitest'
import { registerTurnstileProvider, withTurnstileToken } from '../turnstile'

describe('Turnstile token requests', () => {
  it('uses the registered challenge provider', async () => {
    const unregister = registerTurnstileProvider(async () => 'verified-token')

    await expect(withTurnstileToken(async (token) => token)).resolves.toBe('verified-token')

    unregister()
  })

  it('serializes challenges so concurrent queue requests receive separate tokens', async () => {
    const resolvers: Array<(token: string) => void> = []
    let calls = 0
    const unregister = registerTurnstileProvider(
      () =>
        new Promise<string>((resolve) => {
          calls++
          resolvers.push(resolve)
        }),
    )

    const first = withTurnstileToken(async (token) => token)
    const second = withTurnstileToken(async (token) => token)
    await Promise.resolve()
    expect(calls).toBe(1)

    resolvers.shift()!('first-token')
    await expect(first).resolves.toBe('first-token')
    await Promise.resolve()
    expect(calls).toBe(2)

    resolvers.shift()!('second-token')
    await expect(second).resolves.toBe('second-token')

    unregister()
  })
})
