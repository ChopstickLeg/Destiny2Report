namespace D2Report.BungieClient;

public partial interface ID2ReportClient
{
    Task<Response41> Destiny2_GetDestinyEntityDefinitionAsync(string entityType, string hashIdentifier, CancellationToken cancellationToken);
}

public partial class D2ReportClient
{
    private const string PgcrHost = "stats.bungie.net";
    private const string PgcrPathPrefix = "/Platform/Destiny2/Stats/PostGameCarnageReport/";
    private const string PgcrReportPathSuffix = "/Report/";

    public virtual async Task<Response41> Destiny2_GetDestinyEntityDefinitionAsync(
        string entityType,
        string hashIdentifier,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entityType);
        ArgumentNullException.ThrowIfNull(hashIdentifier);

        using var request = new HttpRequestMessage();
        request.Method = HttpMethod.Get;
        request.Headers.Accept.Add(System.Net.Http.Headers.MediaTypeWithQualityHeaderValue.Parse("application/json"));

        var urlBuilder = new System.Text.StringBuilder();
        if (!string.IsNullOrEmpty(_baseUrl))
        {
            urlBuilder.Append(_baseUrl);
        }

        urlBuilder.Append("Destiny2/Manifest/");
        urlBuilder.Append(Uri.EscapeDataString(entityType));
        urlBuilder.Append('/');
        urlBuilder.Append(Uri.EscapeDataString(hashIdentifier));
        urlBuilder.Append('/');

        PrepareRequest(_httpClient, request, urlBuilder);

        var url = urlBuilder.ToString();
        request.RequestUri = new Uri(url, UriKind.RelativeOrAbsolute);

        PrepareRequest(_httpClient, request, url);

        var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var headers = new Dictionary<string, IEnumerable<string>>();
            foreach (var item in response.Headers)
            {
                headers[item.Key] = item.Value;
            }

            if (response.Content?.Headers is not null)
            {
                foreach (var item in response.Content.Headers)
                {
                    headers[item.Key] = item.Value;
                }
            }

            ProcessResponse(_httpClient, response);

            var status = (int)response.StatusCode;
            if (status == 200)
            {
                var objectResponse = await ReadObjectResponseAsync<Response41>(response, headers, cancellationToken)
                    .ConfigureAwait(false);
                return objectResponse.Object
                    ?? throw new ApiException("Response was null which was not expected.", status, objectResponse.Text, headers, null);
            }

            var responseData = response.Content is null
                ? null
                : await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new ApiException("The HTTP status code of the response was not expected (" + status + ").", status, responseData, headers, null);
        }
        finally
        {
            response.Dispose();
        }
    }

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
