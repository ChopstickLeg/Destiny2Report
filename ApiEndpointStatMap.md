# API Endpoint Stat Map

This only covers requested statistics that are pullable from Bungie's API or reasonably derivable from API data.

## Manifest / Static Definitions

### `GET /Platform/Destiny2/Manifest/`

Use this first to discover the current manifest paths.

- Needed for nearly every display-name lookup.
- Read `Response.jsonWorldComponentContentPaths["en"]`.
- Download the relevant definition JSON from `https://www.bungie.net{path}`.

Definition tables needed:

- `DestinyActivityDefinition`
  - resolves activity hashes from history/PGCRs
  - provides `destinationHash`, `placeHash`, `activityModeTypes`, `directActivityModeType`, `isPvP`
- `DestinyActivityModeDefinition`
  - resolves activity mode names and parent mode relationships
- `DestinyDestinationDefinition`
  - resolves patrol destination names
- `DestinyPlaceDefinition`
  - optional broader destination/place grouping
- `DestinyInventoryItemDefinition`
  - resolves weapon hashes from PGCR weapon stats
- `DestinyMetricDefinition`
  - finds Good Boy Protocol / Archie pet and fish caught metric hashes
- `DestinyActivityModifierDefinition`
  - resolves Contest Mode modifier hashes
- `DestinyPresentationNodeDefinition`
  - resolves seals/title presentation nodes
- `DestinyRecordDefinition`
  - resolves title/seal record names and completion requirements

Supports:

- Titles / seals.
- Good Boy Protocol / Archie pet stat lookup.
- Fish caught stat lookup.
- Patrol time grouped by destination.
- Day 1 raid/dungeon activity matching.
- Contest Mode indicator.
- Top weapons display names.
- PvE / Crucible / Gambit classification support.

### `GET /Platform/Destiny2/Manifest/{entityType}/{hashIdentifier}/`

Use this only for one-off lookups when a hash is already known.

- Good for debugging a single activity, metric, modifier, weapon, destination, title node, or record.
- Not ideal for discovery/search; use the component JSON tables for that.

## Profile Data

### `GET /Platform/Destiny2/{membershipType}/Profile/{destinyMembershipId}/?components=200,900,1100`

Useful components:

- `200` Characters
- `900` ProfileRecords
- `1100` Metrics

Supports:

- Class playtime split setup.
  - Use characters to map character IDs to class.
  - Combine with historical `secondsPlayed` per character.
- Titles / seals.
  - Use profile record state from component `900`.
  - Resolve records and seal nodes from manifest definitions.
- Good Boy Protocol / Archie pet stat.
  - Use component `1100`, then `metrics.data.metrics[metricHash].objectiveProgress.progress`.
- Fish caught stat.
  - Same metrics flow as above.
- Approximate non-activity time.
  - Use character `minutesPlayedTotal`, then subtract historical activity seconds.

## Historical Stats

### `GET /Platform/Destiny2/{membershipType}/Account/{destinyMembershipId}/Stats/`

Generated client: `Destiny2_GetHistoricalStatsForAccountAsync`.

Supports:

- Total play time.
  - `mergedAllCharacters.merged.allTime["secondsPlayed"]`
  - optionally compare with `totalActivityDurationSeconds`
- Overall kill count.
  - `mergedAllCharacters.merged.allTime["kills"]`
  - optionally `opponentsDefeated`
- Misadventures.
  - `suicides`
- Class playtime split.
  - per-character `secondsPlayed`, mapped to class via profile characters
- Approximate non-activity time.
  - combine total character `minutesPlayedTotal` with historical `secondsPlayed`

Useful stat IDs:

- `secondsPlayed`
- `totalActivityDurationSeconds`
- `kills`
- `opponentsDefeated`
- `suicides`

### `GET /Platform/Destiny2/{membershipType}/Account/{destinyMembershipId}/Character/{characterId}/Stats/?modes={mode}`

Generated client: `Destiny2_GetHistoricalStatsAsync`.

Call once per character and mode bucket as needed.

Mode IDs:

- `5` AllPvP / Crucible
- `6` Patrol
- `7` AllPvE
- `63` Gambit
- `75` Gambit Prime legacy
- `4` Raid
- `82` Dungeon

Supports:

- PvE / Crucible / Gambit playtime split.
  - `secondsPlayed` by mode
- Crucible K/D and KDA.
  - mode `5`
  - `killsDeathsRatio`
  - `killsDeathsAssists`
- Gambit K/D and KDA.
  - mode `63`, plus `75` if needed
  - `killsDeathsRatio`
  - `killsDeathsAssists`
- Crucible wins and losses.
  - mode `5`
  - `activitiesWon`
  - `activitiesEntered`
  - `losses = activitiesEntered - activitiesWon`
- Gambit wins and losses.
  - mode `63`, plus `75` if needed
  - same wins/losses calculation

Useful stat IDs:

- `secondsPlayed`
- `activitiesEntered`
- `activitiesWon`
- `killsDeathsRatio`
- `killsDeathsAssists`
- `kills`
- `deaths`
- `assists`

### `GET /Platform/Destiny2/Stats/Definition/`

Generated client: `Destiny2_GetHistoricalStatsDefinitionAsync`.

Use for:

- validating historical stat IDs
- checking which modes support a stat
- discovering whether new stat IDs exist before hardcoding

Supports implementation for:

- historical playtime
- kill/death/assist stats
- wins/losses
- misadventures

## Activity History

### `GET /Platform/Destiny2/{membershipType}/Account/{destinyMembershipId}/Character/{characterId}/Stats/Activities/?mode={mode}&page={page}&count={count}`

Generated client: `Destiny2_GetActivityHistoryAsync`.

Call per character, paginating until exhausted or until the needed time range is covered.

Useful mode filters:

- `4` Raid
- `5` AllPvP / Crucible
- `6` Patrol
- `63` Gambit
- `75` Gambit Prime legacy
- `82` Dungeon

Supports:

- Patrol time grouped by destination.
  - mode `6`
  - use activity hash to resolve destination via `DestinyActivityDefinition`
- First raid completion.
  - mode `4`
  - then validate completion through PGCR
- First dungeon completion.
  - mode `82`
  - then validate completion through PGCR
- Day 1 raid completions.
  - mode `4`
  - compare activity `period` / end time to curated release windows
- Day 1 dungeon completions.
  - mode `82`
  - compare to curated release windows
- First raid flawless.
  - mode `4`
  - validate via PGCR deaths/completion
- First dungeon flawless.
  - mode `82`
  - validate via PGCR deaths/completion
- First dungeon solo flawless.
  - mode `82`
  - validate via PGCR deaths/completion/player count
- Zero-kill activities.
  - crawl all relevant modes, then validate via PGCR
- Most common 3-player fireteam combo.
  - crawl relevant 3-player activities, then group PGCR teammates
- Most common 6-player fireteam combo.
  - crawl relevant 6-player activities, then group PGCR teammates
- Unique players encountered.
  - crawl relevant activity history and collect PGCR participants
- Inferred first-raid carry / first-clear-with-teammate count.
  - crawl completed raid PGCRs, then inspect teammates and their first raid clears
- Crucible rival / most played opponent.
  - mode `5`, then inspect PGCR participants
- Crucible record against most played opponent.
  - mode `5`, then inspect PGCR team/player standing
- K/D in games where rival opponent was present.
  - mode `5`, then aggregate target player's PGCR kills/deaths in those games
- Gambit motes banked/lost.
  - mode `63`, plus `75` if needed, then inspect PGCR values
- Top 10 PvE / Crucible / Gambit weapons by weapon kills.
  - crawl histories for each bucket, then inspect PGCR extended weapon stats

Useful fields:

- `activities[].period`
- `activities[].activityDetails.instanceId`
- `activities[].activityDetails.referenceId`
- `activities[].activityDetails.directorActivityHash`
- `activities[].activityDetails.mode`
- `activities[].activityDetails.modes`
- `activities[].values`

## Post Game Carnage Report

### `GET /Platform/Destiny2/Stats/PostGameCarnageReport/{activityId}/`

Generated client: `Destiny2_GetPostGameCarnageReportAsync`.

Use `activityId` from `GetActivityHistory.activities[].activityDetails.instanceId`.

Supports:

- Zero-kill activities.
  - target entry `values["kills"] == 0`
- First raid completion.
  - target entry `values["completed"]`
  - optionally `completionReason`
- First dungeon completion.
  - same completion validation
- Day 1 raid/dungeon completion validation.
  - completion value plus `period` and duration
- Contest Mode indicator.
  - `selectedSkullHashes` intersects known Contest Mode modifier hashes
- First raid flawless.
  - completed and target/team deaths are `0`
- First dungeon flawless.
  - completed and target deaths are `0`
- First dungeon solo flawless.
  - completed, target deaths `0`, solo participant/fireteam validation
- Most common fireteam combo.
  - group entries on target player's team/fireteam
- Unique players encountered.
  - collect distinct `entry.player.destinyUserInfo`
- Inferred first-raid carry / first-clear-with-teammate count.
  - inspect raid teammates, then crawl their raid history where available
- Crucible rival / most played opponent.
  - collect opposing players from Crucible PGCRs
- Crucible record against most played opponent.
  - use `teams[].standing`, `entries[].standing`, and target/opponent team IDs
- K/D in games where rival opponent was present.
  - sum target `values["kills"]` and `values["deaths"]` across those PGCRs
- Gambit motes banked/lost.
  - inspect target `values`, `extended.values`, and `extended.scoreboardValues`
- Top 10 weapons by mode.
  - target `extended.weapons[].referenceId`
  - target `extended.weapons[].values`

Useful fields:

- `period`
- `activityWasStartedFromBeginning`
- `selectedSkullHashes`
- `activityDetails.instanceId`
- `activityDetails.referenceId`
- `activityDetails.directorActivityHash`
- `activityDetails.mode`
- `activityDetails.modes`
- `teams[].teamId`
- `teams[].standing`
- `teams[].score`
- `entries[].standing`
- `entries[].player.destinyUserInfo.membershipType`
- `entries[].player.destinyUserInfo.membershipId`
- `entries[].player.bungieNetUserInfo.membershipId`
- `entries[].characterId`
- `entries[].values["completed"]`
- `entries[].values["completionReason"]`
- `entries[].values["kills"]`
- `entries[].values["deaths"]`
- `entries[].values["assists"]`
- `entries[].values["timePlayedSeconds"]`
- `entries[].values["activityDurationSeconds"]`
- `entries[].extended.values`
- `entries[].extended.scoreboardValues`
- `entries[].extended.weapons[].referenceId`
- `entries[].extended.weapons[].values`

Stats that require PGCR value-key discovery before final hardcoding:

- Gambit motes banked.
- Gambit motes lost.
- Top weapon value key to use for "weapon kills" if it is not consistently named across payloads.

## Aggregate Activity Stats

### `GET /Platform/Destiny2/{membershipType}/Account/{destinyMembershipId}/Character/{characterId}/Stats/AggregateActivityStats/`

Generated client: `Destiny2_GetDestinyAggregateActivityStatsAsync`.

Optional helper endpoint.

Supports:

- PvE / Crucible / Gambit playtime split validation.
- Activity-type time aggregation.
- Patrol destination/activity sanity checks.

Use:

- `activities[].activityHash`
- `activities[].values["activitySecondsPlayed"]`
- resolve `activityHash` through `DestinyActivityDefinition`

This can reduce full PGCR crawling for activity-level totals, but it will not replace PGCRs for participant, rival, weapon, flawless, or motes details.

## Unique Weapon History

### `GET /Platform/Destiny2/{membershipType}/Account/{destinyMembershipId}/Character/{characterId}/Stats/UniqueWeapons/`

Generated client: `Destiny2_GetUniqueWeaponHistoryAsync`.

Supports:

- Fallback all-time weapon usage per character.
- Sanity-checking top weapon totals discovered via PGCR crawl.

Does not directly support:

- top 10 PvE weapons
- top 10 Crucible weapons
- top 10 Gambit weapons

Reason:

- the endpoint is not split by activity mode, so use PGCR extended weapon data for the requested mode-specific top 10s.

## External / Local Curated Data

Some derivations need a local table in addition to Bungie API responses.

Needed tables:

- raid release windows
- dungeon release windows
- Day 1 cutoff timestamps
- whether Contest Mode existed for the activity
- known Contest Mode modifier hash allowlist, regenerated from `DestinyActivityModifierDefinition`
- activity grouping rules for 3-player and 6-player fireteam combo leaderboards

Supports:

- Day 1 raid completions.
- Day 1 dungeon completions.
- Contest Mode classification fallback.
- Most common 3-player fireteam combo.
- Most common 6-player fireteam combo.
