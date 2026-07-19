/**
 * Recently viewed players, kept in localStorage so the home page can offer
 * quick return paths. Identity is membership type + id; display fields are
 * a convenience snapshot only.
 */

export interface RecentPlayer {
  membershipTypeId: number
  membershipId: string
  displayName: string
  displayCode: number | null
  emblemIconUrl: string
  viewedAt: number
}

const STORAGE_KEY = 'd2r.recent-players'
const LIMIT = 6

export function loadRecentPlayers(): RecentPlayer[] {
  try {
    const raw = localStorage.getItem(STORAGE_KEY)
    if (!raw) return []
    const parsed: unknown = JSON.parse(raw)
    if (!Array.isArray(parsed)) return []
    return parsed.filter(
      (entry): entry is RecentPlayer =>
        typeof entry === 'object' &&
        entry !== null &&
        typeof (entry as RecentPlayer).membershipId === 'string' &&
        typeof (entry as RecentPlayer).membershipTypeId === 'number',
    )
  } catch {
    return []
  }
}

export function rememberPlayer(player: Omit<RecentPlayer, 'viewedAt'>): void {
  try {
    const existing = loadRecentPlayers().filter(
      (entry) =>
        entry.membershipId !== player.membershipId ||
        entry.membershipTypeId !== player.membershipTypeId,
    )
    const next = [{ ...player, viewedAt: Date.now() }, ...existing].slice(0, LIMIT)
    localStorage.setItem(STORAGE_KEY, JSON.stringify(next))
  } catch {
    // Storage may be unavailable in private mode or when the quota is full. Recents are optional.
  }
}
