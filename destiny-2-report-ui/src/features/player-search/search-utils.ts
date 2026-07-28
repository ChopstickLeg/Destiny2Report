/**
 * Search input interpretation.
 *
 * Players paste names with or without the Bungie code suffix ("Name#1234").
 * The API searches by display-name prefix only, so the code is stripped
 * before the request and used to narrow results client-side afterward.
 */

import type { PlayerSearchResult } from '@/lib/api/types'

interface ParsedSearchQuery {
  prefix: string
  code: number | null
}

export function parseSearchQuery(raw: string): ParsedSearchQuery {
  const trimmed = raw.trim()
  const hashIndex = trimmed.lastIndexOf('#')
  if (hashIndex > 0) {
    const codePart = trimmed.slice(hashIndex + 1)
    if (/^\d{1,4}$/.test(codePart)) {
      return { prefix: trimmed.slice(0, hashIndex).trim(), code: Number(codePart) }
    }
  }
  return { prefix: trimmed, code: null }
}

const MIN_SEARCH_LENGTH = 2

export function isSearchable(query: ParsedSearchQuery): boolean {
  return query.prefix.length >= MIN_SEARCH_LENGTH
}

/** Narrow results by an exact display code when the user typed one. */
export function filterByCode(
  results: PlayerSearchResult[],
  code: number | null,
): PlayerSearchResult[] {
  if (code === null) return results
  const exact = results.filter((result) => result.displayCode === code)
  // If the code eliminated everything, fall back to the full list rather
  // than implying nobody exists under that name.
  return exact.length > 0 ? exact : results
}
