/**
 * Editorial selectors for the "Your Story" retrospective.
 *
 * Every highlight follows explicit rules, kept as pure functions so they
 * can be unit-tested against sparse and extreme reports:
 *  - zero/absent values are omitted, never padded with filler;
 *  - "favorite"/"most" language only describes a measured #1 ranking;
 *  - earned endgame distinctions outrank generic counts;
 *  - competitive claims always carry their sample size;
 *  - the story describes all-history ("your Destiny 2 history"), never a
 *    year or season the backend cannot support.
 */

import type {
  ActivityCompletionSummary,
  DestinyReport,
  StoryVisualAssetsReport,
  WeaponActivityModeAggregateReport,
} from '@/lib/api/types'
import { formatClock, formatHours, parseTimeSpan } from '@/lib/formatting/duration'
import { formatInteger, formatPercent, formatRatio } from '@/lib/formatting/numbers'
import {
  humanizeModeName,
  rankCharacterPlaytime,
  rankPatrolTime,
  summarizeStreak,
} from '@/features/report-overview/report-view'

export interface StoryFact {
  label: string
  value: string
  /** Comparison basis or definition; keeps claims honest. */
  note?: string
}

export interface StoryChapter {
  key: string
  kicker: string
  title: string
  lead: string
  facts: StoryFact[]
}

export type StoryTone = 'gold' | 'solar' | 'arc' | 'void' | 'neutral'

export interface StoryImage {
  url: string
  alt: string
}

export type StoryLayout =
  | 'achievement-list'
  | 'contest-gallery'
  | 'pantheon-gallery'
  | 'seal-gallery'
  | 'split-tally'
  | 'sherpa-spotlight'
  | 'class-breakdown'
  | 'weapon-leaderboard'
  | 'teammate-profile'
  | 'match-scoreboard'
  | 'emblem-banner'
  | 'personality-number'

export interface StoryStat {
  label: string
  value: string
  numericValue?: number
  share?: number
  iconUrl?: string
  color?: string
}

export interface StoryRankedItem {
  label: string
  value: string
  imageUrl?: string
  group?: string
}

/** A single beat in the interactive story experience. */
export interface StorySlide {
  key: string
  layout: StoryLayout
  eyebrow: string
  title: string
  value: string
  body: string
  detail?: string
  tone: StoryTone
  iconUrl?: string
  imageUrl?: string
  imageAlt?: string
  imageUrls?: StoryImage[]
  stats?: StoryStat[]
  items?: StoryRankedItem[]
}

export interface StoryWeapon {
  name: string
  iconUrl: string
  kills: number
}

/** Playlists need at least this many matches before being praised. */
export const MIN_PLAYLIST_SAMPLE = 20

/**
 * Selects a short, accomplishment-first narrative rather than mirroring the
 * report dashboard. Rare earned distinctions lead; scale and personality
 * follow; routine stats only qualify when they tell a notable story.
 */
export function mostUsedActualWeapons(
  weapons: WeaponActivityModeAggregateReport | null | undefined,
  limit = 5,
): StoryWeapon[] {
  if (!weapons) return []

  const totals = new Map<number, StoryWeapon>()
  for (const character of weapons.classes) {
    for (const mode of character.modes) {
      for (const category of mode.categories) {
        for (const weapon of category.weapons) {
          if (
            weapon.referenceId <= 0 ||
            !weapon.iconUrl ||
            weapon.weaponName.trim().toLowerCase() === 'unknown'
          ) {
            continue
          }

          const existing = totals.get(weapon.referenceId)
          totals.set(weapon.referenceId, {
            name: weapon.weaponName,
            iconUrl: weapon.iconUrl,
            kills: (existing?.kills ?? 0) + weapon.kills,
          })
        }
      }
    }
  }

  return [...totals.values()].sort((a, b) => b.kills - a.kills).slice(0, limit)
}

export function mostUsedActualWeapon(
  weapons: WeaponActivityModeAggregateReport | null | undefined,
): StoryWeapon | null {
  return mostUsedActualWeapons(weapons, 1)[0] ?? null
}

function classBreakdown(
  report: DestinyReport,
  assets?: StoryVisualAssetsReport | null,
): StoryStat[] {
  const totals = new Map<string, number>()
  for (const character of report.characterPlaytime) {
    const seconds = parseTimeSpan(character.playtime) ?? 0
    if (seconds <= 0 || character.class === 'Unknown') continue
    totals.set(character.class, (totals.get(character.class) ?? 0) + seconds)
  }
  const total = [...totals.values()].reduce((sum, seconds) => sum + seconds, 0)
  const icons: Record<string, string | undefined> = {
    Titan: assets?.titanIconUrl,
    Hunter: assets?.hunterIconUrl,
    Warlock: assets?.warlockIconUrl,
  }
  const colors: Record<string, string> = {
    Titan: 'var(--color-class-titan)',
    Hunter: 'var(--color-class-hunter)',
    Warlock: 'var(--color-class-warlock)',
  }
  return [...totals.entries()]
    .sort((a, b) => b[1] - a[1])
    .map(([label, seconds]) => ({
      label,
      value: formatHours(seconds),
      share: total > 0 ? seconds / total : 0,
      iconUrl: icons[label],
      color: colors[label],
    }))
}

function boundedNames(names: string[], visible = 3): string {
  const shown = names.slice(0, visible).join(', ')
  const remaining = names.length - visible
  return remaining > 0 ? `${shown} + ${formatInteger(remaining)} more` : shown
}

/** Stable FNV-1a hash used to make per-player editorial choices repeatable. */
function stableHash(value: string): number {
  let hash = 0x811c9dc5
  for (let index = 0; index < value.length; index += 1) {
    hash ^= value.charCodeAt(index)
    hash = Math.imul(hash, 0x01000193)
  }
  return hash >>> 0
}

function raidAssetKey(name: string): string {
  return name.toLocaleLowerCase().replace(/[^a-z0-9]/g, '')
}

function pantheonAssetKey(name: string): string {
  return raidAssetKey(name)
    .replace(/customize$/, '')
    .replace(/^thepantheon/, 'pantheon')
}

function isPantheonActivity(name: string): boolean {
  return pantheonAssetKey(name).startsWith('pantheon')
}

function canonicalPantheonName(name: string): string {
  return name.replace(/: Customize$/i, '').replace(/^The Pantheon:/i, 'Pantheon:')
}

const PANTHEON_2_KEYS = new Set([
  pantheonAssetKey('Pantheon: Calus Resplendent'),
  pantheonAssetKey('Pantheon: Morgeth Surpassing'),
  pantheonAssetKey('Pantheon: Insurrection Prime Revolutionary'),
])

export function buildStorySlides(
  report: DestinyReport,
  weapons?: WeaponActivityModeAggregateReport | null,
  assets?: StoryVisualAssetsReport | null,
): StorySlide[] {
  const slides: StorySlide[] = []
  const emblem = [...report.mostUsedEmblems]
    .map((item) => ({ ...item, seconds: parseTimeSpan(item.totalPlaytime) ?? 0 }))
    .sort((a, b) => b.seconds - a.seconds)[0]

  const soloFlawless = countDistinction(
    report.dungeonCompletions,
    (activity) => activity.soloFlawlessClear,
  )
  if (soloFlawless.length > 0) {
    slides.push({
      key: 'solo-flawless',
      layout: 'achievement-list',
      eyebrow: 'Solo flawless dungeons',
      title: 'You cleared them alone without dying.',
      value: `${formatInteger(soloFlawless.length)} ${soloFlawless.length === 1 ? 'dungeon' : 'dungeons'} solo flawless`,
      body: boundedNames(soloFlawless),
      detail: 'Completed from the beginning with no fireteam and zero deaths.',
      tone: 'gold',
      iconUrl: assets?.dungeonIconUrl,
    })
  }

  const contest = countDistinction(report.raidCompletions, (activity) => activity.contestClear)
  if (contest.length > 0) {
    const contestEmblems = new Map(
      assets?.contestRaidEmblems?.map((emblem) => [raidAssetKey(emblem.raidName), emblem]) ?? [],
    )
    slides.push({
      key: 'contest',
      layout: 'contest-gallery',
      eyebrow: 'Contest raid clears',
      title: 'You were there before the difficulty came down.',
      value: `${formatInteger(contest.length)} ${contest.length === 1 ? 'raid' : 'raids'} cleared on contest`,
      body: 'Cleared while each raid’s contest modifier was active.',
      tone: 'solar',
      items: contest.map((raidName) => {
        const emblem = contestEmblems.get(raidAssetKey(raidName))
        return {
          label: raidName,
          value: emblem?.emblemName ?? 'Contest clear',
          imageUrl: emblem?.iconUrl ?? assets?.raidIconUrl,
        }
      }),
    })
  }

  const completedPantheon = new Map(
    report.raidCompletions
      .filter(
        (activity) => activity.completionCount > 0 && isPantheonActivity(activity.activityName),
      )
      .map((activity) => [pantheonAssetKey(activity.activityName), activity]),
  )
  const pantheonEmblems = new Map(
    (assets?.pantheonEmblems ?? []).map((emblem) => [
      pantheonAssetKey(emblem.pantheonName),
      emblem,
    ]),
  )
  const catalogKeys = (assets?.pantheonEmblems ?? [])
    .map((emblem) => pantheonAssetKey(emblem.pantheonName))
    .filter((key) => completedPantheon.has(key))
  const pantheonKeys = [
    ...catalogKeys,
    ...[...completedPantheon.keys()].filter((key) => !pantheonEmblems.has(key)),
  ]
  if (pantheonKeys.length > 0) {
    slides.push({
      key: 'pantheon',
      layout: 'pantheon-gallery',
      eyebrow: 'Pantheon completions',
      title: 'Your completed Pantheon tiers.',
      value: `${formatInteger(pantheonKeys.length)} ${pantheonKeys.length === 1 ? 'tier' : 'tiers'} completed`,
      body: 'Completed Pantheon lineups from both runs of the boss gauntlet.',
      detail: 'Only fully completed Pantheon activities appear here.',
      tone: 'gold',
      items: pantheonKeys.map((key) => {
        const activity = completedPantheon.get(key)!
        const emblem = pantheonEmblems.get(key)
        return {
          label: emblem?.pantheonName ?? canonicalPantheonName(activity.activityName),
          value: emblem?.emblemName ?? 'Pantheon clear',
          imageUrl: emblem?.iconUrl ?? assets?.raidIconUrl,
          group: PANTHEON_2_KEYS.has(key) ? 'Pantheon 2.0' : 'Pantheon 1.0',
        }
      }),
    })
  }

  const completedSeals = report.triumphSeals.filter((seal) => seal.isCompleted)
  if (completedSeals.length > 0) {
    const featured = completedSeals[0]!
    slides.push({
      key: 'seals',
      layout: 'seal-gallery',
      eyebrow: 'Completed titles',
      title:
        completedSeals.length === 1 ? 'You earned a title.' : 'You collected titles across Sol.',
      value:
        completedSeals.length === 1
          ? featured.name
          : `${formatInteger(completedSeals.length)} completed titles`,
      body:
        completedSeals.length === 1
          ? featured.description
          : boundedNames(completedSeals.map((seal) => seal.name)),
      detail: 'Completed triumph seals recorded on your account.',
      tone: 'void',
      imageUrls: completedSeals
        .filter((seal) => seal.iconUrl)
        .slice(0, 6)
        .map((seal) => ({ url: seal.iconUrl, alt: `${seal.name} title icon` })),
    })
  }

  const raidClears = report.raidCompletions.reduce((sum, raid) => sum + raid.completionCount, 0)
  const dungeonClears = report.dungeonCompletions.reduce(
    (sum, dungeon) => sum + dungeon.completionCount,
    0,
  )
  const endgameClears = raidClears + dungeonClears
  if (endgameClears > 0) {
    slides.push({
      key: 'endgame',
      layout: 'split-tally',
      eyebrow: 'Raid and dungeon completions',
      title: 'You kept going back to the endgame.',
      value: `${formatInteger(endgameClears)} total clears`,
      body: `${formatInteger(raidClears)} raid clears + ${formatInteger(dungeonClears)} dungeon clears.`,
      tone: 'void',
      stats: [
        {
          label: 'Raid clears',
          value: formatInteger(raidClears),
          numericValue: raidClears,
          share: raidClears / endgameClears,
        },
        {
          label: 'Dungeon clears',
          value: formatInteger(dungeonClears),
          numericValue: dungeonClears,
          share: dungeonClears / endgameClears,
        },
      ],
    })
  }

  const sherpaed = report.playersSherpaed.reduce((sum, item) => sum + item.playerCount, 0)
  if (sherpaed > 0) {
    const topSherpa = [...report.playersSherpaed].sort((a, b) => b.playerCount - a.playerCount)[0]
    slides.push({
      key: 'sherpas',
      layout: 'sherpa-spotlight',
      eyebrow: 'Raid sherpas',
      title: 'You helped first-time raiders reach the finish line.',
      value: `${formatInteger(sherpaed)} first-time raiders guided`,
      body: 'Players whose first recorded clear happened in a raid you completed with them.',
      detail: topSherpa
        ? `${topSherpa.raidName} accounts for ${formatInteger(topSherpa.playerCount)} of those sherpas.`
        : undefined,
      tone: 'gold',
      iconUrl: assets?.guidedGamesIconUrl,
      items: [...report.playersSherpaed]
        .sort((a, b) => b.playerCount - a.playerCount)
        .slice(0, 3)
        .map((item) => ({ label: item.raidName, value: formatInteger(item.playerCount) })),
    })
  }

  const playtime = parseTimeSpan(report.totalPlaytime)
  if (playtime && playtime > 0) {
    const topCharacter = rankCharacterPlaytime(report.characterPlaytime)[0]
    const streak = summarizeStreak(report.longestPlaytimeStreak)
    slides.push({
      key: 'time',
      layout: 'class-breakdown',
      eyebrow: 'Total character playtime',
      title: 'Time played across your characters.',
      value: formatHours(playtime),
      body: 'Summed from the playtime reported for every current and deleted character on this account.',
      detail:
        topCharacter && streak && streak.days > 1
          ? `${topCharacter.label} leads with ${formatHours(topCharacter.seconds)} · Longest streak: ${formatInteger(streak.days)} days`
          : topCharacter
            ? `${topCharacter.label} leads with ${formatHours(topCharacter.seconds)}.`
            : undefined,
      tone: 'arc',
      stats: classBreakdown(report, assets),
    })
  }

  const topWeapons = mostUsedActualWeapons(weapons)
  const weapon = topWeapons[0]
  if (weapon) {
    slides.push({
      key: 'weapon',
      layout: 'weapon-leaderboard',
      eyebrow: 'Your PvE arsenal',
      title: 'The five weapons that did the most work.',
      value: `${weapon.name} took the top spot`,
      body: 'Ranked by recorded PvE kills across every character.',
      tone: 'solar',
      items: topWeapons.map((item) => ({
        label: item.name,
        value: formatInteger(item.kills),
        imageUrl: item.iconUrl,
      })),
    })
  }

  const teammate = [...report.mostPlayedWith].sort((a, b) => b.encounterCount - a.encounterCount)[0]
  if (teammate?.player.displayName || report.uniquePlayersPlayedWith > 0) {
    slides.push({
      key: 'people',
      layout: 'teammate-profile',
      eyebrow: 'Most frequent teammate',
      title: teammate?.player.displayName
        ? `${teammate.player.displayName} was your most frequent teammate.`
        : 'Your activity history is full of other Guardians.',
      value: teammate
        ? `${formatInteger(teammate.encounterCount)} activities together`
        : `${formatInteger(report.uniquePlayersPlayedWith)} Guardians met`,
      body:
        report.uniquePlayersPlayedWith > 0
          ? `${formatInteger(report.uniquePlayersPlayedWith)} unique players appear across your recorded activity history.`
          : 'Measured from the teammates present in your recorded activities.',
      tone: 'arc',
      imageUrl: teammate?.player.emblemUrl,
      imageAlt: teammate?.player.emblemUrl ? `${teammate.player.displayName}'s emblem` : undefined,
    })
  }

  const bestPlaylist = [...report.pvpPlaylists]
    .filter(
      (playlist) =>
        playlist.matches >= MIN_PLAYLIST_SAMPLE &&
        playlist.mode > 0 &&
        !/^(none|reserved|mode \d+|private matches)/i.test(
          humanizeModeName(playlist.modeName).trim(),
        ),
    )
    .sort((a, b) => b.winRate - a.winRate)[0]
  if (bestPlaylist && bestPlaylist.winRate > 0.5) {
    const playlistName = humanizeModeName(bestPlaylist.modeName)
    slides.push({
      key: 'competitive',
      layout: 'match-scoreboard',
      eyebrow: 'Best qualifying Crucible playlist',
      title: `${playlistName} was your strongest winning playlist.`,
      value: `${formatInteger(bestPlaylist.wins)} wins in ${formatInteger(bestPlaylist.matches)} matches`,
      body: `${formatPercent(bestPlaylist.winRate)} win rate in ${playlistName}.`,
      detail: `Only named Crucible playlists with at least ${formatInteger(MIN_PLAYLIST_SAMPLE)} recorded matches are considered.`,
      tone: 'solar',
      iconUrl: assets?.crucibleIconUrl,
      stats: [
        { label: 'Wins', value: formatInteger(bestPlaylist.wins) },
        { label: 'Losses', value: formatInteger(bestPlaylist.losses) },
        { label: 'Win rate', value: formatPercent(bestPlaylist.winRate) },
      ],
    })
  }

  if (emblem && emblem.seconds > 0) {
    slides.push({
      key: 'emblem',
      layout: 'emblem-banner',
      eyebrow: 'Most-used emblem',
      title: 'Your most-used emblem.',
      value: emblem.name,
      body: `${formatHours(emblem.seconds)} of character playtime, more than any other emblem in your report.`,
      tone: 'neutral',
      imageUrl: emblem.backgroundUrl || emblem.iconUrl,
      imageAlt: `${emblem.name} emblem`,
    })
  }

  const personalityCandidates = [
    {
      count: report.goodBoyProtocol,
      eyebrow: 'Good Boy Protocol',
      value: formatInteger(report.goodBoyProtocol),
      title: 'Good Boy Protocol interactions.',
      body: 'Recorded interactions with the best boy in the Tower.',
      iconUrl: assets?.goodBoyProtocolIconUrl,
    },
    {
      count: report.fishCaught,
      eyebrow: 'Fish caught',
      value: formatInteger(report.fishCaught),
      title: 'Time spent fishing.',
      body: 'Fish caught between universe-ending emergencies.',
    },
    {
      count: report.misadventures,
      eyebrow: 'Misadventures',
      value: formatInteger(report.misadventures),
      title: 'The Architects remember you too.',
      body: 'Deaths with nobody else to blame.',
    },
  ].filter((candidate) => candidate.count > 0)
  const personality =
    personalityCandidates.length > 0
      ? personalityCandidates[
          stableHash(`${report.platformId}:${report.playerMembershipId}`) %
            personalityCandidates.length
        ]
      : undefined
  if (personality) {
    slides.push({
      key: 'personality',
      layout: 'personality-number',
      eyebrow: personality.eyebrow,
      value: personality.value,
      title: personality.title,
      body: personality.body,
      tone: 'gold',
      iconUrl: personality.iconUrl,
    })
  }

  return slides
}

export function buildStory(report: DestinyReport): StoryChapter[] {
  const chapters = [
    timeChapter(report),
    fightChapter(report),
    challengeChapter(report),
    competitiveChapter(report),
    peopleChapter(report),
    detailsChapter(report),
  ].filter((chapter): chapter is StoryChapter => chapter !== null && chapter.facts.length > 0)

  return chapters.map((chapter, index) => ({
    ...chapter,
    kicker: `Chapter ${index + 1}`,
  }))
}

function timeChapter(report: DestinyReport): StoryChapter | null {
  const facts: StoryFact[] = []

  const playtime = parseTimeSpan(report.totalPlaytime)
  if (playtime && playtime > 0) {
    facts.push({
      label: 'Total playtime',
      value: formatHours(playtime),
      note: 'Total character playtime across your account.',
    })
  }

  const topCharacter = rankCharacterPlaytime(report.characterPlaytime)[0]
  if (topCharacter) {
    facts.push({
      label: 'Most-played character',
      value: topCharacter.label,
      note: `${formatHours(topCharacter.seconds)} of playtime, more than any other character.`,
    })
  }

  const streak = summarizeStreak(report.longestPlaytimeStreak)
  if (streak && streak.days > 1) {
    facts.push({
      label: 'Longest streak',
      value: `${formatInteger(streak.days)} days in a row`,
    })
  }

  const topPatrol = rankPatrolTime(report.patrolTimeByPlanet)[0]
  if (topPatrol) {
    facts.push({
      label: 'Most-patrolled destination',
      value: topPatrol.label,
      note: `${formatHours(topPatrol.seconds)} on patrol at your most-visited destination.`,
    })
  }

  return {
    key: 'time',
    kicker: '',
    title: 'Time played',
    lead: 'Your whole Destiny 2 history, across every character and destination.',
    facts,
  }
}

function fightChapter(report: DestinyReport): StoryChapter | null {
  const facts: StoryFact[] = []

  if (report.totalKills > 0) {
    facts.push({
      label: 'Total kills',
      value: formatInteger(report.totalKills),
      note: 'Every combatant and Guardian, in every activity.',
    })
  }

  if (report.crucibleKills.total > 0) {
    const topMode = Object.entries(report.crucibleKills.byMode).sort((a, b) => b[1] - a[1])[0]
    facts.push({
      label: 'Crucible kills',
      value: formatInteger(report.crucibleKills.total),
      note: topMode ? `Most of them in ${humanizeModeName(topMode[0])}.` : undefined,
    })
  }

  return {
    key: 'fight',
    kicker: '',
    title: 'Combat',
    lead: 'The scale of the fight, in numbers only your Ghost was counting.',
    facts,
  }
}

interface FastestClear {
  activityName: string
  seconds: number
}

/** Fastest clear across raids and dungeons (conquests excluded: different pacing). */
export function fastestEndgameClear(report: DestinyReport): FastestClear | null {
  let best: FastestClear | null = null
  for (const summary of [...report.raidCompletions, ...report.dungeonCompletions]) {
    const seconds = parseTimeSpan(summary.fastestCompletion?.duration)
    if (!seconds || seconds <= 0) continue
    if (!best || seconds < best.seconds) {
      best = { activityName: summary.activityName, seconds }
    }
  }
  return best
}

function countDistinction(
  summaries: ActivityCompletionSummary[],
  predicate: (s: ActivityCompletionSummary) => boolean,
): string[] {
  return summaries.filter((s) => s.completionCount > 0 && predicate(s)).map((s) => s.activityName)
}

function challengeChapter(report: DestinyReport): StoryChapter | null {
  const facts: StoryFact[] = []
  const raids = report.raidCompletions

  const raidClears = raids.reduce((sum, raid) => sum + raid.completionCount, 0)
  if (raidClears > 0) {
    const topRaid = [...raids].sort((a, b) => b.completionCount - a.completionCount)[0]
    facts.push({
      label: 'Raid clears',
      value: formatInteger(raidClears),
      note:
        topRaid && topRaid.completionCount > 0
          ? `${topRaid.activityName} led the way with ${formatInteger(topRaid.completionCount)} clears.`
          : undefined,
    })
  }

  const dungeonClears = report.dungeonCompletions.reduce((sum, d) => sum + d.completionCount, 0)
  if (dungeonClears > 0) {
    facts.push({ label: 'Dungeon clears', value: formatInteger(dungeonClears) })
  }

  // Earned distinctions outrank generic counts; solo flawless is rarest.
  const soloFlawless = countDistinction(
    [...raids, ...report.dungeonCompletions],
    (s) => s.soloFlawlessClear,
  )
  if (soloFlawless.length > 0) {
    facts.push({
      label: 'Solo flawless',
      value: soloFlawless.join(', '),
      note: 'Alone, without dying once.',
    })
  }

  const contest = countDistinction(raids, (s) => s.contestClear)
  if (contest.length > 0) {
    facts.push({
      label: 'Contest-mode clears',
      value: contest.join(', '),
      note: 'Cleared while contest difficulty was live.',
    })
  }

  const fastest = fastestEndgameClear(report)
  if (fastest) {
    facts.push({
      label: 'Fastest clear',
      value: formatClock(fastest.seconds),
      note: `Your quickest ${fastest.activityName} completion.`,
    })
  }

  return {
    key: 'challenge',
    kicker: '',
    title: 'Endgame completions',
    lead: 'Raid and dungeon clears, including notable completions.',
    facts,
  }
}

function competitiveChapter(report: DestinyReport): StoryChapter | null {
  const facts: StoryFact[] = []

  if (report.crucibleMatchesPlayed > 0) {
    facts.push({
      label: 'Crucible',
      value: `${formatRatio(report.crucibleKd)} KD`,
      note: `Across ${formatInteger(report.crucibleMatchesPlayed)} matches, ${formatInteger(report.crucibleWins)} of them wins.`,
    })
  }

  if (report.gambitMatchesPlayed > 0) {
    facts.push({
      label: 'Gambit',
      value: `${formatRatio(report.gambitKd)} KD`,
      note: `Across ${formatInteger(report.gambitMatchesPlayed)} matches, ${formatInteger(report.gambitWins)} of them wins.`,
    })
  }

  // "Best playlist" only with a meaningful sample.
  const bestPlaylist = [...report.pvpPlaylists]
    .filter((playlist) => playlist.matches >= MIN_PLAYLIST_SAMPLE)
    .sort((a, b) => b.winRate - a.winRate)[0]
  if (bestPlaylist && bestPlaylist.winRate > 0.5) {
    facts.push({
      label: 'Strongest playlist',
      value: humanizeModeName(bestPlaylist.modeName),
      note: `${formatPercent(bestPlaylist.winRate)} win rate over ${formatInteger(bestPlaylist.matches)} matches.`,
    })
  }

  return {
    key: 'competitive',
    kicker: '',
    title: 'Competitive record',
    lead: 'Ratios straight from match history, sample sizes included.',
    facts,
  }
}

function peopleChapter(report: DestinyReport): StoryChapter | null {
  const facts: StoryFact[] = []

  if (report.uniquePlayersPlayedWith > 0) {
    facts.push({
      label: 'Guardians met',
      value: formatInteger(report.uniquePlayersPlayedWith),
      note: 'Unique players who shared at least one activity with you.',
    })
  }

  const topTeammate = [...report.mostPlayedWith].sort(
    (a, b) => b.encounterCount - a.encounterCount,
  )[0]
  if (topTeammate && topTeammate.player.displayName) {
    facts.push({
      label: 'Most frequent teammate',
      value: topTeammate.player.displayName,
      note: `${formatInteger(topTeammate.encounterCount)} activities together, more than anyone else.`,
    })
  }

  const sherpaed = report.playersSherpaed.reduce((sum, sherpa) => sum + sherpa.playerCount, 0)
  if (sherpaed > 0) {
    facts.push({
      label: 'Raiders sherpaed',
      value: formatInteger(sherpaed),
      note: 'First-time raiders you guided through a clear.',
    })
  }

  return {
    key: 'people',
    kicker: '',
    title: 'Teammates',
    lead: 'Players who appear most often in your activity history.',
    facts,
  }
}

function detailsChapter(report: DestinyReport): StoryChapter | null {
  const facts: StoryFact[] = []

  const favoriteEmblem = report.mostUsedEmblems
    .map((emblem) => ({ ...emblem, seconds: parseTimeSpan(emblem.totalPlaytime) ?? 0 }))
    .sort((a, b) => b.seconds - a.seconds)[0]
  if (favoriteEmblem && favoriteEmblem.seconds > 0) {
    facts.push({
      label: 'Favorite emblem',
      value: favoriteEmblem.name,
      note: `Worn for ${formatHours(favoriteEmblem.seconds)}, longer than any other.`,
    })
  }

  if (report.triumphSeals.length > 0) {
    const first = report.triumphSeals[0]
    facts.push({
      label: 'Seals completed',
      value: formatInteger(report.triumphSeals.length),
      note: first ? `Including ${first.name}.` : undefined,
    })
  }

  if (report.goodBoyProtocol > 0) {
    facts.push({
      label: 'Good Boy Protocol',
      value: formatInteger(report.goodBoyProtocol),
      note: 'Visits to the Tower’s very good dog.',
    })
  }

  if (report.fishCaught > 0) {
    facts.push({ label: 'Fish caught', value: formatInteger(report.fishCaught) })
  }

  if (report.misadventures > 0) {
    facts.push({
      label: 'Misadventures',
      value: formatInteger(report.misadventures),
      note: 'Deaths with nobody to blame. The architects thank you.',
    })
  }

  return {
    key: 'details',
    kicker: '',
    title: 'Other stats',
    lead: 'Emblems, titles, and miscellaneous activity totals.',
    facts,
  }
}
