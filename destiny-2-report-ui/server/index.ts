interface Env {
  API_ORIGIN: string
  ASSETS: {
    fetch(request: Request): Promise<Response>
  }
}

function isApiRequest(pathname: string): boolean {
  return pathname === '/api' || pathname.startsWith('/api/')
}

export async function handleRequest(request: Request, env: Env): Promise<Response> {
  const incomingUrl = new URL(request.url)

  if (!isApiRequest(incomingUrl.pathname)) {
    return env.ASSETS.fetch(request)
  }

  const apiOrigin = new URL(env.API_ORIGIN)
  const upstreamUrl = new URL(`${incomingUrl.pathname}${incomingUrl.search}`, apiOrigin)
  const body =
    request.method === 'GET' || request.method === 'HEAD' || request.body === null
      ? undefined
      : await request.arrayBuffer()
  const headers = new Headers(request.headers)
  const clientIp = request.headers.get('CF-Connecting-IP')

  // Normalize the standard forwarded address from Cloudflare's visitor header
  // instead of passing through a value that a browser supplied. The API also
  // reads CF-Connecting-IP directly from its trusted tunnel proxy.
  if (clientIp) {
    headers.set('X-Forwarded-For', clientIp)
  } else {
    headers.delete('X-Forwarded-For')
  }

  try {
    const upstreamRequest = new Request(upstreamUrl, {
      method: request.method,
      headers,
      body,
      redirect: request.redirect,
    })

    return await fetch(upstreamRequest)
  } catch (error) {
    console.error('API origin request failed', error)

    return Response.json(
      {
        type: 'about:blank',
        title: 'API origin unavailable',
        status: 502,
      },
      { status: 502 },
    )
  }
}

export default {
  fetch: handleRequest,
}
