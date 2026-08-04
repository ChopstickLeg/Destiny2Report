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

interface ProblemDetails {
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

export function formatRetryAfter(seconds: number): string {
  const roundedSeconds = Math.max(1, Math.ceil(seconds))
  if (roundedSeconds < 60) {
    return `${roundedSeconds} ${roundedSeconds === 1 ? 'second' : 'seconds'}`
  }

  const totalMinutes = Math.ceil(roundedSeconds / 60)
  if (totalMinutes < 60) {
    return `${totalMinutes} ${totalMinutes === 1 ? 'minute' : 'minutes'}`
  }

  const hours = Math.floor(totalMinutes / 60)
  const minutes = totalMinutes % 60
  const hourText = `${hours} ${hours === 1 ? 'hour' : 'hours'}`
  return minutes === 0 ? hourText : `${hourText} ${minutes} minutes`
}

export function getErrorMessage(error: unknown, fallback: string): string {
  if (isApiError(error)) {
    if (error.isRateLimited) {
      if (error.problem?.code === 'crawl_cooldown') return error.message
      return error.retryAfterSeconds !== null
        ? `Too many requests right now. Try again in ${formatRetryAfter(error.retryAfterSeconds)}.`
        : 'Too many requests right now. Give it a minute and try again.'
    }
    if (error.status >= 500) return 'The service hit a problem answering this request.'
    return error.message
  }
  if (error instanceof TypeError) {
    return 'The service could not be reached. Check your connection and try again.'
  }
  return fallback
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

interface ApiRequestOptions {
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
  if (Number.isFinite(seconds)) return Math.max(0, seconds)

  const retryAt = Date.parse(header)
  return Number.isNaN(retryAt) ? null : Math.max(0, Math.ceil((retryAt - Date.now()) / 1_000))
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
    const problem = await readProblem(response)
    const bodyRetryAfter =
      typeof problem?.retryAfterSeconds === 'number' ? problem.retryAfterSeconds : null
    throw new ApiError(response.status, problem, readRetryAfter(response) ?? bodyRetryAfter)
  }

  const text = await response.text()
  if (!text) return undefined as T
  return parseApiJson<T>(text)
}
