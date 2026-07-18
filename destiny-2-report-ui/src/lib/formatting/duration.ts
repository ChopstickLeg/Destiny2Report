/**
 * .NET `TimeSpan` handling.
 *
 * The API serializes `TimeSpan` with System.Text.Json's constant ("c")
 * format: `[-][d.]hh:mm:ss[.fffffff]`, e.g. "124.10:03:22" or
 * "00:32:11.5000000". Durations are parsed once into seconds and kept
 * numeric for sorting and chart math; formatting is contextual rather
 * than universal.
 */

const TIMESPAN_PATTERN = /^(-)?(?:(\d+)\.)?(\d{1,2}):([0-5]?\d):([0-5]?\d)(?:\.(\d{1,7}))?$/

/**
 * Parse a .NET TimeSpan string into total seconds.
 * Returns `null` for missing or malformed input — callers decide how to
 * treat absence; missing data is never silently converted to zero.
 */
export function parseTimeSpan(value: string | null | undefined): number | null {
  if (!value) return null
  const match = TIMESPAN_PATTERN.exec(value.trim())
  if (!match) return null

  const [, sign, days, hours, minutes, seconds, fraction] = match
  let total =
    Number(days ?? 0) * 86_400 + Number(hours) * 3_600 + Number(minutes) * 60 + Number(seconds)
  if (fraction) {
    total += Number(`0.${fraction}`)
  }
  return sign === '-' ? -total : total
}

const HOURS_FORMAT = new Intl.NumberFormat('en-US', { maximumFractionDigits: 0 })

/**
 * Whole-hour presentation for large totals: "1,842 h".
 * Below one hour it degrades to minutes so small values stay honest.
 */
export function formatHours(totalSeconds: number): string {
  if (totalSeconds < 3_600) {
    return `${Math.round(totalSeconds / 60)} min`
  }
  return `${HOURS_FORMAT.format(totalSeconds / 3_600)} h`
}

/**
 * Two-unit compact form: "5d 3h", "2h 14m", "45m", "30s".
 */
export function formatDurationCompact(totalSeconds: number): string {
  const seconds = Math.round(Math.abs(totalSeconds))
  const sign = totalSeconds < 0 ? '-' : ''
  const days = Math.floor(seconds / 86_400)
  const hours = Math.floor((seconds % 86_400) / 3_600)
  const minutes = Math.floor((seconds % 3_600) / 60)
  const secs = seconds % 60

  if (days > 0) return `${sign}${days}d${hours > 0 ? ` ${hours}h` : ''}`
  if (hours > 0) return `${sign}${hours}h${minutes > 0 ? ` ${minutes}m` : ''}`
  if (minutes > 0) return `${sign}${minutes}m`
  return `${sign}${secs}s`
}

/**
 * Clock form for precise timings such as fastest clears:
 * "04:32:18" when an hour or longer, otherwise "32:18".
 */
export function formatClock(totalSeconds: number): string {
  const seconds = Math.round(Math.abs(totalSeconds))
  const hours = Math.floor(seconds / 3_600)
  const minutes = Math.floor((seconds % 3_600) / 60)
  const secs = seconds % 60
  const pad = (n: number) => String(n).padStart(2, '0')

  if (hours > 0) return `${pad(hours)}:${pad(minutes)}:${pad(secs)}`
  return `${pad(minutes)}:${pad(secs)}`
}
