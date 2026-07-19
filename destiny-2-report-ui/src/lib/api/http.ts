/**
 * The single fetch wrapper for the Destiny 2 Report API.
 *
 * Responsibilities:
 *  - reads VITE_API_BASE_URL, defaulting to same-origin `/api`;
 *  - sends/accepts JSON, including the nonstandard `QUERY` verb;
 *  - normalizes ASP.NET Core ProblemDetails and empty 404/429 responses;
 *  - preserves 64-bit identity fields that would lose precision in
 *    JSON.parse by rewriting them to strings before parsing;
 *  - accepts AbortSignal for route changes and search supersession.
 */

export interface ProblemDetails {
  type?: string
  title?: string
  status?: number
  detail?: string
  [extension: string]: unknown
}

export class ApiError extends Error {
  readonly status: number
  readonly problem: ProblemDetails | null
  readonly retryAfterSeconds: number | null

  constructor(status: number, problem: ProblemDetails | null, retryAfterSeconds: number | null) {
    super(problem?.detail ?? problem?.title ?? `Request failed with status ${status}`)
    this.name = 'ApiError'
    this.status = status
    this.problem = problem
    this.retryAfterSeconds = retryAfterSeconds
  }

  get isNotFound(): boolean {
    return this.status === 404
  }

  get isRateLimited(): boolean {
    return this.status === 429
  }
}

export function isApiError(error: unknown): error is ApiError {
  return error instanceof ApiError
}

export const API_BASE_URL: string = import.meta.env.VITE_API_BASE_URL ?? '/api'

/**
 * Destiny membership IDs (~4.6e18) and PGCR instance IDs exceed
 * Number.MAX_SAFE_INTEGER (~9e15). JSON.parse would silently round them,
 * corrupting identity. These specific keys are rewritten to JSON strings in
 * the raw response text before parsing; the transport types declare them as
 * `string`.
 *
 * Request bodies may send the values back as strings. ASP.NET Core's web
 * JSON defaults (`NumberHandling.AllowReadingFromString`) accept that.
 */
const BIG_INT_KEY_PATTERN =
  /"(membershipId|playerMembershipId|ownerMembershipId|instanceId)":\s*(-?\d+)/g

export function parseApiJson<T>(raw: string): T {
  const guarded = raw.replace(BIG_INT_KEY_PATTERN, '"$1":"$2"')
  return JSON.parse(guarded) as T
}

export interface ApiRequestOptions {
  method?: 'GET' | 'POST' | 'QUERY'
  body?: unknown
  signal?: AbortSignal
}

async function readProblem(response: Response): Promise<ProblemDetails | null> {
  try {
    const text = await response.text()
    if (!text) return null
    return JSON.parse(text) as ProblemDetails
  } catch {
    return null
  }
}

function readRetryAfter(response: Response): number | null {
  const header = response.headers.get('Retry-After')
  if (!header) return null
  const seconds = Number(header)
  return Number.isFinite(seconds) ? seconds : null
}

export async function apiFetch<T>(path: string, options: ApiRequestOptions = {}): Promise<T> {
  const headers: Record<string, string> = { Accept: 'application/json' }
  if (options.body !== undefined) headers['Content-Type'] = 'application/json'

  const response = await fetch(`${API_BASE_URL}${path}`, {
    method: options.method ?? 'GET',
    headers,
    body: options.body === undefined ? undefined : JSON.stringify(options.body),
    signal: options.signal,
    credentials: 'include',
  })

  if (!response.ok) {
    throw new ApiError(response.status, await readProblem(response), readRetryAfter(response))
  }

  const text = await response.text()
  if (!text) return undefined as T
  return parseApiJson<T>(text)
}
