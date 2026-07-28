import { describe, expect, it } from 'vitest'
import { canonicalPatrolDestination, humanizeModeName } from '../destiny-display'

describe('Destiny display mappings', () => {
  it('maps legacy patrol aliases to visible canonical destinations', () => {
    const aliases = {
      'Arcadian Valley': 'Nessus',
      'Echo Mesa': 'IO',
      'Hellas Basin': 'Mars',
      'New Pacific Arcology': 'Titan',
    }
    for (const [source, expected] of Object.entries(aliases)) {
      expect(canonicalPatrolDestination(source)).toBe(expected)
    }
    expect(canonicalPatrolDestination('The Pale Heart')).toBe('The Pale Heart')
    expect(canonicalPatrolDestination('Unknown')).toBeNull()
  })

  it('maps explicit and camel-cased activity names consistently', () => {
    expect(humanizeModeName('Mode 93')).toBe('Lawless Frontier')
    expect(humanizeModeName('Mode 94')).toBe('Sparrow Racing League')
    expect(humanizeModeName('LawlessFrontier')).toBe('Lawless Frontier')
    expect(humanizeModeName('SparrowRacingLeague')).toBe('Sparrow Racing League')
    expect(humanizeModeName('TrialsOfOsiris')).toBe('Trials Of Osiris')
  })
})
