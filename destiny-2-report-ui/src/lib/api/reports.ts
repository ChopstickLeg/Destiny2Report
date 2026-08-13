import { apiFetch, isApiError } from './http'
import type {
  ActivityModeParam,
  ActivityPlaytimeAggregateReport,
  DeathActivityModeAggregateReport,
  DestinyReport,
  PlayerLeaderboardStandingsResponse,
  ReportQueueResponse,
  StoryVisualAssetsReport,
  WeaponActivityModeAggregateReport,
} from './types'
import { withTurnstileToken } from '../turnstile'

export interface ReportIdentity {
  membershipTypeId: number
  membershipId: string
}

interface StoryShareResponse {
  token: string
}

/**
 * `null` means no report has been generated yet. This is a legitimate state that
 * drives the "Generate report" experience, distinct from request failure.
 */
export async function fetchReport(
  identity: ReportIdentity,
  signal?: AbortSignal,
): Promise<DestinyReport | null> {
  try {
    return await apiFetch<DestinyReport>(
      `/reports/${identity.membershipTypeId}/${identity.membershipId}`,
      { signal },
    )
  } catch (error) {
    if (isApiError(error) && error.isNotFound) return null
    throw error
  }
}

export function fetchPlayerStandings(
  identity: ReportIdentity,
  signal?: AbortSignal,
): Promise<PlayerLeaderboardStandingsResponse> {
  return apiFetch(`/leaderboards/players/${identity.membershipTypeId}/${identity.membershipId}`, {
    signal,
  })
}

export function fetchWeapons(
  identity: ReportIdentity,
  mode: ActivityModeParam,
  signal?: AbortSignal,
): Promise<WeaponActivityModeAggregateReport> {
  return apiFetch(
    `/reports/${identity.membershipTypeId}/${identity.membershipId}/weapons/${mode}`,
    {
      signal,
    },
  )
}

export function fetchStoryVisualAssets(signal?: AbortSignal): Promise<StoryVisualAssetsReport> {
  return apiFetch('/reports/story-assets', { signal })
}

export function createStoryShare(identity: ReportIdentity): Promise<StoryShareResponse> {
  return apiFetch('/reports/story-shares', {
    method: 'POST',
    body: identity,
  })
}

export function resolveStoryShare(token: string, signal?: AbortSignal): Promise<ReportIdentity> {
  return apiFetch(`/reports/story-shares/${encodeURIComponent(token)}`, { signal })
}

export function fetchDeaths(
  identity: ReportIdentity,
  mode: ActivityModeParam,
  signal?: AbortSignal,
): Promise<DeathActivityModeAggregateReport> {
  return apiFetch(`/reports/${identity.membershipTypeId}/${identity.membershipId}/deaths/${mode}`, {
    signal,
  })
}

export function fetchPlaytime(
  identity: ReportIdentity,
  mode: ActivityModeParam,
  signal?: AbortSignal,
): Promise<ActivityPlaytimeAggregateReport> {
  return apiFetch(
    `/reports/${identity.membershipTypeId}/${identity.membershipId}/playtime/${mode}`,
    { signal },
  )
}

export async function queueReport(identity: ReportIdentity): Promise<ReportQueueResponse> {
  return withTurnstileToken((turnstileToken) =>
    apiFetch<ReportQueueResponse>('/reports/queue', {
      method: 'POST',
      body: {
        membershipTypeId: identity.membershipTypeId,
        membershipId: identity.membershipId,
        turnstileToken,
      },
    }),
  )
}

/** Stable TanStack Query keys for everything report-related. */
export const reportKeys = {
  report: (identity: ReportIdentity) =>
    ['report', identity.membershipTypeId, identity.membershipId] as const,
  standings: (identity: ReportIdentity) =>
    ['report', identity.membershipTypeId, identity.membershipId, 'standings'] as const,
  weapons: (identity: ReportIdentity, mode: ActivityModeParam) =>
    ['report', identity.membershipTypeId, identity.membershipId, 'weapons', mode] as const,
  deaths: (identity: ReportIdentity, mode: ActivityModeParam) =>
    ['report', identity.membershipTypeId, identity.membershipId, 'deaths', mode] as const,
  playtime: (identity: ReportIdentity, mode: ActivityModeParam) =>
    ['report', identity.membershipTypeId, identity.membershipId, 'playtime', mode] as const,
  storyAssets: () => ['report', 'story-assets'] as const,
  storyShare: (token: string) => ['report', 'story-share', token] as const,
}
