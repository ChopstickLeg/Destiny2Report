# Tracked Statistics Notes

## Confirmed Sources

### Metrics / Stat Trackers

- Source: `GetProfile` with component `1100` (`DestinyComponentType.Metrics`).
- Definitions: `DestinyMetricDefinition` from the Destiny manifest.
- Runtime values: `Response.metrics.data.metrics[metricHash].objectiveProgress.progress`.
- Useful fields:
  - `DestinyMetricDefinition.displayProperties.name`
  - `DestinyMetricDefinition.displayProperties.description`
  - `DestinyMetricDefinition.trackingObjectiveHash`
  - `DestinyMetricComponent.invisible`
  - `DestinyMetricComponent.objectiveProgress.progress`
  - `DestinyMetricComponent.objectiveProgress.completionValue`
  - `DestinyMetricComponent.objectiveProgress.complete`

Confirmed metrics:

- Good Boy Protocol / Archie pet stat
- Fish caught stat

Manifest lookup flow:

1. Call `GetDestinyManifest`.
2. Read `Response.jsonWorldComponentContentPaths["en"]["DestinyMetricDefinition"]`.
3. Download `https://www.bungie.net{path}`.
4. Search the returned JSON table by `displayProperties.name`.
5. Use the matching metric hash with profile component `1100`.

### Historical Stats

- Source: `GetHistoricalStatsForAccount` for account-wide merged totals.
- Source: `GetHistoricalStats` for per-character/per-mode historical stats.
- Source: `GetHistoricalStatsDefinition` for supported historical stat IDs.
- Source docs:
  - Historical stats by mode: https://bungie-net.github.io/multi/operation_get_Destiny2-GetHistoricalStats.html
  - Account historical stats: https://bungie-net.github.io/multi/operation_get_Destiny2-GetHistoricalStatsForAccount.html
  - Activity mode enum: https://bungie-net.github.io/multi/schema_Destiny-HistoricalStats-Definitions-DestinyActivityModeType.html
- Values usually live under:
  - `mergedAllCharacters.merged.allTime[statId]`
  - `characters[characterId].merged.allTime[statId]`
  - `characters[characterId].results.allPvE/allPvP/...`

Useful activity mode IDs:

- `5`: AllPvP / Crucible aggregate.
- `7`: AllPvE aggregate.
- `63`: Gambit.
- `75`: Gambit Prime, older/legacy Gambit Prime bucket.
- `64`: AllPvECompetitive, useful to inspect for Gambit-era aggregate behavior.

Useful confirmed stat IDs:

- `secondsPlayed`: total historical activity time.
- `totalActivityDurationSeconds`: total activity duration, depending on the aggregation needed.
- `activitiesEntered`: activities entered.
- `activitiesCleared`: PvE-style successful completions.
- `activitiesWon`: wins in modes where wins apply.
- `completed`: PGCR/current activity completion flag.
- `completionReason`: PGCR completion result/reason.
- `kills`: historical kills.
- `deaths`: historical deaths.
- `assists`: historical assists.
- `opponentsDefeated`: kills plus assists style defeated count.
- `killsDeathsRatio`: K/D.
- `killsDeathsAssists`: KDA.
- `efficiency`: efficiency.
- `suicides`: misadventures.
- `fastestCompletionMs`: fastest completion.
- `fastestCompletionMsForActivity`: fastest completion for a specific activity.
- `activityDurationSeconds`: PGCR activity duration.
- `timePlayedSeconds`: PGCR player time played.
- `remainingTimeAfterQuitSeconds`: time remaining after quitting.

### Activity History / PGCR

- Source: `GetActivityHistory`.
- Source: `GetPostGameCarnageReport`.
- Use this path for first/earliest activity questions, single-match bests, zero-kill activities, flawless checks, and solo checks.
- `GetActivityHistory` gives activity instances and timestamps.
- `GetPostGameCarnageReport` gives per-player entries and activity values for a single activity instance.
- Source docs:
  - Activity history: https://bungie-net.github.io/multi/operation_get_Destiny2-GetActivityHistory.html
  - PGCR: https://bungie-net.github.io/multi/schema_Destiny-HistoricalStats-DestinyPostGameCarnageReportData.html
  - PGCR entry: https://bungie-net.github.io/multi/schema_Destiny-HistoricalStats-DestinyPostGameCarnageReportEntry.html
  - PGCR extended data: https://bungie-net.github.io/multi/schema_Destiny-HistoricalStats-DestinyPostGameCarnageReportExtendedData.html

Useful PGCR fields:

- `period`: activity start time.
- `activityDetails.instanceId`: PGCR instance ID.
- `activityDetails.referenceId`: activity hash.
- `activityDetails.directorActivityHash`: director activity hash.
- `activityDetails.mode`: primary historical activity mode.
- `activityDetails.modes`: all activity modes.
- `activityWasStartedFromBeginning`: useful when validating flawless/full-run conditions.
- `teams[].standing`
- `teams[].score`
- `entries[].standing`
- `entries[].values["completed"]`
- `entries[].values["kills"]`
- `entries[].values["deaths"]`
- `entries[].values["timePlayedSeconds"]`
- `entries[].values["activityDurationSeconds"]`
- `entries[].extended.values`
- `entries[].extended.scoreboardValues`
- `entries[].extended.weapons[].referenceId`
- `entries[].extended.weapons[].values`

### Manifest Definitions

- `DestinyActivityDefinition`: map activity hashes to names, activity types, mode hashes, and display metadata.
- `DestinyActivityModeDefinition`: resolve mode hashes, parent modes, and mode categories.
- `DestinyInventoryItemDefinition`: resolve weapon hashes from PGCR/weapon history into weapon names and item metadata.
- `DestinyMetricDefinition`: find stat tracker/metric hashes such as Good Boy Protocol and fish caught.
- `DestinyPresentationNodeDefinition`: title/seal presentation tree and other presentation hierarchy.
- `DestinyRecordDefinition`: title/seal records and completion state definitions.

## Current Feature Ideas

### Play Time

- Best source: `GetHistoricalStatsForAccount`.
- Use `mergedAllCharacters.merged.allTime["secondsPlayed"].basic.value` for total historical activity time.
- Compare with `totalActivityDurationSeconds` and pick the one that best matches the intended display.
- Avoid summing `allParticipantsTimePlayed` for personal play time; that stat sounds like aggregate participant time and can overcount.

### PvE / Crucible / Gambit Play Time Split

- Source: `GetHistoricalStats` per character with mode filters, or `GetHistoricalStatsForAccount` if its mode buckets are complete enough.
- Mode IDs:
  - PvE: `7` / AllPvE
  - Crucible: `5` / AllPvP
  - Gambit: `63` / Gambit
  - Legacy Gambit Prime: `75`
- Use `secondsPlayed` from each bucket.
- For the cleanest total, sum per-character values, then group by mode.
- For Gambit, validate whether mode `63` already includes legacy `75`; if not, include both and avoid double counting by checking `activityDetails.modes` in activity history.
- PGCR crawl alternative:
  - classify each activity by `activityDetails.modes`
  - sum the target player's `timePlayedSeconds` or activity `activityDurationSeconds`
  - useful if historical mode buckets behave oddly.

### Patrol Time by Destination

- Source: `GetActivityHistory` plus manifest definitions.
- Mode ID:
  - Patrol: `6`
- Feasible, but requires activity-history crawling.
- Approach:
  - call `GetActivityHistory` per character with mode `6`, or crawl all history and filter activities whose `activityDetails.modes` includes `6`
  - for each patrol activity, look up `activityDetails.referenceId` or `directorActivityHash` in `DestinyActivityDefinition`
  - read `DestinyActivityDefinition.destinationHash`
  - resolve that hash through `DestinyDestinationDefinition` for the destination display name
  - sum target player's `timePlayedSeconds` when present, otherwise use `activityDurationSeconds`
- Optional grouping:
  - use `placeHash` / `DestinyPlaceDefinition` if the UI wants broader places instead of specific destinations
  - keep both `destinationHash` and `placeHash` in the model so grouping can be changed later
- Caveats:
  - Patrol sessions are activity instances, so long patrol visits may be split into multiple PGCR/history rows.
  - Public events, lost sectors, seasonal public spaces, or destination activities may or may not be mode `6`; include only mode `6` if the display should mean literal Patrol.
  - Destination names can shift across vaulted/reworked destinations, so store hashes as the stable key and resolve names from the manifest.

### Crucible / Gambit KD and KDA

- Source: `GetHistoricalStats` per character with mode filters.
- Crucible:
  - mode `5`
  - use `killsDeathsRatio`
  - use `killsDeathsAssists`
- Gambit:
  - mode `63`, plus `75` for legacy Gambit Prime if needed
  - use `killsDeathsRatio`
  - use `killsDeathsAssists`
- Important Gambit caveat:
  - Historical Gambit `kills`/KDA may include PvE combatants depending on Bungie's stat semantics.
  - If the desired number is Guardian-vs-Guardian Gambit K/D, verify a sample Gambit PGCR for invasion/guardian-kill stat keys before trusting the aggregate historical K/D.

### Crucible / Gambit Wins and Losses

- Source: `GetHistoricalStats` per character with mode filters.
- Use:
  - `activitiesEntered`
  - `activitiesWon`
- Losses are inferred:
  - `losses = activitiesEntered - activitiesWon`
- Caveats:
  - The inferred loss count may include quits, incomplete games, ties, private matches, or weird historical records.
  - PGCR crawl can produce a stricter win/loss record by using target player/team `standing`, `completed`, and `completionReason`.
  - Crucible mode: `5`
  - Gambit mode: `63`, plus `75` if needed for legacy Gambit Prime.

### Crucible Rival / Most Played Opponent

- Source: `GetActivityHistory` plus PGCR.
- Feasible:
  - most played against opponent
  - number of matches against that opponent
  - wins/losses in games where that opponent was present, if team standing is reliable
  - target player's aggregate K/D in matches where that opponent was present
- Not directly exposed:
  - true head-to-head kills by target player against that specific opponent
  - true deaths of target player caused by that specific opponent
  - true K/D specifically against that opponent
- Why:
  - PGCR exposes participants and aggregate player stats, but not a kill feed or victim/attacker matrix.
- Suggested display:
  - "Rival": opponent with the highest count of PGCRs against the target player.
  - "Record vs Rival": inferred from team/player standing in those PGCRs.
  - "K/D in Rival Games": target player's total kills divided by deaths across games where that opponent was on the opposing side.
- Avoid labeling this as "K/D against Rival" unless a future API/source provides kill attribution.

### Gambit Motes Banked / Lost

- Source: PGCR, likely `entries[].values`, `entries[].extended.values`, or `entries[].extended.scoreboardValues`.
- Feasible with validation against sample Gambit PGCRs.
- Approach:
  - crawl Gambit PGCRs for mode `63` and legacy `75`
  - inspect target player's value dictionaries for keys containing `mote`, `bank`, `deposit`, or `lost`
  - once stat IDs are confirmed, sum them across PGCRs
- Expected outputs:
  - total motes banked/deposited
  - total motes lost
  - optional per-game bests
- Caveats:
  - Exact stat IDs should be discovered from real Gambit PGCR payloads before hardcoding.
  - Some values may live in `scoreboardValues` rather than the older `values` dictionary.
  - Historical account stats may expose some Gambit totals, but PGCR crawl is safer for exact "banked/lost" scoreboard totals.

### Top Weapons by PvE / PvP / Gambit

- Source for unsplit all-time weapon usage: `GetUniqueWeaponHistory`.
- Source for PvE/PvP/Gambit split: PGCR crawl using `entries[].extended.weapons`.
- Source docs:
  - Unique weapon history: https://bungie-net.github.io/multi/operation_get_Destiny2-GetUniqueWeaponHistory.html
  - Historical weapon stats: https://bungie-net.github.io/multi/schema_Destiny-HistoricalStats-DestinyHistoricalWeaponStats.html
- For each PGCR:
  - identify the target player's entry
  - classify the activity as PvE, Crucible, or Gambit from `activityDetails.modes`
  - read `entry.extended.weapons[].referenceId`
  - aggregate weapon values by `referenceId`
  - resolve `referenceId` through `DestinyInventoryItemDefinition`
- Define "most used" as one of:
  - weapon kills, recommended
  - precision kills
  - total weapon stat value available in the weapon `values` dictionary
- Top 10 lists:
  - top 10 PvE weapons
  - top 10 Crucible weapons
  - top 10 Gambit weapons
- Caveats:
  - No known "time wielded" stat, so usage should mean kills unless another weapon value is confirmed.
  - Ability kills will not belong to a weapon.
  - `GetUniqueWeaponHistory` is useful as a fallback/all-time weapon list, but it does not provide the requested PvE/PvP/Gambit split by itself.

### Class Split

- Source: `GetHistoricalStatsForAccount`.
- Use per-character `secondsPlayed` and map each character to class from profile character data.
- If using character profile data, `DestinyCharacterComponent.minutesPlayedTotal` is also available, but it includes idle/menu/social time and is not activity-only.

### Titles

- Source: `GetProfile` with presentation/record components.
- `profileRecords` component `900` gives profile-wide record state.
- Use `DestinyPresentationNodeDefinition` and `DestinyRecordDefinition` from the manifest to resolve title/seal names and completion requirements.
- `recordSealsRootNodeHash` points at the root presentation node for seals.

### Stat Trackers

- Source: `GetProfile` component `1100`.
- Confirmed:
  - Good Boy Protocol / Archie pet.
  - Fish caught.
- Search `DestinyMetricDefinition` by display name, then read the matching metric hash from profile metrics.

### Misadventures

- Source: `GetHistoricalStatsForAccount`.
- Use `suicides`.

### Zero-Kill Activities

- Source: `GetActivityHistory` plus `GetPostGameCarnageReport`.
- Filter PGCR entries for the target player where `kills == 0`.
- Decide whether to include incomplete/quit activities by checking `completed` and `completionReason`.

### Overall Kill Count

- Source: `GetHistoricalStatsForAccount`.
- Use `mergedAllCharacters.merged.allTime["kills"].basic.value`.
- Consider also showing `opponentsDefeated` if the UI wants a broader "defeated" number.

### Orbit / Non-Activity Time

- No direct orbit-time stat found.
- Approximation:
  - Sum `DestinyCharacterComponent.minutesPlayedTotal * 60`.
  - Subtract historical `secondsPlayed` or `totalActivityDurationSeconds`.
- This remainder is not pure orbit. It likely includes orbit, menus, vendors, social spaces, loading, and idle time.

### First Raid / Dungeon Completion

- Source: `GetActivityHistory` plus PGCR.
- Modes:
  - Raid: `4`
  - Dungeon: `82`
- Crawl history, filter completed PGCRs, then choose the earliest `period`.
- Prefer checking `completed == 1`; use `completionReason` as supporting detail.

### Day 1 Raid / Dungeon Completions

- Source: `GetActivityHistory` plus PGCR.
- Modes:
  - Raid: `4`
  - Dungeon: `82`
- Needs a curated release-window table keyed by activity/raid/dungeon:
  - activity name
  - canonical activity hash or hashes
  - release start time in UTC
  - Day 1 cutoff time in UTC
  - whether Contest Mode was available for that release
  - optional Contest modifier hash / activity hash if known
- Crawl activity history for raid/dungeon completions and match completed PGCRs where `period` falls inside the configured Day 1 window.
- Prefer using the activity's end time for strict validation if available:
  - `period + activityDurationSeconds <= dayOneCutoff`
  - fall back to `period <= dayOneCutoff` if duration is missing.
- Day 1 should be based on the specific activity's launch window, not the player's local calendar date.
- Dungeons may not have the same Day 1/Contest treatment as raids, so mark dungeon Day 1 separately from raid Day 1.

Contest Mode indicator:

- Use PGCR `selectedSkullHashes` first. Bungie documents this as the collection of active skull/modifier hashes for the completed activity.
- Compare `selectedSkullHashes` against known `DestinyActivityModifierDefinition` hashes whose `displayProperties.name` is exactly `Contest Mode`.
- Source docs:
  - PGCR `selectedSkullHashes`: https://bungie-net.github.io/multi/schema_Destiny-HistoricalStats-DestinyPostGameCarnageReportData.html
  - Activity modifier definitions: https://bungie-net.github.io/multi/schema_Destiny-Definitions-ActivityModifiers-DestinyActivityModifierDefinition.html
  - Activity definitions/modifier references: https://bungie-net.github.io/multi/schema_Destiny-Definitions-DestinyActivityDefinition.html
- Do not match on descriptions containing the word "Contest" alone. Some compound modifiers, especially Grandmaster modifiers, include Contest-like text but are not the raid/dungeon launch Contest Mode modifier.
- To keep this current, rebuild the allowlist from the manifest:
  - load `DestinyActivityModifierDefinition`
  - filter where `displayProperties.name == "Contest Mode"`
  - keep the hashes in config alongside the curated release table
- For a PGCR:
  - `confirmed`: `selectedSkullHashes` contains a known Contest Mode modifier hash
  - `inferred`: no known Contest hash present, but completion is inside a known Contest-enabled launch window
  - `not_applicable`: activity predates Contest or had no Contest mode
  - `unknown`: selected skull hashes are missing and the release table does not settle it
- For raids/dungeons after Deep Stone Crypt, prefer the `selectedSkullHashes` check over release-window inference.
- For raids/dungeons without Contest Mode, use the release-window table only to decide Day 1 status.
- Secondary signals if `selectedSkullHashes` is missing:
  - known release-window metadata for that raid/dungeon
  - `DestinyActivityDefinition` variants/hashes for Contest versions
  - activity modifiers on the resolved `DestinyActivityDefinition`, if the manifest exposes a Contest modifier for that activity
  - PGCR `activityDetails.referenceId` / `directorActivityHash` matching a known Contest activity hash
- Note: Contest Mode was not consistently a launch-raid/dungeon concept early in Destiny 2. Treat older raids/dungeons as `not_applicable` unless a curated table says otherwise.

### First Raid / Dungeon Flawless

- Source: `GetActivityHistory` plus PGCR.
- Player flawless: completed activity where the target player's `deaths == 0`.
- Team flawless: completed activity where all relevant PGCR entries have `deaths == 0`.
- Prefer `activityWasStartedFromBeginning == true` when available.

### First Dungeon Solo Flawless

- Source: `GetActivityHistory` plus PGCR.
- Completed dungeon, target player's `deaths == 0`, and solo participation.
- Validate solo by checking player count / fireteam membership in PGCR entries.
- Prefer `activityWasStartedFromBeginning == true` when available.

### Most Common Fireteam Combo

- Source: `GetActivityHistory` plus PGCR.
- Requires crawling PGCRs and grouping teammate sets across activities.
- Build separate leaderboards for:
  - 3-player activities
  - 6-player activities
- For each PGCR:
  - identify the target player's team/fireteam
  - collect the other participating players on that same team/fireteam
  - normalize the combo key by stable membership IDs, sorted ascending
  - include the target player in the combo key if the display should mean "full fireteam including me"
  - exclude the target player if the display should mean "most common teammates"
- Prefer stable IDs over display names:
  - `destinyUserInfo.membershipType`
  - `destinyUserInfo.membershipId`
  - optionally `bungieNetUserInfo.membershipId` when available
- Use `activityDetails.mode`, `activityDetails.modes`, and manifest activity data to decide whether the activity belongs in the 3-player or 6-player bucket.
- Only count completed activities if this is meant to represent meaningful fireteam history; include incomplete activities only if the UI says "played with" rather than "cleared with".
- Watch-outs:
  - Matchmade activities can create noisy "fireteam" combos if PGCR only exposes team membership rather than premade fireteam membership.
  - Raids can have replacements, disconnects, or partial clears.
  - Some activities are 4-player, 2-player, or odd-sized and should be excluded from the 3/6 leaderboards unless intentionally supported.

### Unique Players Encountered

- Source: `GetActivityHistory` plus PGCR.
- Requires crawling PGCRs and building a distinct set of player identities.
- Prefer stable IDs over display names:
  - `destinyUserInfo.membershipType`
  - `destinyUserInfo.membershipId`
  - optionally `bungieNetUserInfo.membershipId` when available
- Decide which count the UI wants:
  - teammates only
  - opponents only
  - all players in the PGCR except the target player
  - all players in specific buckets such as raids, dungeons, Crucible, Gambit, or seasonal activities
- For each PGCR:
  - identify the target player's entry
  - compare teams/fireteams if teammate/opponent split is needed
  - add matching player IDs to a `HashSet`
- Watch-outs:
  - Cross-save and platform membership can make identity normalization tricky; prefer Destiny membership identity unless a Bungie membership ID is consistently available.
  - Private/deleted/renamed players may have incomplete display data, but stable IDs are still the important part.
  - Counting all playlist PGCRs can get very large, especially Crucible and matchmade PvE.

### Guardians Carried Through First Raid

- No official "carry" stat found.
- Possible inference:
  - For each completed raid PGCR, inspect teammates.
  - For each teammate, find their earliest completed raid.
  - Count teammates whose first completed raid is that same PGCR.
- This is expensive, privacy-limited, and only proves "first clear together", not an actual carry.

## Not Found / Probably Not Exposed

- Emotes used.
  - No historical stat or metric found so far.
- Total distance ran / traveled.
  - Historical stats only expose combat distance stats such as `averageKillDistance`, `averageDeathDistance`, `totalKillDistance`, `totalDeathDistance`, and `longestKillDistance`.
- Exact orbit time.
  - Only approximations from total character time minus activity time.
- True play sessions.
  - No direct session stat found.
  - Must infer from activity history by stitching activities together.
  - Sort activities by `period`, estimate end time from `activityDurationSeconds` or `timePlayedSeconds`, and merge adjacent activities when the gap is below a chosen threshold.
  - This is an approximation and depends heavily on the selected gap threshold.
- True head-to-head K/D against a specific Crucible opponent.
  - PGCR exposes participants and aggregate stats, but not kill attribution.
  - Can infer "K/D in games against this opponent", but not "kills/deaths caused by this opponent".
- Exact weapon time-used / time-wielded.
  - Weapon lists expose weapon stat values, but no confirmed "time held" stat.
  - Use weapon kills as the practical "most used" definition.
- Retroactive time spent on each subclass.
  - Current subclass is available from live equipment because subclasses are inventory items.
  - Historical activities/PGCRs expose character class (`classHash` / `characterClass`) but not equipped subclass.
  - Bungie does not expose historical equipment snapshots for old activity completions.
  - Could only be tracked prospectively by taking local snapshots over time, starting after the app begins tracking.
- Carries.
  - Must infer, and the inference is not authoritative.
