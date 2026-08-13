import { describe, expect, it } from 'vitest'
import { filterByCode, isSearchable, parseSearchQuery } from '../search-utils'
import type { PlayerSearchResult } from '@/lib/api/types'

function result(displayName: string, displayCode: number | null): PlayerSearchResult {
  return {
    displayName,
    displayCode,
    membershipId: '1',
    membershipTypeId: 3,
    emblemIconUrl: '',
    queueTicket: 'ticket',
  }
}

describe('parseSearchQuery', () => {
  it('splits a full Bungie name into prefix and code', () => {
    expect(parseSearchQuery('Guardian#1234')).toEqual({ prefix: 'Guardian', code: 1234 })
  })

  it('accepts short codes', () => {
    expect(parseSearchQuery('Guardian#7')).toEqual({ prefix: 'Guardian', code: 7 })
  })

  it('leaves plain names alone', () => {
    expect(parseSearchQuery('  Guardian  ')).toEqual({ prefix: 'Guardian', code: null })
  })

  it('ignores non-numeric suffixes', () => {
    expect(parseSearchQuery('Guardian#abc')).toEqual({ prefix: 'Guardian#abc', code: null })
  })

  it('does not treat a leading hash as a code separator', () => {
    expect(parseSearchQuery('#1234')).toEqual({ prefix: '#1234', code: null })
  })
})

describe('isSearchable', () => {
  it('requires at least two prefix characters', () => {
    expect(isSearchable(parseSearchQuery('G'))).toBe(false)
    expect(isSearchable(parseSearchQuery('Gu'))).toBe(true)
  })
})

describe('filterByCode', () => {
  const results = [result('Guardian', 1234), result('Guardian', 5678)]

  it('narrows to the exact code', () => {
    expect(filterByCode(results, 1234)).toEqual([results[0]])
  })

  it('passes everything through without a code', () => {
    expect(filterByCode(results, null)).toEqual(results)
  })

  it('falls back to all results when the code matches nobody', () => {
    expect(filterByCode(results, 9999)).toEqual(results)
  })
})
