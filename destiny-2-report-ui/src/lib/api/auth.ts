import { apiFetch } from './http'
import type { SignedInPlayerResponse } from './types'

export function exchangeOAuthCode(
  code: string,
  redirectUri: string,
): Promise<SignedInPlayerResponse> {
  return apiFetch('/auth/bungie/oauth', {
    method: 'POST',
    body: { code, redirectUri },
  })
}

/** Without a session cookie this resolves `{ signedIn: false }` rather than a 401. */
export function fetchWhoAmI(signal?: AbortSignal): Promise<SignedInPlayerResponse> {
  return apiFetch('/auth/whoami', { signal })
}

export function signOut(): Promise<void> {
  return apiFetch('/auth/signout', { method: 'POST' })
}
