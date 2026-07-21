/**
 * Bungie membership platform mapping.
 * https://bungie-net.github.io/multi/schema_BungieMembershipType.html
 */

const PLATFORM_LABELS: Record<number, string> = {
  1: 'Xbox',
  2: 'PlayStation',
  3: 'Steam',
  4: 'Battle.net',
  5: 'Stadia',
  6: 'Epic Games',
  254: 'Bungie.net',
}

export function platformLabel(membershipType: number): string {
  return PLATFORM_LABELS[membershipType] ?? `Platform ${membershipType}`
}
