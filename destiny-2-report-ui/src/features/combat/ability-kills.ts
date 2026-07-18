export type AbilityKillIconKind = 'grenade' | 'melee' | 'super'

export function abilityKillIconKind(name: string): AbilityKillIconKind | null {
  switch (name.trim().toLowerCase()) {
    case 'grenade':
      return 'grenade'
    case 'melee':
      return 'melee'
    case 'super':
      return 'super'
    default:
      return null
  }
}
