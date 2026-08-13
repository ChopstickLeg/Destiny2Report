import type { ReportIdentity } from './api/reports'

const STORAGE_PREFIX = 'd2r.queue-ticket:'
const USED_STORAGE_PREFIX = 'd2r.used-queue-ticket:'
const usedTickets = new Set<string>()

export function rememberQueueTicket(identity: ReportIdentity, ticket: string): void {
  if (!ticket) return
  try {
    if (usedTickets.has(ticket) || sessionStorage.getItem(usedTicketKey(identity)) === ticket) return
    sessionStorage.setItem(ticketKey(identity), ticket)
    sessionStorage.removeItem(usedTicketKey(identity))
  } catch {
    // A ticket can be recovered by repeating the player search or sign-in lookup.
  }
}

export function takeQueueTicket(identity: ReportIdentity): string | null {
  let ticket: string | null
  try {
    ticket = sessionStorage.getItem(ticketKey(identity))
    if (!ticket) return null

    sessionStorage.removeItem(ticketKey(identity))
  } catch {
    return null
  }

  usedTickets.add(ticket)
  try {
    sessionStorage.setItem(usedTicketKey(identity), ticket)
  } catch {
    // The in-memory marker still prevents this tab from restoring a cached ticket.
  }
  return ticket
}

function ticketKey(identity: ReportIdentity): string {
  return `${STORAGE_PREFIX}${identity.membershipTypeId}:${identity.membershipId}`
}

function usedTicketKey(identity: ReportIdentity): string {
  return `${USED_STORAGE_PREFIX}${identity.membershipTypeId}:${identity.membershipId}`
}
