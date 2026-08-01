import { describe, expect, it } from 'vitest'
import { makeReport, veteranReport } from '@/test/fixtures/report'
import { buildStorySlides, mostUsedActualWeapons } from '../selectors'

describe('buildStorySlides', () => {
  it('puts rare earned accomplishments before broad account totals', () => {
    const slides = buildStorySlides(veteranReport)

    expect(slides[0]?.key).toBe('solo-flawless')
    expect(slides.findIndex((slide) => slide.key === 'contest')).toBeLessThan(
      slides.findIndex((slide) => slide.key === 'time'),
    )
  })

  it('attaches real report imagery to personal story beats', () => {
    const slides = buildStorySlides(veteranReport)

    expect(slides.find((slide) => slide.key === 'people')?.imageUrl).toContain('bungie.net')
    expect(slides.find((slide) => slide.key === 'emblem')?.imageUrl).toContain('bungie.net')
  })

  it('uses the shared Guardian class colors for the playtime breakdown', () => {
    const time = buildStorySlides(veteranReport).find((slide) => slide.key === 'time')
    const colors = Object.fromEntries(time?.stats?.map((stat) => [stat.label, stat.color]) ?? [])

    expect(colors).toEqual({
      Titan: 'var(--color-class-titan)',
      Warlock: 'var(--color-class-warlock)',
      Hunter: 'var(--color-class-hunter)',
    })
  })

  it('does not turn an ordinary losing playlist into a highlight', () => {
    const report = makeReport({
      pvpPlaylists: [
        { mode: 73, modeName: 'Control', wins: 8, losses: 12, matches: 20, winRate: 0.4 },
      ],
    })

    expect(buildStorySlides(report).map((slide) => slide.key)).not.toContain('competitive')
  })

  it('never treats the undefined mode-zero playlist as a competitive highlight', () => {
    const report = makeReport({
      pvpPlaylists: [
        { mode: 0, modeName: 'None', wins: 30, losses: 0, matches: 30, winRate: 1 },
        { mode: 88, modeName: 'Rift', wins: 16, losses: 6, matches: 22, winRate: 0.7273 },
      ],
    })

    const slide = buildStorySlides(report).find((item) => item.key === 'competitive')
    expect(slide?.title).toContain('Rift')
    expect(slide?.value).toBe('16 wins in 22 matches')
  })

  it('summarizes long accomplishment lists instead of putting every name in the card', () => {
    const base = veteranReport.dungeonCompletions[0]!
    const report = makeReport({
      dungeonCompletions: Array.from({ length: 11 }, (_, index) => ({
        ...base,
        activityName: `Dungeon ${index + 1}`,
        soloFlawlessClear: true,
      })),
    })

    const slide = buildStorySlides(report).find((item) => item.key === 'solo-flawless')
    expect(slide?.value).toBe('11 dungeons solo flawless')
    expect(slide?.body).toBe('Dungeon 1, Dungeon 2, Dungeon 3 + 8 more')
  })

  it('pairs each contest clear with its raid-specific emblem', () => {
    const base = veteranReport.raidCompletions[0]!
    const report = makeReport({
      raidCompletions: [
        { ...base, activityName: 'Root of Nightmares', contestClear: true },
        { ...base, activityName: 'The Desert Perpetual: Epic', contestClear: true },
      ],
    })
    const assets = {
      raidIconUrl: '/raid.png',
      dungeonIconUrl: '/dungeon.png',
      crucibleIconUrl: '/crucible.png',
      guidedGamesIconUrl: '/guide.png',
      contestRaidEmblems: [
        {
          raidName: 'Root of Nightmares',
          emblemName: "A Good Night's Sleep",
          iconUrl: '/root-contest.jpg',
        },
        {
          raidName: 'The Desert Perpetual (Epic)',
          emblemName: 'Fractured Timeline',
          iconUrl: '/epic-contest.jpg',
        },
      ],
      pantheonEmblems: [],
      titanIconUrl: '/titan.png',
      hunterIconUrl: '/hunter.png',
      warlockIconUrl: '/warlock.png',
      goodBoyProtocolIconUrl: '/good-boy.png',
    }

    const slide = buildStorySlides(report, null, assets).find((item) => item.key === 'contest')

    expect(slide?.layout).toBe('contest-gallery')
    expect(slide?.items).toEqual([
      {
        label: 'Root of Nightmares',
        value: "A Good Night's Sleep",
        imageUrl: '/root-contest.jpg',
      },
      {
        label: 'The Desert Perpetual: Epic',
        value: 'Fractured Timeline',
        imageUrl: '/epic-contest.jpg',
      },
    ])
  })

  it('places completed Pantheon tiers after contest clears and uses canonical activity names', () => {
    const base = veteranReport.raidCompletions[0]!
    const report = makeReport({
      raidCompletions: [
        { ...base, activityName: 'Root of Nightmares', contestClear: true },
        { ...base, activityName: 'The Pantheon: Atraks Sovereign', contestClear: false },
        {
          ...base,
          activityName: 'Pantheon: Calus Resplendent: Customize',
          contestClear: false,
        },
        {
          ...base,
          activityName: 'Pantheon: Morgeth Surpassing: Customize',
          completionCount: 0,
          contestClear: false,
        },
      ],
    })
    const assets = {
      raidIconUrl: '/raid.png',
      dungeonIconUrl: '/dungeon.png',
      crucibleIconUrl: '/crucible.png',
      guidedGamesIconUrl: '/guide.png',
      contestRaidEmblems: [
        {
          raidName: 'Root of Nightmares',
          emblemName: "A Good Night's Sleep",
          iconUrl: '/root-contest.jpg',
        },
      ],
      pantheonEmblems: [
        {
          pantheonName: 'Pantheon: Atraks Sovereign',
          emblemName: 'Atraks Dethroned',
          iconUrl: '/atraks.jpg',
        },
        {
          pantheonName: 'Pantheon: Calus Resplendent',
          emblemName: 'Calus Conquered',
          iconUrl: '/calus.jpg',
        },
        {
          pantheonName: 'Pantheon: Morgeth Surpassing',
          emblemName: 'Morgeth Mastered',
          iconUrl: '/morgeth.jpg',
        },
      ],
      titanIconUrl: '/titan.png',
      hunterIconUrl: '/hunter.png',
      warlockIconUrl: '/warlock.png',
      goodBoyProtocolIconUrl: '/good-boy.png',
    }

    const slides = buildStorySlides(report, null, assets)
    const contestIndex = slides.findIndex((slide) => slide.key === 'contest')
    const pantheonIndex = slides.findIndex((slide) => slide.key === 'pantheon')
    const pantheon = slides[pantheonIndex]

    expect(pantheonIndex).toBe(contestIndex + 1)
    expect(pantheon?.layout).toBe('pantheon-gallery')
    expect(pantheon?.items).toEqual([
      {
        label: 'Pantheon: Atraks Sovereign',
        value: 'Atraks Dethroned',
        imageUrl: '/atraks.jpg',
        group: 'Pantheon 1.0',
      },
      {
        label: 'Pantheon: Calus Resplendent',
        value: 'Calus Conquered',
        imageUrl: '/calus.jpg',
        group: 'Pantheon 2.0',
      },
    ])
  })

  it('shows Pantheon completions without contest clears or a loaded emblem catalog', () => {
    const base = veteranReport.raidCompletions[0]!
    const report = makeReport({
      raidCompletions: [
        {
          ...base,
          activityName: 'The Pantheon: Nezarec Sublime',
          completionCount: 1,
          contestClear: false,
        },
        {
          ...base,
          activityName: 'Pantheon: Calus Resplendent: Customize',
          completionCount: 2,
          contestClear: false,
        },
      ],
    })

    const slides = buildStorySlides(report, null, null)
    const pantheon = slides.find((slide) => slide.key === 'pantheon')

    expect(slides.map((slide) => slide.key)).not.toContain('contest')
    expect(pantheon?.value).toBe('2 tiers completed')
    expect(pantheon?.items).toEqual([
      {
        label: 'Pantheon: Nezarec Sublime',
        value: 'Pantheon clear',
        imageUrl: undefined,
        group: 'Pantheon 1.0',
      },
      {
        label: 'Pantheon: Calus Resplendent',
        value: 'Pantheon clear',
        imageUrl: undefined,
        group: 'Pantheon 2.0',
      },
    ])
  })

  it('gives raid sherpas their own story card', () => {
    const slides = buildStorySlides(veteranReport)
    const sherpas = slides.find((slide) => slide.key === 'sherpas')

    expect(sherpas?.value).toContain('first-time raiders guided')
  })

  it('chooses a repeatable personality stat for each player', () => {
    const report = makeReport({
      goodBoyProtocol: 42,
      fishCaught: 317,
      misadventures: 89,
    })

    const first = buildStorySlides(report).find((slide) => slide.key === 'personality')
    const second = buildStorySlides({ ...report }).find((slide) => slide.key === 'personality')

    expect(first).toEqual(second)
  })

  it('only chooses personality stats with a value above zero', () => {
    const personality = buildStorySlides(
      makeReport({ goodBoyProtocol: 0, fishCaught: 317, misadventures: 0 }),
    ).find((slide) => slide.key === 'personality')

    expect(personality?.eyebrow).toBe('Fish caught')
    expect(personality?.value).toBe('317')
  })

  it('distributes personality stats between different players', () => {
    const selections = Array.from(
      { length: 30 },
      (_, index) =>
        buildStorySlides(
          makeReport({
            playerMembershipId: String(10_000 + index),
            goodBoyProtocol: 42,
            fishCaught: 317,
            misadventures: 89,
          }),
        ).find((slide) => slide.key === 'personality')?.eyebrow,
    )

    expect(new Set(selections)).toEqual(
      new Set(['Good Boy Protocol', 'Fish caught', 'Misadventures']),
    )
  })

  it('uses purpose-built layouts and never repeats an image inside one card', () => {
    const slides = buildStorySlides(veteranReport)

    expect(new Set(slides.map((slide) => slide.layout)).size).toBe(slides.length)
    for (const slide of slides) {
      const urls = [
        slide.iconUrl,
        slide.imageUrl,
        ...(slide.imageUrls?.map((image) => image.url) ?? []),
        ...(slide.items?.map((item) => item.imageUrl) ?? []),
        ...(slide.stats?.map((stat) => stat.iconUrl) ?? []),
      ].filter(Boolean)
      expect(new Set(urls).size).toBe(urls.length)
    }
  })

  it('returns no highlights when the report has no recorded history', () => {
    expect(buildStorySlides(makeReport())).toEqual([])
  })
})

describe('mostUsedActualWeapons', () => {
  it('aggregates real weapons while excluding abilities and unknown kills', () => {
    const weapon = mostUsedActualWeapons({
      activityMode: 'PvE',
      classes: [
        {
          className: 'Warlock',
          modes: [
            {
              specificActivityModeId: 4,
              specificActivityMode: 'Raid',
              categories: [
                {
                  categoryKey: 'ABILITIES',
                  categoryName: 'Abilities',
                  kills: 50_000,
                  weapons: [
                    {
                      weaponKey: 'GRENADE',
                      weaponName: 'Grenade',
                      referenceId: -1,
                      iconUrl: '',
                      categoryKey: 'ABILITIES',
                      categoryName: 'Abilities',
                      kills: 50_000,
                    },
                  ],
                },
                {
                  categoryKey: 'AUTO RIFLE',
                  categoryName: 'Auto Rifle',
                  kills: 120,
                  weapons: [
                    {
                      weaponKey: 'TOMMYS MATCHBOOK',
                      weaponName: "Tommy's Matchbook",
                      referenceId: 1,
                      iconUrl: 'https://www.bungie.net/tommy.jpg',
                      categoryKey: 'AUTO RIFLE',
                      categoryName: 'Auto Rifle',
                      kills: 120,
                    },
                  ],
                },
              ],
            },
          ],
        },
      ],
    })[0]

    expect(weapon).toEqual({
      name: "Tommy's Matchbook",
      iconUrl: 'https://www.bungie.net/tommy.jpg',
      kills: 120,
    })
  })

  it('returns a descending top-five leaderboard after aggregating characters', () => {
    const weapons = {
      activityMode: 'PvE',
      classes: [
        {
          className: 'Titan',
          modes: [
            {
              specificActivityModeId: 4,
              specificActivityMode: 'Raid',
              categories: [
                {
                  categoryKey: 'TEST',
                  categoryName: 'Test',
                  kills: 0,
                  weapons: Array.from({ length: 7 }, (_, index) => ({
                    weaponKey: `WEAPON ${index}`,
                    weaponName: `Weapon ${index}`,
                    referenceId: index + 1,
                    iconUrl: `https://www.bungie.net/weapon-${index}.jpg`,
                    categoryKey: 'TEST',
                    categoryName: 'Test',
                    kills: (index + 1) * 100,
                  })),
                },
              ],
            },
          ],
        },
      ],
    }

    expect(mostUsedActualWeapons(weapons).map((weapon) => weapon.name)).toEqual([
      'Weapon 6',
      'Weapon 5',
      'Weapon 4',
      'Weapon 3',
      'Weapon 2',
    ])
  })
})
