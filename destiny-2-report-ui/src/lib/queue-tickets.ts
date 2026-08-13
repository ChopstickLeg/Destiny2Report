import type { ReportIdentity } from './api/reports'

const STORAGE_PREFIX = 'd2r.queue-ticket:'

export function rememberQueueTicket(identity: ReportIdentity, ticket: string): void {
  if (!ticket) return
  try {
    sessionStorage.setItem(ticketKey(identity), ticket)
  } catch {
    // A ticket can be recovered by repeating the player search or sign-in lookup.
  }
}

export function loadQueueTicket(identity: ReportIdentity): string | null {
  try {
    return sessionStorage.getItem(ticketKey(identity))
  } catch {
    return null
  }
}

export function forgetQueueTicket(identity: ReportIdentity): void {
  try {
    sessionStorage.removeItem(ticketKey(identity))
  } catch {
    // Session storage is optional; an expired server-side ticket is harmless.
  }
}

function ticketKey(identity: ReportIdentity): string {
  return `${STORAGE_PREFIX}${identity.membershipTypeId}:${identity.membershipId}`
}
