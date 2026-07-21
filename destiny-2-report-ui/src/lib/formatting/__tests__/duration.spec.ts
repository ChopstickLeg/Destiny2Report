import { describe, expect, it } from 'vitest'
import { formatClock, formatDurationCompact, formatHours, parseTimeSpan } from '../duration'

describe('parseTimeSpan', () => {
  it('parses hh:mm:ss', () => {
    expect(parseTimeSpan('04:32:18')).toBe(4 * 3600 + 32 * 60 + 18)
  })

  it('parses day components beyond 24 hours', () => {
    expect(parseTimeSpan('124.10:03:22')).toBe(124 * 86400 + 10 * 3600 + 3 * 60 + 22)
  })

  it('parses fractional seconds', () => {
    expect(parseTimeSpan('00:32:11.5000000')).toBeCloseTo(32 * 60 + 11.5)
  })

  it('parses negative durations', () => {
    expect(parseTimeSpan('-01:00:00')).toBe(-3600)
  })

  it('parses single-digit hour segments', () => {
    expect(parseTimeSpan('1:05:09')).toBe(3600 + 5 * 60 + 9)
  })

  it('returns null for missing input instead of zero', () => {
    expect(parseTimeSpan(null)).toBeNull()
    expect(parseTimeSpan(undefined)).toBeNull()
    expect(parseTimeSpan('')).toBeNull()
  })

  it('returns null for malformed input', () => {
    expect(parseTimeSpan('not a timespan')).toBeNull()
    expect(parseTimeSpan('99:99')).toBeNull()
  })
})

describe('formatHours', () => {
  it('formats large totals as grouped whole hours', () => {
    expect(formatHours(1842 * 3600)).toBe('1,842 h')
  })

  it('degrades to minutes below one hour', () => {
    expect(formatHours(45 * 60)).toBe('45 min')
  })
})

describe('formatDurationCompact', () => {
  it('uses the two largest units', () => {
    expect(formatDurationCompact(2 * 3600 + 14 * 60)).toBe('2h 14m')
    expect(formatDurationCompact(5 * 86400 + 3 * 3600)).toBe('5d 3h')
  })

  it('handles sub-minute values', () => {
    expect(formatDurationCompact(30)).toBe('30s')
  })
})

describe('formatClock', () => {
  it('includes hours only when needed', () => {
    expect(formatClock(4 * 3600 + 32 * 60 + 18)).toBe('04:32:18')
    expect(formatClock(24 * 60 + 3)).toBe('24:03')
  })
})
