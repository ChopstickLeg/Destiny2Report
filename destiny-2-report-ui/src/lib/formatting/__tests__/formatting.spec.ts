import { describe, expect, it } from 'vitest'
import { formatInteger, formatPercent, formatRatio, formatShare } from '../numbers'
import { completeYearsSince, formatRelative, parseApiDate } from '../dates'

describe('formatPercent', () => {
  it('multiplies a backend fraction by 100 exactly once', () => {
    expect(formatPercent(0.5344)).toBe('53.4%')
    expect(formatPercent(0.8571, 0)).toBe('86%')
    expect(formatPercent(1)).toBe('100.0%')
  })
})

describe('formatRatio', () => {
  it('keeps a stable two-decimal presentation', () => {
    expect(formatRatio(1.5)).toBe('1.50')
    expect(formatRatio(0)).toBe('0.00')
  })
})

describe('formatInteger', () => {
  it('groups thousands', () => {
    expect(formatInteger(1234567)).toBe('1,234,567')
  })
})

describe('formatShare', () => {
  it('returns a dash rather than implying measured zero on empty totals', () => {
    expect(formatShare(5, 0)).toBe('N/A')
    expect(formatShare(1, 4, 0)).toBe('25%')
  })
})

describe('parseApiDate', () => {
  it('treats bare DateTime values as UTC', () => {
    const date = parseApiDate('2026-07-01T12:00:00')
    expect(date?.toISOString()).toBe('2026-07-01T12:00:00.000Z')
  })

  it('keeps explicit offsets', () => {
    const date = parseApiDate('2026-07-01T12:00:00Z')
    expect(date?.toISOString()).toBe('2026-07-01T12:00:00.000Z')
  })

  it('returns null for invalid input', () => {
    expect(parseApiDate('never')).toBeNull()
    expect(parseApiDate(null)).toBeNull()
  })
})

describe('formatRelative', () => {
  const now = new Date('2026-07-13T00:00:00Z')

  it('picks a sensible unit', () => {
    expect(formatRelative(new Date('2026-07-10T00:00:00Z'), now)).toBe('3 days ago')
    expect(formatRelative(new Date('2026-07-12T22:00:00Z'), now)).toBe('2 hours ago')
    expect(formatRelative(new Date('2026-07-12T23:59:40Z'), now)).toBe('just now')
  })
})

describe('completeYearsSince', () => {
  const started = new Date('2017-09-06T17:00:00Z')

  it('counts only completed calendar years', () => {
    expect(completeYearsSince(started, new Date('2026-09-05T23:59:59Z'))).toBe(8)
    expect(completeYearsSince(started, new Date('2026-09-06T00:00:00Z'))).toBe(9)
  })

  it('does not return a negative tenure for future dates', () => {
    expect(completeYearsSince(new Date('2027-01-01T00:00:00Z'), new Date('2026-01-01T00:00:00Z'))).toBe(0)
  })
})
