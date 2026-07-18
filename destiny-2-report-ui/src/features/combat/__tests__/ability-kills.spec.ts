import { describe, expect, it } from 'vitest'
import { abilityKillIconKind } from '../ability-kills'

describe('abilityKillIconKind', () => {
  it.each([
    ['Grenade', 'grenade'],
    ['Melee', 'melee'],
    ['Super', 'super'],
    [' super ', 'super'],
  ])('maps %s to the expected icon', (name, expected) => {
    expect(abilityKillIconKind(name)).toBe(expected)
  })

  it('does not replace ordinary weapon artwork', () => {
    expect(abilityKillIconKind('Forerunner')).toBeNull()
  })
})
