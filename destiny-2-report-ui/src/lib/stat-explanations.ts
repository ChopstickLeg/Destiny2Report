export const MISSING_ACTIVITY_MODE_EXPLANATION =
  "Some activities were returned by Bungie's API without a mode due to an API bug, so they appear here as None."

export const UNKNOWN_KILLS_EXPLANATION =
  "Kills that weren't credited to a weapon or ability, such as relic kills or enemies falling off the map."

export function isMissingActivityMode(label: string): boolean {
  return label.trim().toLowerCase() === 'none'
}

export function isUnknownKillCategory(label: string): boolean {
  return label.trim().toLowerCase() === 'unknown'
}
