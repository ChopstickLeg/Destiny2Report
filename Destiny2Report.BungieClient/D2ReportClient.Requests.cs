namespace D2Report.BungieClient;

public partial class D2ReportClient
{
    private const string PgcrHost = "stats.bungie.net";
    private const string PgcrPathPrefix = "/Platform/Destiny2/Stats/PostGameCarnageReport/";
    private const string PgcrReportPathSuffix = "/Report/";

    partial void PrepareRequest(HttpClient client, HttpRequestMessage request, System.Text.StringBuilder urlBuilder)
    {
        if (!Uri.TryCreate(urlBuilder.ToString(), UriKind.Absolute, out var uri)
            || !IsPostGameCarnageReportRequest(uri))
        {
            return;
        }

        var rewritten = new UriBuilder(uri)
        {
            Host = PgcrHost
        };

        urlBuilder.Clear();
        urlBuilder.Append(rewritten.Uri.AbsoluteUri);
    }

    private static bool IsPostGameCarnageReportRequest(Uri uri)
    {
        return uri.Host.Equals("www.bungie.net", StringComparison.OrdinalIgnoreCase)
            && uri.AbsolutePath.StartsWith(PgcrPathPrefix, StringComparison.OrdinalIgnoreCase)
            && !uri.AbsolutePath.EndsWith(PgcrReportPathSuffix, StringComparison.OrdinalIgnoreCase);
    }
}
