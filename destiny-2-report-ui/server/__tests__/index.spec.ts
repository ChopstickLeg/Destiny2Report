import { afterEach, describe, expect, it, vi } from 'vitest'

import { handleRequest } from '../index'

function createEnv(assetResponse = new Response('asset response')) {
  return {
    API_ORIGIN: 'https://api.destiny-2.report',
    ASSETS: {
      fetch: vi.fn<(request: Request) => Promise<Response>>(async () => assetResponse),
    },
  }
}

describe('frontend worker routing', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('proxies API requests without changing the method, path, query, or body', async () => {
    let upstreamRequest: Request | undefined
    const fetchMock = vi.fn<(request: Request) => Promise<Response>>(async (request) => {
      upstreamRequest = request
      return Response.json({ ok: true })
    })
    vi.stubGlobal('fetch', fetchMock)

    const request = new Request('https://destiny-2.report/api/players/search?source=frontend', {
      method: 'QUERY',
      headers: {
        'Content-Type': 'application/json',
        'CF-Connecting-IP': '203.0.113.42',
        'X-Forwarded-For': '198.51.100.99',
      },
      body: JSON.stringify({ displayNamePrefix: 'ChopstickLeg' }),
    })

    const response = await handleRequest(request, createEnv())

    expect(response.status).toBe(200)
    expect(fetchMock).toHaveBeenCalledOnce()
    expect(upstreamRequest).toBeDefined()
    expect(upstreamRequest!.url).toBe(
      'https://api.destiny-2.report/api/players/search?source=frontend',
    )
    expect(upstreamRequest!.method).toBe('QUERY')
    expect(upstreamRequest!.headers.get('Content-Type')).toBe('application/json')
    expect(upstreamRequest!.headers.get('X-Forwarded-For')).toBe('203.0.113.42')
    await expect(upstreamRequest!.text()).resolves.toBe(
      JSON.stringify({ displayNamePrefix: 'ChopstickLeg' }),
    )
  })

  it('does not forward an unverified client-supplied address', async () => {
    let upstreamRequest: Request | undefined
    vi.stubGlobal(
      'fetch',
      vi.fn<(request: Request) => Promise<Response>>(async (request) => {
        upstreamRequest = request
        return Response.json({ ok: true })
      }),
    )

    await handleRequest(
      new Request('https://destiny-2.report/api/auth/whoami', {
        headers: { 'X-Forwarded-For': '198.51.100.99' },
      }),
      createEnv(),
    )

    expect(upstreamRequest!.headers.has('X-Forwarded-For')).toBe(false)
  })

  it('serves non-API requests from the static asset binding', async () => {
    const fetchMock = vi.fn<() => void>()
    vi.stubGlobal('fetch', fetchMock)
    const env = createEnv()
    const request = new Request('https://destiny-2.report/faq')

    const response = await handleRequest(request, env)

    expect(await response.text()).toBe('asset response')
    expect(env.ASSETS.fetch).toHaveBeenCalledWith(request)
    expect(fetchMock).not.toHaveBeenCalled()
  })

  it('returns Problem Details when the API origin cannot be reached', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn<() => Promise<never>>(async () => {
        throw new Error('origin unavailable')
      }),
    )
    vi.spyOn(console, 'error').mockImplementation(() => undefined)

    const response = await handleRequest(
      new Request('https://destiny-2.report/api/leaderboards'),
      createEnv(),
    )

    expect(response.status).toBe(502)
    await expect(response.json()).resolves.toEqual({
      type: 'about:blank',
      title: 'API origin unavailable',
      status: 502,
    })
  })
})
