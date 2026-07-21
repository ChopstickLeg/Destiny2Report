import { describe, expect, it } from 'vitest'
import {
  activityModeAliases,
  canonicalPatrolDestination,
  humanizeModeName,
  patrolDestinationAliases,
} from '../destiny-display'

describe('Destiny display mappings', () => {
  it('maps every patrol alias to a visible canonical destination', () => {
    for (const [source, expected] of Object.entries(patrolDestinationAliases)) {
      expect(canonicalPatrolDestination(source)).toBe(expected)
    }
    expect(canonicalPatrolDestination('Unknown')).toBeNull()
  })

  it('maps every explicit activity alias and Mode 94 consistently', () => {
    for (const [source, expected] of Object.entries(activityModeAliases)) {
      expect(humanizeModeName(source)).toBe(expected)
    }
    expect(humanizeModeName('Mode 94')).toBe('Sparrow Racing League')
  })
})
