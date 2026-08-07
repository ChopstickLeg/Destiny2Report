import { beforeEach, describe, expect, it } from 'vitest'
import { createPinia, setActivePinia } from 'pinia'
import type { DestinyMembership, SignedInPlayerResponse } from '@/lib/api/types'
import { useSessionStore } from '../session'

function membership(type: number, id: string, displayName: string): DestinyMembership {
  return {
    membershipType: type,
    membershipId: id,
    displayName,
    bungieGlobalDisplayName: 'Guardian',
    bungieGlobalDisplayNameCode: 1234,
    iconPath: null,
    crossSaveOverride: 0,
    applicableMembershipTypes: [type],
    isPublic: true,
  }
}

function profile(
  memberships: DestinyMembership[],
  primaryDestinyMembership: DestinyMembership | null = null,
): SignedInPlayerResponse {
  return {
    signedIn: true,
    bungieNetUser: null,
    destinyMemberships: memberships,
    primaryDestinyMembership,
    isAdmin: false,
  }
}

describe('session membership selection', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
    localStorage.clear()
  })

  it('blocks the Story prompt until a non-Cross Save membership is selected', () => {
    const playstation = membership(2, '101', 'Old profile')
    const steam = membership(3, '202', 'Current profile')
    const session = useSessionStore()
    session.profile = profile([playstation, steam])
    session.status = 'signed-in'

    session.showStoryPrompt()

    expect(session.needsMembershipSelection).toBe(true)
    expect(session.activeMembership).toBeNull()
    expect(session.storyPromptOpen).toBe(false)
    expect(session.storyPromptPending).toBe(true)

    session.selectMembership(steam)

    expect(session.activeMembership).toEqual(steam)
    expect(session.storyPromptPending).toBe(false)
    expect(session.storyPromptOpen).toBe(true)
  })

  it('uses Bungie’s Cross Save primary without asking for a selection', () => {
    const playstation = membership(2, '101', 'Overridden profile')
    const steam = membership(3, '202', 'Cross Save primary')
    const session = useSessionStore()
    session.profile = profile([playstation, steam], steam)
    session.status = 'signed-in'

    session.showStoryPrompt()

    expect(session.needsMembershipSelection).toBe(false)
    expect(session.activeMembership).toEqual(steam)
    expect(session.storyPromptOpen).toBe(true)
  })

  it('restores only a selection that still belongs to the signed-in account', () => {
    const playstation = membership(2, '101', 'Old profile')
    const steam = membership(3, '202', 'Current profile')
    localStorage.setItem('d2r.active-membership', '3:202')
    const session = useSessionStore()
    session.profile = profile([playstation, steam])

    session.restoreMembershipSelection()

    expect(session.activeMembership).toEqual(steam)

    session.profile = profile([playstation, membership(6, '303', 'Epic profile')])
    session.restoreMembershipSelection()

    expect(session.activeMembership).toBeNull()
  })
})
