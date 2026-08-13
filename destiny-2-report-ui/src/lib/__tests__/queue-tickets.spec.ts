import { beforeEach, describe, expect, it } from 'vitest'
import { forgetQueueTicket, loadQueueTicket, rememberQueueTicket } from '../queue-tickets'

const identity = { membershipTypeId: 3, membershipId: '4611686018463095984' }

describe('queue tickets', () => {
  beforeEach(() => sessionStorage.clear())

  it('stores tickets only for the current browser session and membership', () => {
    rememberQueueTicket(identity, 'signed-ticket')

    expect(loadQueueTicket(identity)).toBe('signed-ticket')
    expect(loadQueueTicket({ membershipTypeId: 2, membershipId: identity.membershipId })).toBeNull()
  })

  it('forgets a ticket after successful queue admission', () => {
    rememberQueueTicket(identity, 'signed-ticket')

    forgetQueueTicket(identity)

    expect(loadQueueTicket(identity)).toBeNull()
  })
})
