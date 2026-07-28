import { describe, expect, it } from 'vitest'
import type { LeaderboardDefinition } from '@/lib/api/types'
import { organizeLeaderboards } from '../leaderboard-collections'

function board(
  key: string,
  category: string,
  title = key,
  displayOrder = 1,
): LeaderboardDefinition {
  return {
    key,
    category,
    title,
    description: `${title} description`,
    unit: 'count',
    displayOrder,
    rankedPlayerCount: 10,
    isRepairing: false,
  }
}

describe('organizeLeaderboards', () => {
  it('organizes records by how players browse them instead of API category', () => {
    const collections = organizeLeaderboards([
      board('time.patrol.nessus', 'Time'),
      board('combat.weapon-type.bow', 'Combat'),
      board('combat.kills.total', 'Combat'),
      board('competition.crucible.playlist.10', 'Competition'),
      board('competition.crucible.wins', 'Competition'),
      board('oddities.fish-caught', 'Oddities'),
    ])

    expect(
      Object.fromEntries(
        collections.map((collection) => [
          collection.key,
          collection.boards.map((item) => item.key),
        ]),
      ),
    ).toMatchObject({
      destinations: ['time.patrol.nessus'],
      arsenal: ['combat.weapon-type.bow'],
      combat: ['combat.kills.total'],
      competitive: ['competition.crucible.wins'],
      'crucible-core': ['competition.crucible.playlist.10'],
      curiosities: ['oddities.fish-caught'],
    })

    expect(collections.find((collection) => collection.key === 'curiosities')?.title).toBe(
      'Guardian curiosities',
    )
  })

  it('splits specific activity records into focused Destiny activity families', () => {
    const collections = organizeLeaderboards([
      board('time.mode.4', 'Time', 'Raid playtime'),
      board('combat.kills.mode.3', 'Combat', 'Strike kills'),
      board('time.mode.77', 'Time', 'Menagerie playtime'),
      board('competition.gambit.playlist.75', 'Competition', 'Gambit Prime wins'),
      board('combat.kills.mode.10', 'Combat', 'Control kills'),
      board('time.mode.25', 'Time', 'Mayhem playtime'),
      board('competition.crucible.playlist.37', 'Competition', 'Survival wins'),
      board('time.mode.84', 'Time', 'Trials of Osiris playtime'),
      board('combat.kills.mode.43', 'Combat', 'Iron Banner Control kills'),
      board('time.mode.51', 'Time', 'Private Clash playtime'),
    ])

    const keysByCollection = Object.fromEntries(
      collections.map((collection) => [collection.key, collection.boards.map((item) => item.key)]),
    )

    expect(keysByCollection).toMatchObject({
      'pve-endgame': ['time.mode.4'],
      'pve-activities': ['combat.kills.mode.3'],
      'legacy-activities': ['time.mode.77'],
      'gambit-modes': ['competition.gambit.playlist.75'],
      'crucible-core': ['combat.kills.mode.10'],
      'crucible-rotators': ['time.mode.25'],
      'crucible-competitive': ['competition.crucible.playlist.37'],
      trials: ['time.mode.84'],
      'iron-banner': ['combat.kills.mode.43'],
    })
    expect(keysByCollection).not.toHaveProperty('private-matches')
    expect(Object.values(keysByCollection).flat()).not.toContain('time.mode.51')
    expect(keysByCollection).not.toHaveProperty('playlists')
  })

  it('does not create or populate a collection for any private match mode', () => {
    const privateModes = [32, 51, 52, 53, 54, 55, 56, 57]
    const collections = organizeLeaderboards(
      privateModes.flatMap((mode) => [
        board(`time.mode.${mode}`, 'Time'),
        board(`combat.kills.mode.${mode}`, 'Combat'),
        board(`competition.crucible.playlist.${mode}`, 'Competition'),
      ]),
    )

    expect(collections).toEqual([])
  })

  it('presents overlapping mode metrics as one choice with metric variants', () => {
    const collection = organizeLeaderboards([
      board('combat.kills.mode.10', 'Combat', 'Control kills'),
      board('competition.crucible.playlist.10', 'Competition', 'Control wins'),
      board('time.mode.10', 'Time', 'Control playtime'),
    ])[0]!

    expect(collection.key).toBe('crucible-core')
    expect(collection.choices).toHaveLength(1)
    expect(collection.choices[0]).toMatchObject({
      key: 'mode:10',
      title: 'Control',
      variants: [
        { kind: 'time', label: 'Time spent', board: { key: 'time.mode.10' } },
        { kind: 'kills', label: 'Kills', board: { key: 'combat.kills.mode.10' } },
        {
          kind: 'wins',
          label: 'Wins',
          board: { key: 'competition.crucible.playlist.10' },
        },
      ],
    })
  })

  it('gives Gambit mote records their own collection next to Gambit modes', () => {
    const collections = organizeLeaderboards([
      board('time.mode.63', 'Time', 'Gambit playtime'),
      board('oddities.gambit-motes-banked', 'Oddities', 'Gambit motes banked'),
      board('oddities.gambit-motes-lost', 'Oddities', 'Gambit motes lost'),
      board('oddities.gambit-motes-denied', 'Oddities', 'Gambit motes denied'),
      board('oddities.fish-caught', 'Oddities', 'Fish caught'),
    ])

    expect(collections.map((collection) => collection.key)).toEqual([
      'gambit-modes',
      'gambit-motes',
      'curiosities',
    ])
    expect(collections[1]?.boards.map((item) => item.key)).toEqual([
      'oddities.gambit-motes-banked',
      'oddities.gambit-motes-denied',
      'oddities.gambit-motes-lost',
    ])
    expect(collections[2]?.boards.map((item) => item.key)).toEqual([
      'oddities.fish-caught',
    ])
  })

  it('sorts leaderboard choices by database display order with a stable title tie-breaker', () => {
    const collection = organizeLeaderboards([
      board('oddities.fish-caught', 'Oddities', 'Fish caught', 30),
      board('oddities.misadventures', 'Oddities', 'Misadventures', 10),
      board('oddities.zero-kill-activities', 'Oddities', 'Zero-kill activities', 20),
      board('oddities.good-boy-protocol', 'Oddities', 'Good Boy Protocol', 20),
    ]).find((item) => item.key === 'curiosities')!

    expect(collection.choices.map((choice) => choice.title)).toEqual([
      'Misadventures',
      'Good Boy Protocol',
      'Zero-kill activities',
      'Fish caught',
    ])
  })

  it('uses the lowest display order when one dropdown choice groups several metrics', () => {
    const collection = organizeLeaderboards([
      board('time.mode.10', 'Time', 'Control playtime', 40),
      board('combat.kills.mode.10', 'Combat', 'Control kills', 5),
      board('time.mode.12', 'Time', 'Clash playtime', 20),
    ])[0]!

    expect(collection.choices.map((choice) => choice.title)).toEqual(['Control', 'Clash'])
  })
})
