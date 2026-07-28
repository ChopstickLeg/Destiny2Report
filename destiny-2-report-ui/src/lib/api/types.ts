/**
 * Transport types matching the ASP.NET Core API's JSON output (camelCase
 * web defaults). These mirror the wire format; view models and editorial
 * logic live in feature code, not here.
 *
 * Identity caveat: Destiny membership IDs and activity instance IDs are
 * 64-bit integers that exceed Number.MAX_SAFE_INTEGER. The HTTP client
 * rewrites those specific fields into strings before JSON.parse (see
 * http.ts), so they are typed as `string` here.
 */

// ---------------------------------------------------------------------------
// Player search
// ---------------------------------------------------------------------------

export interface PlayerSearchResult {
  displayName: string
  displayCode: number | null
  membershipId: string
  membershipTypeId: number
  emblemIconUrl: string
}

// ---------------------------------------------------------------------------
// Full report
// ---------------------------------------------------------------------------

export type CrawlState = 'queued' | 'running' | 'completed' | 'failed' | 'private'

export interface CharacterPlaytimeReport {
  race: string // "Human" | "Awoken" | "Exo" | "Unknown"
  class: string // "Titan" | "Hunter" | "Warlock" | "Unknown"
  isDeleted: boolean
  playtime: string // .NET TimeSpan
}

export interface PlaytimeStreakReport {
  startDate: string
  endDate: string
}

interface PvpPlaylistReport {
  mode: number
  modeName: string
  wins: number
  losses: number
  matches: number
  winRate: number // fraction of 1, pre-rounded by the backend
}

interface ActivityCompletionMark {
  completedAt: string
  instanceId: string
}

interface ActivityFastestCompletion {
  duration: string // .NET TimeSpan
  completedAt: string
  instanceId: string
}

export interface ActivityCompletionSummary {
  activityName: string
  activityCount: number
  completionCount: number
  clearRate: number // fraction of 1, pre-rounded by the backend
  firstCompletion: ActivityCompletionMark | null
  lastCompletion: ActivityCompletionMark | null
  fastestCompletion: ActivityFastestCompletion | null
  contestClear: boolean
  flawlessClear: boolean
  soloClear: boolean
  soloFlawlessClear: boolean
}

interface CrucibleKillsReport {
  total: number
  byMode: Record<string, number>
}

interface GambitMoteStatReport {
  total: number
  byMode: Record<string, number>
}

interface GambitMotesReport {
  matches: number
  motesBanked: GambitMoteStatReport
  motesLost: GambitMoteStatReport
  motesDenied: GambitMoteStatReport
  averageMotesBanked: number
  averageMotesLost: number
}

interface TriumphSeal {
  name: string
  description: string
  iconUrl: string
  isCompleted: boolean
}

interface DestinyPlayerRef {
  membershipId: string
  membershipType: number
  displayName: string
  emblemUrl: string
}

interface PlayerEncounterReport {
  player: DestinyPlayerRef
  encounterCount: number
}

interface SherpaReport {
  raidName: string
  playerCount: number
}

interface EmblemReport {
  name: string
  iconUrl: string
  backgroundUrl: string
  totalPlaytime: string // .NET TimeSpan
}

export interface DestinyReport {
  platformId: number
  playerMembershipId: string
  displayName: string
  displayCode: number
  fullDisplayName: string
  crawledAt: string
  firstActivityAtUtc: string | null
  crawlState: CrawlState
  queuedInRedis: boolean
  queuedAtUtc: string | null
  startedAtUtc: string | null
  lastCrawledAtUtc: string | null
  hasCompletedCrawl: boolean
  crawlError: string
  needsFullRecrawl: boolean
  fullRecrawlReason: string
  totalPlaytime: string
  characterPlaytime: CharacterPlaytimeReport[]
  patrolTimeByPlanet: Record<string, string>
  goodBoyProtocol: number
  fishCaught: number
  totalKills: number
  crucibleKd: number
  crucibleKda: number
  gambitKd: number
  gambitKda: number
  crucibleMatchesPlayed: number
  gambitMatchesPlayed: number
  crucibleWins: number
  gambitWins: number
  gambitPlaylists: PvpPlaylistReport[]
  crucibleKills: CrucibleKillsReport
  gambitMotes: GambitMotesReport
  triumphSeals: TriumphSeal[]
  misadventures: number
  zeroKillActivities: number
  totalActivityTime: string
  longestPlaytimeStreak: PlaytimeStreakReport | null
  currentPlaytimeStreak: PlaytimeStreakReport | null
  pvpPlaylists: PvpPlaylistReport[]
  raidCompletions: ActivityCompletionSummary[]
  dungeonCompletions: ActivityCompletionSummary[]
  conquestCompletions: ActivityCompletionSummary[]
  mostPlayedWith: PlayerEncounterReport[]
  uniquePlayersPlayedWith: number
  playersSherpaed: SherpaReport[]
  mostUsedEmblems: EmblemReport[]
}

// ---------------------------------------------------------------------------
// Leaderboards
// ---------------------------------------------------------------------------

export interface LeaderboardDefinition {
  key: string
  category: string
  title: string
  description: string
  unit: 'count' | 'seconds' | 'days'
  displayOrder: number
  rankedPlayerCount: number
  isRepairing: boolean
}

export interface LeaderboardCatalogResponse {
  isReady: boolean
  completedPlayerCount: number
  minimumCompletedPlayers: number
  leaderboards: LeaderboardDefinition[]
}

export interface LeaderboardEntry {
  rank: number
  membershipTypeId: number
  membershipId: string
  displayName: string
  displayCode: number
  fullDisplayName: string
  emblemBackgroundUrl: string
  score: number
}

export interface LeaderboardPageResponse {
  key: string
  category: string
  title: string
  description: string
  unit: 'count' | 'seconds' | 'days'
  offset: number
  limit: number
  retainedEntryCount: number
  updatedAtUtc: string
  isRepairing: boolean
  entries: LeaderboardEntry[]
}

export interface StoryVisualAssetsReport {
  raidIconUrl: string
  dungeonIconUrl: string
  crucibleIconUrl: string
  guidedGamesIconUrl: string
  contestRaidEmblems: ContestRaidEmblemAsset[]
  pantheonEmblems: PantheonEmblemAsset[]
  titanIconUrl: string
  hunterIconUrl: string
  warlockIconUrl: string
  goodBoyProtocolIconUrl: string
}

interface ContestRaidEmblemAsset {
  raidName: string
  emblemName: string
  iconUrl: string
}

interface PantheonEmblemAsset {
  pantheonName: string
  emblemName: string
  iconUrl: string
}

// ---------------------------------------------------------------------------
// Drill-down endpoints
// ---------------------------------------------------------------------------

/** Route value for the drill-down endpoints. */
export type ActivityModeParam = 'PvE' | 'PvP' | 'Gambit'

interface WeaponAggregateDetail {
  weaponKey: string
  weaponName: string
  referenceId: number // synthetic negatives: -1 grenade, -2 melee, -3 super
  iconUrl: string
  categoryKey: string
  categoryName: string
  kills: number
}

export interface WeaponCategoryAggregate {
  categoryKey: string
  categoryName: string
  kills: number
  weapons: WeaponAggregateDetail[]
}

interface WeaponModeAggregate {
  specificActivityMode: string
  categories: WeaponCategoryAggregate[]
}

interface WeaponClassAggregate {
  className: string // "Titan" | "Hunter" | "Warlock" | "Unknown"
  modes: WeaponModeAggregate[]
}

export interface WeaponActivityModeAggregateReport {
  activityMode: string // note: the API reports PvP as "Crucible" here
  classes: WeaponClassAggregate[]
}

interface DeathModeAggregate {
  specificActivityModeId: number
  specificActivityMode: string
  deaths: number
}

export interface DeathActivityModeAggregateReport {
  activityMode: string
  deaths: number
  modes: DeathModeAggregate[]
}

interface ActivityModePlaytimeBreakdown {
  mode: number
  modeName: string
  playtime: string // .NET TimeSpan
}

export interface ActivityPlaytimeAggregateReport {
  activityMode: string
  totalPlaytime: string // .NET TimeSpan
  modes: ActivityModePlaytimeBreakdown[]
}

// ---------------------------------------------------------------------------
// Report queue
// ---------------------------------------------------------------------------

export interface ReportQueueResponse {
  jobId: string
  membershipTypeId: number
  membershipId: string
  status: string
  queuedAtUtc: string
}

export type QueueStatus = CrawlState | 'not_found'

interface CrawlProgressSnapshot {
  phase: string
  label: string
  current: number | null
  total: number | null
  startedAtUtc: string
  updatedAtUtc: string
}

export interface ReportQueueStatusResponse {
  membershipTypeId: number
  membershipId: string
  status: QueueStatus
  streamEntryId: string | null
  error: string | null
  position: number | null
  queueLength: number
  updatedAtUtc: string
  progress: CrawlProgressSnapshot | null
}

// ---------------------------------------------------------------------------
// Auth
// ---------------------------------------------------------------------------

/** The one endpoint that serializes snake_case. */
interface BungieNetUser {
  membershipId: string
  uniqueName: string | null
  displayName: string | null
  profilePicturePath: string | null // relative Bungie path
  cachedBungieGlobalDisplayName: string | null
  cachedBungieGlobalDisplayNameCode: number | null
}

export interface DestinyMembership {
  membershipType: number
  membershipId: string
  displayName: string | null
  bungieGlobalDisplayName: string | null
  bungieGlobalDisplayNameCode: number | null
  iconPath: string | null // relative Bungie path
  crossSaveOverride: number
  applicableMembershipTypes: number[]
  isPublic: boolean
}

export interface SignedInPlayerResponse {
  signedIn: boolean
  bungieNetUser: BungieNetUser | null
  destinyMemberships: DestinyMembership[]
  primaryDestinyMembership: DestinyMembership | null
  isAdmin: boolean
}

// ---------------------------------------------------------------------------
// Admin
// ---------------------------------------------------------------------------

interface AdminActiveCrawl {
  membershipTypeId: number
  membershipId: string
  displayName: string | null
  queuedAtUtc: string | null
  startedAtUtc: string | null
  leaseExpiresAtUtc: string | null
  leaseOwner: string
  queuedInRedis: boolean
}

interface AdminQueueStatusCount {
  status: CrawlState
  count: number
}

export interface AdminOverview {
  updatedAtUtc: string
  activeCrawls: AdminActiveCrawl[]
  statusCounts: AdminQueueStatusCount[]
}

export interface AdminMutationResponse {
  affectedPlayers: number
  message: string
}
