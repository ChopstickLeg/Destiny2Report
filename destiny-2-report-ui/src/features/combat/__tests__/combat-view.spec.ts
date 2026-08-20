import { describe, expect, it } from 'vitest'
import type { WeaponActivityModeAggregateReport } from '@/lib/api/types'
import {
  ALL,
  CATEGORY_COLOR,
  availableClasses,
  availableModes,
  categoryColor,
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
          specificActivityModeId: 73,
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
          specificActivityModeId: 19,
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
  it('keeps every long-tail category visible', () => {
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
    expect(shares).toHaveLength(7)
    expect(shares[5]).toEqual({ key: 'f', label: 'F', value: 5 })
    expect(shares[6]).toEqual({ key: 'g', label: 'G', value: 5 })
  })

  it('drops zero-kill categories', () => {
    const shares = categoryShares([
      { key: 'a', name: 'A', kills: 10 },
      { key: 'b', name: 'B', kills: 0 },
    ])
    expect(shares).toHaveLength(1)
  })
})

describe('categoryColor', () => {
  it('assigns a unique permanent color to every current API category', () => {
    expect(Object.keys(CATEGORY_COLOR).sort()).toEqual([
      'ABILITIES',
      'AUTO RIFLE',
      'COMBAT BOW',
      'FUSION RIFLE',
      'GLAIVE',
      'GRENADE LAUNCHER',
      'HAND CANNON',
      'LINEAR FUSION RIFLE',
      'MACHINE GUN',
      'PULSE RIFLE',
      'ROCKET LAUNCHER',
      'SCOUT RIFLE',
      'SHOTGUN',
      'SIDEARM',
      'SNIPER RIFLE',
      'SUBMACHINE GUN',
      'SWORD',
      'TRACE RIFLE',
      'UNKNOWN',
    ])
    const colors = Object.values(CATEGORY_COLOR)
    expect(colors).toHaveLength(19)
    expect(new Set(colors)).toHaveLength(19)
    expect(categoryColor('HAND CANNON')).toBe(CATEGORY_COLOR['HAND CANNON'])
    expect(categoryColor('HAND CANNON')).not.toBe(categoryColor('AUTO RIFLE'))
  })

  it('gives future category keys a deterministic fallback', () => {
    expect(categoryColor('FUTURE WEAPON')).toBe(categoryColor('FUTURE WEAPON'))
    expect(categoryColor('FUTURE WEAPON')).toMatch(/^hsl\(\d+ 58% 60%\)$/)
  })
})
