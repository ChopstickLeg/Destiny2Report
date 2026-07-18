/**
 * Pure view-model selectors for the report overview.
 *
 * Editorial rules live here as tested functions rather than inline template
 * logic: zero values are omitted rather than rendered as rows of zeroes,
 * "favorite"/"most" language is only used for measured rankings, and
 * competitive ratios come from the backend's own KD fields.
 */

import type {
  ActivityCompletionSummary,
  CharacterPlaytimeReport,
  DestinyReport,
  PlaytimeStreakReport,
} from '@/lib/api/types'
import { parseTimeSpan } from '@/lib/formatting/duration'
import { parseApiDate } from '@/lib/formatting/dates'

// ---------------------------------------------------------------------------
// Time spent
// ---------------------------------------------------------------------------

export interface RankedDuration {
  key: string
  label: string
  seconds: number
  tag?: string
  className?: string
}

export function rankCharacterPlaytime(characters: CharacterPlaytimeReport[]): RankedDuration[] {
  return characters
    .map((character, index) => ({
      key: `${character.class}-${character.race}-${index}`,
      label: `${character.class} · ${character.race}`,
      seconds: parseTimeSpan(character.playtime) ?? 0,
      tag: character.isDeleted ? 'Deleted' : undefined,
      className: character.class,
    }))
    .filter((entry) => entry.seconds > 0)
    .sort((a, b) => b.seconds - a.seconds)
}

const PATROL_DESTINATION_NAMES: Readonly<Record<string, string>> = {
  'Arcadian Valley': 'Nessus',
  'Echo Mesa': 'IO',
  'Hellas Basin': 'Mars',
  'New Pacific Arcology': 'Titan',
  Nessus: 'Nessus',
  IO: 'IO',
  Mars: 'Mars',
  Titan: 'Titan',
  'The Pale Heart': 'The Pale Heart',
  'European Dead Zone': 'European Dead Zone',
  'The Moon': 'The Moon',
  Europa: 'Europa',
  Neomuna: 'Neomuna',
  Kepler: 'Kepler',
  'The Dreaming City': 'The Dreaming City',
  'The Tangled Shore': 'The Tangled Shore',
  "Savathûn's Throne World": "Savathûn's Throne World",
  Cosmodrome: 'Cosmodrome',
  Mercury: 'Mercury',
  'Tharsis Expanse': 'Tharsis Expanse',
  Eternity: 'Eternity',
}

export function rankPatrolTime(patrolTimeByPlanet: Record<string, string>): RankedDuration[] {
  const secondsByDestination = new Map<string, number>()

  for (const [backendName, timespan] of Object.entries(patrolTimeByPlanet)) {
    const destination = PATROL_DESTINATION_NAMES[backendName]
    if (!destination) continue

    const seconds = parseTimeSpan(timespan) ?? 0
    if (seconds <= 0) continue
    secondsByDestination.set(destination, (secondsByDestination.get(destination) ?? 0) + seconds)
  }

  return [...secondsByDestination.entries()]
    .map(([destination, seconds]) => ({
      key: destination,
      label: destination,
      seconds,
    }))
    .sort((a, b) => b.seconds - a.seconds)
}

export interface StreakSummary {
  days: number
  start: Date
  end: Date
}

export function summarizeStreak(streak: PlaytimeStreakReport | null): StreakSummary | null {
  if (!streak) return null
  const start = parseApiDate(streak.startDate)
  const end = parseApiDate(streak.endDate)
  if (!start || !end || end < start) return null
  const days = Math.round((end.getTime() - start.getTime()) / 86_400_000) + 1
  return { days, start, end }
}

// ---------------------------------------------------------------------------
// Endgame
// ---------------------------------------------------------------------------

/**
 * Meaningful history first: activities with clears, ranked by clears; then
 * attempted-but-uncleared activities by attempts.
 */
export function sortCompletions(
  completions: ActivityCompletionSummary[],
): ActivityCompletionSummary[] {
  return [...completions].sort((a, b) => {
    const aCleared = a.completionCount > 0 ? 1 : 0
    const bCleared = b.completionCount > 0 ? 1 : 0
    if (aCleared !== bCleared) return bCleared - aCleared
    if (a.completionCount !== b.completionCount) return b.completionCount - a.completionCount
    return b.activityCount - a.activityCount
  })
}

export interface Distinction {
  key: string
  label: string
}

/** Earned facts only — never decorative chips. */
export function distinctions(summary: ActivityCompletionSummary): Distinction[] {
  const earned: Distinction[] = []
  if (summary.contestClear) earned.push({ key: 'contest', label: 'Contest' })
  if (summary.soloFlawlessClear) earned.push({ key: 'solo-flawless', label: 'Solo Flawless' })
  else {
    if (summary.flawlessClear) earned.push({ key: 'flawless', label: 'Flawless' })
    if (summary.soloClear) earned.push({ key: 'solo', label: 'Solo' })
  }
  return earned
}

// ---------------------------------------------------------------------------
// Ranked dictionaries (Crucible kills by mode, motes, sherpas)
// ---------------------------------------------------------------------------

export interface RankedCount {
  key: string
  label: string
  value: number
}

const MODE_NAME_PATTERN = /(?<=[a-z])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])/g
const ACTIVITY_MODE_NAMES: Readonly<Record<string, string>> = {
  'Mode 93': 'Lawless Frontier',
  'Mode 94': 'Sparrow Racing League',
}

/** Replaces known API placeholders, then formats PascalCase names for display. */
export function humanizeModeName(name: string): string {
  return ACTIVITY_MODE_NAMES[name] ?? name.replace(MODE_NAME_PATTERN, ' ')
}

export function rankByMode(byMode: Record<string, number>, limit = 8): RankedCount[] {
  const ranked = Object.entries(byMode)
    .map(([mode, value]) => ({ key: mode, label: humanizeModeName(mode), value }))
    .filter((entry) => entry.value > 0)
    .sort((a, b) => b.value - a.value)

  if (ranked.length <= limit) return ranked
  const head = ranked.slice(0, limit)
  const tail = ranked.slice(limit)
  head.push({
    key: 'other',
    label: `Other (${tail.length} modes)`,
    value: tail.reduce((sum, entry) => sum + entry.value, 0),
  })
  return head
}

// ---------------------------------------------------------------------------
// Section presence — sections collapse entirely instead of showing zeroes.
// ---------------------------------------------------------------------------

export function hasTimeData(report: DestinyReport): boolean {
  return (
    rankCharacterPlaytime(report.characterPlaytime).length > 0 ||
    rankPatrolTime(report.patrolTimeByPlanet).length > 0
  )
}

export function hasCompetitiveData(report: DestinyReport): boolean {
  return report.crucibleMatchesPlayed > 0 || report.gambitMatchesPlayed > 0
}

export function hasEndgameData(report: DestinyReport): boolean {
  return (
    report.raidCompletions.length > 0 ||
    report.dungeonCompletions.length > 0 ||
    report.conquestCompletions.length > 0
  )
}

export function hasSealsData(report: DestinyReport): boolean {
  return report.triumphSeals.length > 0
}

export function hasOdditiesData(report: DestinyReport): boolean {
  return (
    report.goodBoyProtocol > 0 ||
    report.fishCaught > 0 ||
    report.misadventures > 0 ||
    report.zeroKillActivities > 0
  )
}

export function hasSocialData(report: DestinyReport): boolean {
  return (
    report.uniquePlayersPlayedWith > 0 ||
    report.mostPlayedWith.length > 0 ||
    report.playersSherpaed.length > 0
  )
}

export function hasEmblemData(report: DestinyReport): boolean {
  return report.mostUsedEmblems.length > 0
}
