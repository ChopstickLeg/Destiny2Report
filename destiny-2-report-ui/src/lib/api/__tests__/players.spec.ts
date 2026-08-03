import { afterEach, describe, expect, it, vi } from 'vitest'
import { searchPlayers } from '../players'

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('searchPlayers', () => {
  it('sends the Bungie display code to the API', async () => {
    const fetchMock = vi.fn<typeof fetch>().mockResolvedValue(
      new Response('[]', { status: 200, headers: { 'Content-Type': 'application/json' } }),
    )
    vi.stubGlobal('fetch', fetchMock)

    await searchPlayers('Cheeto', 5476)

    expect(fetchMock).toHaveBeenCalledOnce()
    const [, init] = fetchMock.mock.calls[0] as [string, RequestInit]
    expect(init.method).toBe('QUERY')
    expect(JSON.parse(init.body as string)).toEqual({
      displayNamePrefix: 'Cheeto',
      displayCode: 5476,
    })
  })
})
