import type { LeaderboardDefinition } from '@/lib/api/types'

export interface LeaderboardCollection {
  key: string
  title: string
  description: string
  boards: LeaderboardDefinition[]
  choices: LeaderboardChoice[]
}

export type LeaderboardMetricKind = 'time' | 'kills' | 'wins' | 'score'

export interface LeaderboardVariant {
  kind: LeaderboardMetricKind
  label: string
  board: LeaderboardDefinition
}

export interface LeaderboardChoice {
  key: string
  title: string
  variants: LeaderboardVariant[]
}

const collectionDetails = [
  {
    key: 'time-invested',
    title: 'Time invested',
    description: 'Classes, activities, and the hours that shaped a Guardian.',
  },
  {
    key: 'destinations',
    title: 'Destination devotees',
    description: 'The Guardians who never really left patrol.',
  },
  {
    key: 'combat',
    title: 'Combat records',
    description: 'Career kills across modes and Guardian classes.',
  },
  {
    key: 'arsenal',
    title: 'Arsenal specialists',
    description: 'Weapon families, damage types, and Exotic dedication.',
  },
  {
    key: 'competitive',
    title: 'Competitive records',
    description: 'Crucible and Gambit wins, from broad records to favorite modes.',
  },
  {
    key: 'pve-endgame',
    title: 'PvE endgame',
    description: 'Raids, dungeons, Nightfalls, Lost Sectors, and Nightmare Hunts.',
  },
  {
    key: 'pve-activities',
    title: 'Everyday PvE',
    description: 'Stories, strikes, patrol, adventures, and Dares of Eternity.',
  },
  {
    key: 'legacy-activities',
    title: 'Seasonal & legacy PvE',
    description: 'Menagerie, Reckoning, Sundial, and activities from seasons past.',
  },
  {
    key: 'gambit-modes',
    title: 'Gambit modes',
    description: 'Playtime, kills, and wins in the Drifter’s favorite modes.',
  },
  {
    key: 'crucible-core',
    title: 'Core Crucible',
    description: 'Control, Clash, Rumble, Supremacy, Rift, and related playlists.',
  },
  {
    key: 'crucible-rotators',
    title: 'Crucible rotators',
    description: 'Mayhem, Scorched, Doubles, Momentum, Relic, and special modes.',
  },
  {
    key: 'crucible-competitive',
    title: 'Competitive Crucible',
    description: 'Survival, Countdown, Elimination, and competitive variants.',
  },
  {
    key: 'trials',
    title: 'Trials',
    description: 'Trials of Osiris, Trials of the Nine, and their variants.',
  },
  {
    key: 'iron-banner',
    title: 'Iron Banner',
    description: 'Every Iron Banner ruleset in one focused collection.',
  },
  {
    key: 'private-matches',
    title: 'Private matches',
    description: 'Records earned across private Crucible rulesets.',
  },
  {
    key: 'other-activities',
    title: 'Other activities',
    description: 'Activity modes that do not fit the usual Vanguard or Crucible buckets.',
  },
  {
    key: 'curiosities',
    title: 'Miscellaneous',
    description: 'Fish, motes, misadventures, streaks, and other unusual honors.',
  },
] as const

const modeCollections: ReadonlyArray<[string, ReadonlySet<number>]> = [
  ['pve-endgame', new Set([4, 16, 17, 46, 47, 79, 82, 87])],
  ['pve-activities', new Set([2, 3, 6, 18, 58, 85])],
  ['legacy-activities', new Set([66, 76, 77, 78, 83, 86, 93])],
  ['gambit-modes', new Set([63, 75])],
  ['iron-banner', new Set([19, 43, 44, 45, 68, 90, 91])],
  ['trials', new Set([39, 41, 42, 84])],
  ['private-matches', new Set([32, 51, 52, 53, 54, 55, 56, 57])],
  ['crucible-competitive', new Set([37, 38, 59, 65, 69, 72, 74, 80])],
  ['crucible-core', new Set([10, 12, 31, 48, 67, 70, 71, 73, 88, 89])],
  ['crucible-rotators', new Set([15, 25, 40, 49, 50, 60, 61, 62, 81, 92, 94])],
]

function specificModeId(key: string): number | null {
  const match = key.match(
    /^(?:time\.mode|combat\.kills\.mode)\.(\d+)$|^competition\.(?:crucible|gambit)\.playlist\.(\d+)$/,
  )
  if (!match) return null
  return Number(match[1] ?? match[2])
}

function metricKind(board: LeaderboardDefinition): LeaderboardMetricKind {
  if (board.key.startsWith('time.')) return 'time'
  if (board.key.startsWith('combat.')) return 'kills'
  if (board.key.startsWith('competition.')) return 'wins'
  return 'score'
}

const metricLabels: Record<LeaderboardMetricKind, string> = {
  time: 'Time spent',
  kills: 'Kills',
  wins: 'Wins',
  score: 'Ranking',
}

function choiceKey(board: LeaderboardDefinition): string {
  const modeId = specificModeId(board.key)
  return modeId === null ? board.key : `mode:${modeId}`
}

function choiceTitle(board: LeaderboardDefinition): string {
  return specificModeId(board.key) === null
    ? board.title
    : board.title.replace(/\s+(?:playtime|kills|wins)$/i, '')
}

function choiceDisplayOrder(choice: LeaderboardChoice): number {
  return Math.min(...choice.variants.map((variant) => variant.board.displayOrder))
}

function organizeChoices(boards: LeaderboardDefinition[]): LeaderboardChoice[] {
  const choices = new Map<string, LeaderboardChoice>()
  for (const board of boards) {
    const key = choiceKey(board)
    const choice = choices.get(key) ?? { key, title: choiceTitle(board), variants: [] }
    const kind = metricKind(board)
    choice.variants.push({ kind, label: metricLabels[kind], board })
    choices.set(key, choice)
  }

  const metricOrder: LeaderboardMetricKind[] = ['time', 'kills', 'wins', 'score']
  return [...choices.values()]
    .map((choice) => ({
      ...choice,
      variants: choice.variants.sort(
        (left, right) => metricOrder.indexOf(left.kind) - metricOrder.indexOf(right.kind),
      ),
    }))
    .sort(
      (left, right) =>
        choiceDisplayOrder(left) - choiceDisplayOrder(right) ||
        left.title.localeCompare(right.title),
    )
}

function collectionKey(board: LeaderboardDefinition): string {
  if (board.key.startsWith('time.patrol.') && board.key !== 'time.patrol.total') {
    return 'destinations'
  }
  if (
    board.key.startsWith('combat.weapon-type.') ||
    board.key.startsWith('combat.damage.') ||
    board.key === 'combat.exotic'
  ) {
    return 'arsenal'
  }
  if (board.key.startsWith('oddities.')) return 'curiosities'
  const modeId = specificModeId(board.key)
  if (modeId !== null) {
    return modeCollections.find(([, modeIds]) => modeIds.has(modeId))?.[0] ?? 'other-activities'
  }
  if (board.key.startsWith('competition.')) return 'competitive'
  if (board.key.startsWith('time.')) return 'time-invested'
  if (board.key.startsWith('combat.')) return 'combat'
  return 'curiosities'
}

export function organizeLeaderboards(boards: LeaderboardDefinition[]): LeaderboardCollection[] {
  const grouped = new Map<string, LeaderboardDefinition[]>()
  for (const board of boards) {
    const key = collectionKey(board)
    const group = grouped.get(key) ?? []
    group.push(board)
    grouped.set(key, group)
  }

  return collectionDetails
    .map((collection) => {
      const collectionBoards = (grouped.get(collection.key) ?? []).sort(
        (left, right) =>
          left.displayOrder - right.displayOrder || left.title.localeCompare(right.title),
      )
      return {
        ...collection,
        boards: collectionBoards,
        choices: organizeChoices(collectionBoards),
      }
    })
    .filter((collection) => collection.boards.length > 0)
}

export function findLeaderboardChoice(
  collections: LeaderboardCollection[],
  boardKey: string,
): LeaderboardChoice | undefined {
  return collections
    .flatMap((collection) => collection.choices)
    .find((choice) => choice.variants.some((variant) => variant.board.key === boardKey))
}

export function findLeaderboardCollection(
  collections: LeaderboardCollection[],
  boardKey: string,
): string {
  return (
    collections.find((collection) => collection.boards.some((board) => board.key === boardKey))
      ?.key ??
    collections[0]?.key ??
    ''
  )
}
