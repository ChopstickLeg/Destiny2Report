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

export interface DonutShare {
  label: string
  value: number
}

/**
 * Top five category shares plus "Other." These are true parts of one whole; the
 * only place a donut is justified.
 */
export function categoryShares(categories: CategoryTotal[], top = 5): DonutShare[] {
  const positive = categories.filter((category) => category.kills > 0)
  const head = positive.slice(0, top).map((c) => ({ label: c.name, value: c.kills }))
  const tail = positive.slice(top)
  if (tail.length > 0) {
    head.push({ label: 'Other', value: tail.reduce((sum, c) => sum + c.kills, 0) })
  }
  return head
}

/** Stable per-bucket colors (see tokens.css). */
export const MODE_COLOR: Record<ActivityModeParam, string> = {
  PvE: 'var(--color-mode-pve)',
  PvP: 'var(--color-mode-pvp)',
  Gambit: 'var(--color-mode-gambit)',
}

/** Triumph palette for donut segments; order-stable, not random per render. */
export const DONUT_COLORS = [
  'var(--color-class-titan)',
  'var(--color-class-warlock)',
  'var(--color-class-hunter)',
  'var(--color-mode-gambit)',
  'var(--color-info)',
  'var(--color-bar-emphasis)',
] as const
