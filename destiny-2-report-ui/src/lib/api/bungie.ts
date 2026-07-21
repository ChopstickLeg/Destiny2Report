/**
 * Bungie-hosted image resolution.
 *
 * Most URLs from this API are already absolute; the `/api/auth/whoami`
 * endpoint passes raw relative Bungie paths through. Everything funnels
 * through here so the base host lives in exactly one place.
 */

const BUNGIE_HOST = 'https://www.bungie.net'

export function bungieUrl(path: string | null | undefined): string | null {
  if (!path) return null
  if (path.startsWith('http://') || path.startsWith('https://')) return path
  return `${BUNGIE_HOST}${path.startsWith('/') ? '' : '/'}${path}`
}
