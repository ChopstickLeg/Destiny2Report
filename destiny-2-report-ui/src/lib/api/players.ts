import { apiFetch, isApiError } from './http'
import type { PlayerSearchResult } from './types'

/**
 * Player search uses the nonstandard HTTP `QUERY` verb with a JSON body.
 * A 404 from the API means "no matches" and is surfaced as an empty list.
 */
export async function searchPlayers(
  displayNamePrefix: string,
  displayCode: number | null = null,
  signal?: AbortSignal,
): Promise<PlayerSearchResult[]> {
  try {
    return await apiFetch<PlayerSearchResult[]>('/players/search', {
      method: 'QUERY',
      body: { displayNamePrefix, displayCode },
      signal,
    })
  } catch (error) {
    if (isApiError(error) && error.isNotFound) return []
    throw error
  }
}
