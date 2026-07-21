import { describe, expect, it } from 'vitest'
import { makeReport, veteranReport } from '@/test/fixtures/report'
import {
  distinctions,
  hasCompetitiveData,
  hasEndgameData,
  hasOdditiesData,
  hasSealsData,
  humanizeModeName,
  rankByMode,
  rankCharacterPlaytime,
  rankPatrolTime,
  sortCompletions,
  summarizeStreak,
} from '../report-view'

describe('rankCharacterPlaytime', () => {
  it('sorts by playtime and tags deleted characters', () => {
    const ranked = rankCharacterPlaytime(veteranReport.characterPlaytime)
    expect(ranked[0]?.label).toBe('Titan · Exo')
    expect(ranked[0]?.className).toBe('Titan')
    expect(ranked.find((c) => c.tag === 'Deleted')).toBeTruthy()
  })

  it('drops zero-playtime characters', () => {
    const ranked = rankCharacterPlaytime([
      { race: 'Human', class: 'Hunter', isDeleted: false, playtime: '00:00:00' },
    ])
    expect(ranked).toEqual([])
  })
})

describe('rankPatrolTime', () => {
  it('renames legacy patrol locations and combines them with their destination', () => {
    const ranked = rankPatrolTime({
      'Arcadian Valley': '02:00:00',
      Nessus: '01:00:00',
      'Echo Mesa': '00:30:00',
      'Hellas Basin': '00:20:00',
      'New Pacific Arcology': '00:10:00',
    })

    expect(ranked.map((entry) => entry.label)).toEqual(['Nessus', 'IO', 'Mars', 'Titan'])
    expect(ranked[0]?.seconds).toBe(3 * 60 * 60)
  })

  it('keeps approved destinations and hides every other backend location', () => {
    const ranked = rankPatrolTime({
      'The Pale Heart': '03:00:00',
      Europa: '02:00:00',
      Cosmodrome: '01:00:00',
      'The Cosmodrome': '10:00:00',
      'Unknown Destination': '20:00:00',
    })

    expect(ranked.map((entry) => entry.label)).toEqual(['The Pale Heart', 'Europa', 'Cosmodrome'])
  })
})

describe('summarizeStreak', () => {
  it('counts inclusive days', () => {
    const streak = summarizeStreak({
      startDate: '2024-01-01T00:00:00Z',
      endDate: '2024-01-14T00:00:00Z',
    })
    expect(streak?.days).toBe(14)
  })

  it('rejects inverted ranges and null', () => {
    expect(summarizeStreak(null)).toBeNull()
    expect(
      summarizeStreak({ startDate: '2024-02-01T00:00:00Z', endDate: '2024-01-01T00:00:00Z' }),
    ).toBeNull()
  })
})

describe('sortCompletions', () => {
  it('puts cleared activities before attempted-only ones', () => {
    const sorted = sortCompletions(veteranReport.raidCompletions)
    expect(sorted[0]?.activityName).toBe('Last Wish')
    expect(sorted[sorted.length - 1]?.activityName).toBe('Crota’s End')
  })
})

describe('distinctions', () => {
  it('emits only earned facts', () => {
    const summary = veteranReport.dungeonCompletions[0]!
    const badges = distinctions(summary).map((d) => d.key)
    // Solo flawless subsumes solo and flawless.
    expect(badges).toEqual(['solo-flawless'])
  })

  it('returns nothing when nothing was earned', () => {
    const summary = veteranReport.raidCompletions[2]!
    expect(distinctions(summary)).toEqual([])
  })
})

describe('rankByMode', () => {
  it('sorts, filters zeroes, and groups the tail into Other', () => {
    const ranked = rankByMode({ A: 10, B: 0, C: 30, D: 5, E: 4, F: 3, G: 2, H: 1, I: 1, J: 1 }, 3)
    expect(ranked[0]).toEqual({ key: 'C', label: 'C', value: 30 })
    expect(ranked).toHaveLength(4)
    expect(ranked[3]?.key).toBe('other')
    expect(ranked[3]?.value).toBe(12)
  })
})

describe('humanizeModeName', () => {
  it('splits PascalCase mode names', () => {
    expect(humanizeModeName('IronBanner')).toBe('Iron Banner')
    expect(humanizeModeName('TrialsOfOsiris')).toBe('Trials Of Osiris')
    expect(humanizeModeName('Control')).toBe('Control')
    expect(humanizeModeName('Mode 84')).toBe('Mode 84')
  })

  it('replaces known API placeholder names', () => {
    expect(humanizeModeName('Mode 93')).toBe('Lawless Frontier')
    expect(humanizeModeName('Mode 94')).toBe('Sparrow Racing League')
  })
})

describe('section presence', () => {
  it('collapses sections for an empty report', () => {
    const empty = makeReport()
    expect(hasCompetitiveData(empty)).toBe(false)
    expect(hasEndgameData(empty)).toBe(false)
    expect(hasSealsData(empty)).toBe(false)
    expect(hasOdditiesData(empty)).toBe(false)
  })

  it('detects data in the dense fixture', () => {
    expect(hasCompetitiveData(veteranReport)).toBe(true)
    expect(hasEndgameData(veteranReport)).toBe(true)
    expect(hasSealsData(veteranReport)).toBe(true)
    expect(hasOdditiesData(veteranReport)).toBe(true)
  })
})
