/**
 * Pure flattening/filtering logic for the layered weapon endpoint:
 * class → specific activity mode → category → weapons.
 *
 * The UI filters by class and specific mode, then presents categories as
 * ranked bars and weapons as a ranked table. Aggregation merges duplicate
 * categories/weapons across the selected slices by key, summing kills.
 */

import type {
  ActivityModeParam,
  WeaponActivityModeAggregateReport,
  WeaponCategoryAggregate,
} from '@/lib/api/types'

export const ALL = 'All'

export interface WeaponFilters {
  className: string // ALL or a class name present in the data
  specificMode: string // ALL or a mode name present in the selected class(es)
}

export interface CategoryTotal {
  key: string
  name: string
  kills: number
}

export interface WeaponRow {
  key: string
  name: string
  iconUrl: string
  categoryName: string
  kills: number
}

export interface FlattenedWeapons {
  categories: CategoryTotal[]
  weapons: WeaponRow[]
  totalKills: number
}

export function availableClasses(report: WeaponActivityModeAggregateReport): string[] {
  return report.classes.map((c) => c.className)
}

export function availableModes(
  report: WeaponActivityModeAggregateReport,
  className: string,
): string[] {
  const names = new Set<string>()
  for (const cls of report.classes) {
    if (className !== ALL && cls.className !== className) continue
    for (const mode of cls.modes) names.add(mode.specificActivityMode)
  }
  return [...names].sort((a, b) => a.localeCompare(b))
}

function* selectedCategories(
  report: WeaponActivityModeAggregateReport,
  filters: WeaponFilters,
): Generator<WeaponCategoryAggregate> {
  for (const cls of report.classes) {
    if (filters.className !== ALL && cls.className !== filters.className) continue
    for (const mode of cls.modes) {
      if (filters.specificMode !== ALL && mode.specificActivityMode !== filters.specificMode) {
        continue
      }
      yield* mode.categories
    }
  }
}

export function flattenWeapons(
  report: WeaponActivityModeAggregateReport,
  filters: WeaponFilters,
): FlattenedWeapons {
  const categoryTotals = new Map<string, CategoryTotal>()
  const weaponTotals = new Map<string, WeaponRow>()
  let totalKills = 0

  for (const category of selectedCategories(report, filters)) {
    totalKills += category.kills

    const existingCategory = categoryTotals.get(category.categoryKey)
    if (existingCategory) {
      existingCategory.kills += category.kills
    } else {
      categoryTotals.set(category.categoryKey, {
        key: category.categoryKey,
        name: category.categoryName,
        kills: category.kills,
      })
    }

    for (const weapon of category.weapons) {
      const existingWeapon = weaponTotals.get(weapon.weaponKey)
      if (existingWeapon) {
        existingWeapon.kills += weapon.kills
      } else {
        weaponTotals.set(weapon.weaponKey, {
          key: weapon.weaponKey,
          name: weapon.weaponName,
          iconUrl: weapon.iconUrl,
          categoryName: weapon.categoryName,
          kills: weapon.kills,
        })
      }
    }
  }

  return {
    categories: [...categoryTotals.values()].sort((a, b) => b.kills - a.kills),
    weapons: [...weaponTotals.values()].sort((a, b) => b.kills - a.kills),
    totalKills,
  }
}

export interface CategoryShare {
  key: string
  label: string
  value: number
}

/**
 * Every positive category as its own part of the whole. The combat view uses
 * the ranked bars as the legend, so the long tail can remain honest without a
 * large second legend or an opaque "Other" bucket.
 */
export function categoryShares(categories: CategoryTotal[]): CategoryShare[] {
  return categories
    .filter((category) => category.kills > 0)
    .map((category) => ({ key: category.key, label: category.name, value: category.kills }))
}

/** Stable per-bucket colors shared by activity-level charts. */
export const MODE_COLOR: Record<ActivityModeParam, string> = {
  PvE: 'var(--color-mode-pve)',
  PvP: 'var(--color-mode-pvp)',
  Gambit: 'var(--color-mode-gambit)',
}

/** Permanent colors for the 19 category keys currently emitted by the API. */
export const CATEGORY_COLOR: Readonly<Record<string, string>> = {
  ABILITIES: '#b36bd4',
  'AUTO RIFLE': '#4f98d2',
  'COMBAT BOW': '#42b9b1',
  'FUSION RIFLE': '#9b72cf',
  GLAIVE: '#d56f9e',
  'GRENADE LAUNCHER': '#dd7f42',
  'HAND CANNON': '#d55353',
  'LINEAR FUSION RIFLE': '#8276d1',
  'MACHINE GUN': '#8f9edb',
  'PULSE RIFLE': '#e7b93e',
  'ROCKET LAUNCHER': '#e16f55',
  'SCOUT RIFLE': '#63a66f',
  SHOTGUN: '#c69445',
  SIDEARM: '#50b7a7',
  'SNIPER RIFLE': '#5f83cc',
  'SUBMACHINE GUN': '#a8b957',
  SWORD: '#d66892',
  'TRACE RIFLE': '#62a5dc',
  UNKNOWN: '#8d7f75',
}

/**
 * Keep future API categories stable as well. Known categories use the curated
 * palette above; an unfamiliar key receives a deterministic HSL fallback.
 */
export function categoryColor(categoryKey: string): string {
  const knownColor = CATEGORY_COLOR[categoryKey]
  if (knownColor) return knownColor

  let hash = 0
  for (const character of categoryKey) {
    hash = Math.imul(hash, 31) + character.charCodeAt(0)
  }
  const hue = ((hash % 360) + 360) % 360
  return `hsl(${hue} 58% 60%)`
}
