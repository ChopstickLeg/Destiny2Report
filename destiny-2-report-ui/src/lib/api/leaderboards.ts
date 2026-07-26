import { apiFetch } from './http'
import type { LeaderboardCatalogResponse, LeaderboardPageResponse } from './types'

export const leaderboardKeys = {
  catalog: ['leaderboards'] as const,
  board: (key: string) => ['leaderboards', key] as const,
}

export function fetchLeaderboardCatalog(signal?: AbortSignal): Promise<LeaderboardCatalogResponse> {
  return apiFetch('/leaderboards', { signal })
}

export function fetchLeaderboard(
  key: string,
  offset: number,
  signal?: AbortSignal,
): Promise<LeaderboardPageResponse> {
  return apiFetch(`/leaderboards/${encodeURIComponent(key)}?offset=${offset}&limit=250`, { signal })
}
