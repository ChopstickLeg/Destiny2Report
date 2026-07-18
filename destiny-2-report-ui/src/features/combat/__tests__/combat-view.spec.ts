import { describe, expect, it } from 'vitest'
import type { WeaponActivityModeAggregateReport } from '@/lib/api/types'
import {
  ALL,
  availableClasses,
  availableModes,
  categoryShares,
  flattenWeapons,
} from '../combat-view'

const report: WeaponActivityModeAggregateReport = {
  activityMode: 'Crucible',
  classes: [
    {
      className: 'Titan',
      modes: [
        {
          specificActivityMode: 'Control',
          categories: [
            {
              categoryKey: 'HAND_CANNON',
              categoryName: 'Hand Cannon',
              kills: 100,
              weapons: [
                {
                  weaponKey: 'fatebringer',
                  weaponName: 'Fatebringer',
                  referenceId: 1,
                  iconUrl: '',
                  categoryKey: 'HAND_CANNON',
                  categoryName: 'Hand Cannon',
                  kills: 100,
                },
              ],
            },
            {
              categoryKey: 'ABILITIES',
              categoryName: 'Abilities',
              kills: 40,
              weapons: [
                {
                  weaponKey: 'grenade',
                  weaponName: 'Grenade',
                  referenceId: -1,
                  iconUrl: '',
                  categoryKey: 'ABILITIES',
                  categoryName: 'Abilities',
                  kills: 40,
                },
              ],
            },
          ],
        },
      ],
    },
    {
      className: 'Hunter',
      modes: [
        {
          specificActivityMode: 'IronBanner',
          categories: [
            {
              categoryKey: 'HAND_CANNON',
              categoryName: 'Hand Cannon',
              kills: 60,
              weapons: [
                {
                  weaponKey: 'fatebringer',
                  weaponName: 'Fatebringer',
                  referenceId: 1,
                  iconUrl: '',
                  categoryKey: 'HAND_CANNON',
                  categoryName: 'Hand Cannon',
                  kills: 60,
                },
              ],
            },
          ],
        },
      ],
    },
  ],
}

describe('flattenWeapons', () => {
  it('merges categories and weapons across classes and modes', () => {
    const flat = flattenWeapons(report, { className: ALL, specificMode: ALL })
    expect(flat.totalKills).toBe(200)
    expect(flat.categories).toEqual([
      { key: 'HAND_CANNON', name: 'Hand Cannon', kills: 160 },
      { key: 'ABILITIES', name: 'Abilities', kills: 40 },
    ])
    const fatebringer = flat.weapons.find((weapon) => weapon.key === 'fatebringer')
    expect(fatebringer?.kills).toBe(160)
  })

  it('filters by class', () => {
    const flat = flattenWeapons(report, { className: 'Hunter', specificMode: ALL })
    expect(flat.totalKills).toBe(60)
    expect(flat.weapons).toHaveLength(1)
  })

  it('filters by specific mode', () => {
    const flat = flattenWeapons(report, { className: ALL, specificMode: 'Control' })
    expect(flat.totalKills).toBe(140)
  })

  it('sorts results by kills descending', () => {
    const flat = flattenWeapons(report, { className: ALL, specificMode: ALL })
    expect(flat.categories[0]?.kills).toBeGreaterThanOrEqual(flat.categories[1]?.kills ?? 0)
  })
})

describe('availableClasses / availableModes', () => {
  it('lists only what the data contains', () => {
    expect(availableClasses(report)).toEqual(['Titan', 'Hunter'])
    expect(availableModes(report, 'Hunter')).toEqual(['IronBanner'])
    expect(availableModes(report, ALL)).toEqual(['Control', 'IronBanner'])
  })
})

describe('categoryShares', () => {
  it('groups the long tail into Other', () => {
    const categories = [
      { key: 'a', name: 'A', kills: 50 },
      { key: 'b', name: 'B', kills: 40 },
      { key: 'c', name: 'C', kills: 30 },
      { key: 'd', name: 'D', kills: 20 },
      { key: 'e', name: 'E', kills: 10 },
      { key: 'f', name: 'F', kills: 5 },
      { key: 'g', name: 'G', kills: 5 },
    ]
    const shares = categoryShares(categories)
    expect(shares).toHaveLength(6)
    expect(shares[5]).toEqual({ label: 'Other', value: 10 })
  })

  it('drops zero-kill categories', () => {
    const shares = categoryShares([
      { key: 'a', name: 'A', kills: 10 },
      { key: 'b', name: 'B', kills: 0 },
    ])
    expect(shares).toHaveLength(1)
  })
})
