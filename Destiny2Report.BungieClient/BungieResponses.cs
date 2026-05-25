namespace D2Report.BungieClient;

public interface BungieResponse
{
    int ErrorCode { get; }

    int ThrottleSeconds { get; }

    string ErrorStatus { get; }

    string Message { get; }

    IDictionary<string, string> MessageData { get; }

    string DetailedErrorTrace { get; }

    IDictionary<string, object> AdditionalProperties { get; }
}

public interface Response<out TResponse> : BungieResponse
{
    TResponse Response { get; }
}

public static class BungieResponseExtensions
{
    public static Response<TResponse> AsResponse<TResponse>(this Response<TResponse> response)
    {
        return response;
    }
}

public partial class Response : Response<ApiUsage>
{
    ApiUsage Response<ApiUsage>.Response => Response1;
}

public partial class Response2 : Response<ICollection<Application>> { }
public partial class Response3 : Response<GeneralUser> { }
public partial class Response4 : Response<IDictionary<string, string>> { }
public partial class Response5 : Response<ICollection<GetCredentialTypesForAccountResponse>> { }
public partial class Response6 : Response<ICollection<UserTheme>> { }
public partial class Response7 : Response<UserMembershipData> { }
public partial class Response8 : Response<HardLinkedUserMembership> { }
public partial class Response9 : Response<UserSearchResponse> { }
public partial class Response10 : Response<ContentTypeDescription> { }
public partial class Response11 : Response<ContentItemPublicContract> { }
public partial class Response12 : Response<SearchResultOfContentItemPublicContract> { }
public partial class Response13 : Response<object> { }
public partial class Response14 : Response<NewsArticleRssResponse> { }
public partial class Response15 : Response<PostSearchResponse> { }
public partial class Response16 : Response<long> { }
public partial class Response17 : Response<ICollection<TagResponse>> { }
public partial class Response18 : Response<ICollection<ForumRecruitmentDetail>> { }
public partial class Response19 : Response<IDictionary<string, string>> { }
public partial class Response20 : Response<ICollection<GroupTheme>> { }
public partial class Response21 : Response<bool> { }
public partial class Response22 : Response<ICollection<GroupV2Card>> { }
public partial class Response23 : Response<GroupSearchResponse> { }
public partial class Response24 : Response<GroupResponse> { }
public partial class Response25 : Response<ICollection<GroupOptionalConversation>> { }
public partial class Response26 : Response<int> { }
public partial class Response27 : Response<SearchResultOfGroupMember> { }
public partial class Response28 : Response<GroupMemberLeaveResult> { }
public partial class Response29 : Response<SearchResultOfGroupBan> { }
public partial class Response30 : Response<SearchResultOfGroupEditHistory> { }
public partial class Response31 : Response<SearchResultOfGroupMemberApplication> { }
public partial class Response32 : Response<ICollection<EntityActionResult>> { }
public partial class Response33 : Response<GetGroupsForMemberResponse> { }
public partial class Response34 : Response<GroupMembershipSearchResponse> { }
public partial class Response35 : Response<GroupPotentialMembershipSearchResponse> { }
public partial class Response36 : Response<GroupApplicationResponse> { }
public partial class Response37 : Response<ICollection<PartnerOfferSkuHistoryResponse>> { }
public partial class Response38 : Response<PartnerRewardHistoryResponse> { }
public partial class Response39 : Response<IDictionary<string, BungieRewardDisplay>> { }
public partial class Response40 : Response<DestinyManifest> { }
public partial class Response41 : Response<DestinyDefinition> { }
public partial class Response42 : Response<ICollection<UserInfoCard>> { }
public partial class Response43 : Response<DestinyLinkedProfilesResponse> { }
public partial class Response44 : Response<DestinyProfileResponse> { }
public partial class Response45 : Response<DestinyCharacterResponse> { }
public partial class Response46 : Response<DestinyMilestone> { }
public partial class Response47 : Response<ClanBannerSource> { }
public partial class Response48 : Response<DestinyItemResponse> { }
public partial class Response49 : Response<DestinyVendorsResponse> { }
public partial class Response50 : Response<DestinyVendorResponse> { }
public partial class Response51 : Response<DestinyPublicVendorsResponse> { }
public partial class Response52 : Response<DestinyCollectibleNodeDetailResponse> { }
public partial class Response53 : Response<DestinyEquipItemResults> { }
public partial class Response54 : Response<DestinyItemChangeResponse> { }
public partial class Response55 : Response<DestinyPostGameCarnageReportData> { }
public partial class Response56 : Response<IDictionary<string, DestinyHistoricalStatsDefinition>> { }
public partial class Response57 : Response<IDictionary<string, IDictionary<string, DestinyLeaderboard>>> { }
public partial class Response58 : Response<ICollection<DestinyClanAggregateStat>> { }
public partial class Response59 : Response<DestinyEntitySearchResult> { }
public partial class Response60 : Response<IDictionary<string, DestinyHistoricalStatsByPeriod>> { }
public partial class Response61 : Response<DestinyHistoricalStatsAccountResult> { }
public partial class Response62 : Response<DestinyActivityHistoryResults> { }
public partial class Response63 : Response<DestinyHistoricalWeaponStatsData> { }
public partial class Response64 : Response<DestinyAggregateActivityResults> { }
public partial class Response65 : Response<DestinyMilestoneContent> { }
public partial class Response66 : Response<IDictionary<string, DestinyPublicMilestone>> { }
public partial class Response67 : Response<AwaInitializeResponse> { }
public partial class Response68 : Response<AwaAuthorizationResult> { }
public partial class Response69 : Response<TrendingCategories> { }
public partial class Response70 : Response<SearchResultOfTrendingEntry> { }
public partial class Response71 : Response<TrendingDetail> { }
public partial class Response72 : Response<SearchResultOfFireteamSummary> { }
public partial class Response73 : Response<SearchResultOfFireteamResponse> { }
public partial class Response74 : Response<FireteamResponse> { }
public partial class Response75 : Response<BungieFriendListResponse> { }
public partial class Response76 : Response<BungieFriendRequestListResponse> { }
public partial class Response77 : Response<PlatformFriendResponse> { }
public partial class Response78 : Response<IDictionary<string, string>> { }
public partial class Response79 : Response<CoreSettingsConfiguration> { }
public partial class Response80 : Response<IDictionary<string, CoreSystem>> { }
public partial class Response81 : Response<ICollection<GlobalAlert>> { }
