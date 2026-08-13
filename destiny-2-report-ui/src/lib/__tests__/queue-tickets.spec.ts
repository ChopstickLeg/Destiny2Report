import { beforeEach, describe, expect, it } from 'vitest'
import { rememberQueueTicket, takeQueueTicket } from '../queue-tickets'

const identity = { membershipTypeId: 3, membershipId: '4611686018463095984' }

describe('queue tickets', () => {
  beforeEach(() => sessionStorage.clear())

  it('stores tickets only for the current browser session and membership', () => {
    rememberQueueTicket(identity, 'signed-ticket')

    expect(takeQueueTicket({ membershipTypeId: 2, membershipId: identity.membershipId })).toBeNull()
    expect(takeQueueTicket(identity)).toBe('signed-ticket')
  })

  it('takes a ticket once and does not restore the consumed ticket from cached search data', () => {
    rememberQueueTicket(identity, 'single-use-ticket')

    expect(takeQueueTicket(identity)).toBe('single-use-ticket')
    expect(takeQueueTicket(identity)).toBeNull()

    rememberQueueTicket(identity, 'single-use-ticket')
    expect(takeQueueTicket(identity)).toBeNull()

    rememberQueueTicket(identity, 'fresh-ticket')
    expect(takeQueueTicket(identity)).toBe('fresh-ticket')
  })
})
