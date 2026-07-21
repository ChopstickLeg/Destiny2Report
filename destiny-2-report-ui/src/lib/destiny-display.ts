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

const MODE_NAME_PATTERN = /(?<=[a-z])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])/g
const ACTIVITY_MODE_NAMES: Readonly<Record<string, string>> = {
  'Mode 93': 'Lawless Frontier',
  'Mode 94': 'Sparrow Racing League',
  LawlessFrontier: 'Lawless Frontier',
  SparrowRacingLeague: 'Sparrow Racing League',
}

export function canonicalPatrolDestination(name: string): string | null {
  return PATROL_DESTINATION_NAMES[name] ?? null
}

export function humanizeModeName(name: string): string {
  return ACTIVITY_MODE_NAMES[name] ?? name.replace(MODE_NAME_PATTERN, ' ')
}

export const patrolDestinationAliases = PATROL_DESTINATION_NAMES
export const activityModeAliases = ACTIVITY_MODE_NAMES
