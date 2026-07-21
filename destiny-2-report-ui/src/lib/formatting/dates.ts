/**
 * Date and relative-time formatting. API timestamps are ISO 8601 strings.
 */

const DATE_FORMAT = new Intl.DateTimeFormat('en-US', {
  year: 'numeric',
  month: 'short',
  day: 'numeric',
})

const DATE_TIME_FORMAT = new Intl.DateTimeFormat('en-US', {
  year: 'numeric',
  month: 'short',
  day: 'numeric',
  hour: 'numeric',
  minute: '2-digit',
})

const RELATIVE_FORMAT = new Intl.RelativeTimeFormat('en-US', { numeric: 'auto' })

export function parseApiDate(value: string | null | undefined): Date | null {
  if (!value) return null
  // Bare DateTime values from the API are UTC but may omit the "Z".
  const normalized = /(Z|[+-]\d{2}:\d{2})$/.test(value) ? value : `${value}Z`
  const date = new Date(normalized)
  return Number.isNaN(date.getTime()) ? null : date
}

export function formatDate(date: Date): string {
  return DATE_FORMAT.format(date)
}

export function formatDateTime(date: Date): string {
  return DATE_TIME_FORMAT.format(date)
}

/** Number of complete UTC calendar years elapsed since a date. */
export function completeYearsSince(date: Date, now: Date = new Date()): number {
  let years = now.getUTCFullYear() - date.getUTCFullYear()
  const anniversary = new Date(
    Date.UTC(now.getUTCFullYear(), date.getUTCMonth(), date.getUTCDate()),
  )
  if (now < anniversary) years--
  return Math.max(0, years)
}

const RELATIVE_STEPS: Array<[Intl.RelativeTimeFormatUnit, number]> = [
  ['year', 365 * 86_400],
  ['month', 30 * 86_400],
  ['week', 7 * 86_400],
  ['day', 86_400],
  ['hour', 3_600],
  ['minute', 60],
]

/**
 * "3 days ago", "last month", "just now". `now` is injectable for tests.
 */
export function formatRelative(date: Date, now: Date = new Date()): string {
  const deltaSeconds = (date.getTime() - now.getTime()) / 1000
  const magnitude = Math.abs(deltaSeconds)

  for (const [unit, seconds] of RELATIVE_STEPS) {
    if (magnitude >= seconds) {
      return RELATIVE_FORMAT.format(Math.trunc(deltaSeconds / seconds), unit)
    }
  }
  return 'just now'
}
