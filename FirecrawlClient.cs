#nullable enable
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Linq;

public sealed class FirecrawlClient
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _proxy;
    private readonly int _timeoutMs;

    public FirecrawlClient(HttpClient http, string baseUrl, string proxy, int timeoutMs)
    {
        _http = http;
        _baseUrl = baseUrl.TrimEnd('/');
        _proxy = proxy;
        _timeoutMs = timeoutMs;
    }

    private const string PromptFast = @"
Retorne SOMENTE JSON válido:
{
  ""last_status_description"": string,
  ""last_status_timestamp"": string,
  ""last_flight"": string,
  ""origin"": string,
  ""destination"": string,
  ""last_status_code"": string
}

Regras:
- last_status_code só pode ser: BKD, FBW, RCS, RCT, MAN, FFM, DEP, LOF, TFD, AWD, ARR, DLH, NFD, RCF, DLV, POD, UNK
- Nunca invente códigos
- Nunca use parênteses
- Se não tiver certeza, use UNK
- origin/destination preferencialmente IATA 3 letras
- last_flight tipo LA1234 ou N/A
- Se não encontrar um campo, use N/A
";

    private const string PromptFull = @"
Retorne SOMENTE JSON válido:
{
  ""last_status_description"": string,
  ""last_status_timestamp"": string,
  ""last_flight"": string,
  ""origin"": string,
  ""destination"": string,
  ""last_status_code"": string
}

Regras:
- last_status_code só pode ser: BKD, FBW, RCS, RCT, MAN, FFM, DEP, LOF, TFD, AWD, ARR, DLH, NFD, RCF, DLV, POD, UNK
- Se não tiver certeza, use UNK
- Sem parênteses
- origin/destination preferencialmente IATA 3 letras
- last_flight tipo LA1234 ou N/A
- Se não encontrar um campo, use N/A
";

    public Task<TrackingDetails> ScrapeFastAsync(string awb)
        => ScrapeInternalAsync(awb, PromptFast, includeTimeline: false);

    public Task<TrackingDetails> ScrapeFullAsync(string awb)
        => ScrapeInternalAsync(awb, PromptFull, includeTimeline: true);

    private async Task<TrackingDetails> ScrapeInternalAsync(string awb, string prompt, bool includeTimeline)
    {
        var url = $"https://parcelsapp.com/pt/tracking/{Uri.EscapeDataString(awb)}";
        var endpoint = $"{_baseUrl}/scrape";

        var payload = new
        {
            url,
            proxy = _proxy,
            timeout = _timeoutMs,
            storeInCache = false,
            actions = new object[]
            {
                new { type = "wait", milliseconds = 2500 },
                new { type = "scroll", direction = "down", amount = 3000 },
                new { type = "wait", milliseconds = 1500 }
            },
            formats = new object[]
            {
                "markdown",
                new { type = "json", prompt }
            }
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };

        HttpResponseMessage resp;
        string body;

        try
        {
            resp = await _http.SendAsync(req);
            body = await resp.Content.ReadAsStringAsync();
        }
        catch (Exception ex)
        {
            return TrackingDetails.Empty(awb, $"Firecrawl request failed: {ex.GetType().Name}: {ex.Message}");
        }

        if (!resp.IsSuccessStatusCode)
        {
            var shortBody = body.Length > 900 ? body.Substring(0, 900) + "..." : body;
            return TrackingDetails.Empty(awb, $"Firecrawl HTTP {(int)resp.StatusCode}: {shortBody}");
        }

        try
        {
            using var doc = JsonDocument.Parse(body);

            if (!doc.RootElement.TryGetProperty("success", out var successEl) || successEl.ValueKind != JsonValueKind.True)
            {
                var shortBody = body.Length > 900 ? body.Substring(0, 900) + "..." : body;
                return TrackingDetails.Empty(awb, $"Firecrawl success=false: {shortBody}");
            }

            if (!doc.RootElement.TryGetProperty("data", out var data))
                return TrackingDetails.Empty(awb, "Firecrawl response sem campo 'data'.");

            var markdown = TryExtractMarkdown(data);
            string? extractedJson = TryExtractJsonString(data);

            if (string.IsNullOrWhiteSpace(extractedJson))
            {
                var obj = TryExtractJsonObject(data);
                if (obj.HasValue)
                {
                    var result = MapToTrackingDetails(awb, obj.Value, includeTimeline);

                    if (includeTimeline && !string.IsNullOrWhiteSpace(markdown))
                        result.Timeline = ParseTimelineFromMarkdown(markdown);

                    return result;
                }

                return TrackingDetails.Empty(awb, "Firecrawl não retornou JSON extraível.");
            }

            extractedJson = StripCodeFences(extractedJson.Trim());

            using var inner = JsonDocument.Parse(extractedJson);
            var parsed = MapToTrackingDetails(awb, inner.RootElement, includeTimeline);

            if (includeTimeline && !string.IsNullOrWhiteSpace(markdown))
                parsed.Timeline = ParseTimelineFromMarkdown(markdown);

            return parsed;
        }
        catch (Exception ex)
        {
            var shortBody = body.Length > 900 ? body.Substring(0, 900) + "..." : body;
            return TrackingDetails.Empty(awb, $"Parse Firecrawl failed: {ex.GetType().Name}: {ex.Message} | body={shortBody}");
        }
    }

    private static string NormalizeStatusCode(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "UNK";

        var s = raw.Trim().ToUpperInvariant();
        s = s.Replace("(", "").Replace(")", "").Trim();
        s = new string(s.Where(char.IsLetter).ToArray());

        return s switch
        {
            "BKD" => "BKD",
            "FBW" => "FBW",
            "RCS" => "RCS",
            "RCT" => "RCT",
            "MAN" => "MAN",
            "FFM" => "FFM",
            "DEP" => "DEP",
            "LOF" => "LOF",
            "TFD" => "TFD",
            "AWD" => "AWD",
            "ARR" => "ARR",
            "DLH" => "DLH",
            "NFD" => "NFD",
            "RCF" => "RCF",
            "DLV" => "DLV",
            "POD" => "POD",
            _ => "UNK"
        };
    }

    private static string StripCodeFences(string s)
    {
        s = s.Trim();
        if (s.StartsWith("```"))
        {
            var firstNewline = s.IndexOf('\n');
            if (firstNewline > 0) s = s[(firstNewline + 1)..];
            if (s.EndsWith("```")) s = s[..^3];
        }
        return s.Trim();
    }

    private static string? TryExtractJsonString(JsonElement data)
    {
        if (data.TryGetProperty("formats", out var formats) && formats.ValueKind == JsonValueKind.Array && formats.GetArrayLength() > 0)
        {
            foreach (var f0 in formats.EnumerateArray())
            {
                if (f0.ValueKind != JsonValueKind.Object)
                    continue;

                if (f0.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                {
                    // tenta detectar se é json
                    var text = content.GetString();
                    if (!string.IsNullOrWhiteSpace(text) && text.TrimStart().StartsWith("{"))
                        return text;
                }

                if (f0.TryGetProperty("json", out var j) && j.ValueKind == JsonValueKind.String)
                    return j.GetString();
            }
        }

        if (data.TryGetProperty("json", out var jsonEl) && jsonEl.ValueKind == JsonValueKind.String)
            return jsonEl.GetString();

        if (data.TryGetProperty("content", out var contentEl) && contentEl.ValueKind == JsonValueKind.String)
        {
            var text = contentEl.GetString();
            if (!string.IsNullOrWhiteSpace(text) && text.TrimStart().StartsWith("{"))
                return text;
        }

        return null;
    }

    private static JsonElement? TryExtractJsonObject(JsonElement data)
    {
        if (data.TryGetProperty("formats", out var formats) && formats.ValueKind == JsonValueKind.Array)
        {
            foreach (var f0 in formats.EnumerateArray())
            {
                if (f0.ValueKind == JsonValueKind.Object &&
                    f0.TryGetProperty("json", out var j) &&
                    j.ValueKind == JsonValueKind.Object)
                {
                    return j;
                }
            }
        }

        if (data.TryGetProperty("json", out var jsonEl) && jsonEl.ValueKind == JsonValueKind.Object)
            return jsonEl;

        return null;
    }

    private static string? TryExtractMarkdown(JsonElement data)
    {
        if (data.TryGetProperty("markdown", out var md) && md.ValueKind == JsonValueKind.String)
            return md.GetString();

        if (data.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
            return content.GetString();

        if (data.TryGetProperty("formats", out var formats) && formats.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in formats.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;

                if (item.TryGetProperty("type", out var typeEl) &&
                    typeEl.ValueKind == JsonValueKind.String &&
                    string.Equals(typeEl.GetString(), "markdown", StringComparison.OrdinalIgnoreCase))
                {
                    if (item.TryGetProperty("content", out var contentEl) && contentEl.ValueKind == JsonValueKind.String)
                        return contentEl.GetString();

                    if (item.TryGetProperty("markdown", out var mdEl) && mdEl.ValueKind == JsonValueKind.String)
                        return mdEl.GetString();
                }
            }
        }

        return null;
    }

    private static List<TrackingEvent> ParseTimelineFromMarkdown(string markdown)
    {
        var events = new List<TrackingEvent>();

        if (string.IsNullOrWhiteSpace(markdown))
            return events;

        var lines = markdown
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();

        var headerRegex = new System.Text.RegularExpressions.Regex(
            @"^- \*\*(?<date>\d{1,2}\s+[A-Za-z]{3}\s+\d{4})\*\*\s+(?<time>\d{1,2}:\d{2})$",
            System.Text.RegularExpressions.RegexOptions.Compiled);

        var descLocRegex = new System.Text.RegularExpressions.Regex(
            @"^\*\*(?<desc>.+?)\*\*\s+(?<loc>.+)$",
            System.Text.RegularExpressions.RegexOptions.Compiled);

        for (int i = 0; i < lines.Count; i++)
        {
            var headerMatch = headerRegex.Match(lines[i]);
            if (!headerMatch.Success)
                continue;

            var date = headerMatch.Groups["date"].Value.Trim();
            var time = headerMatch.Groups["time"].Value.Trim();
            var timestamp = $"{date} {time}";

            string description = "N/A";
            string location = "N/A";
            string carrier = "N/A";

            if (i + 1 < lines.Count)
            {
                var descLocMatch = descLocRegex.Match(lines[i + 1]);
                if (descLocMatch.Success)
                {
                    description = descLocMatch.Groups["desc"].Value.Trim();
                    location = descLocMatch.Groups["loc"].Value.Trim();
                }
                else
                {
                    description = lines[i + 1];
                }
            }

            if (i + 2 < lines.Count)
            {
                var candidateCarrier = lines[i + 2].Trim();

                if (!candidateCarrier.StartsWith("|") &&
                    !candidateCarrier.StartsWith("[") &&
                    !candidateCarrier.StartsWith("_") &&
                    !candidateCarrier.StartsWith("Tracking link", StringComparison.OrdinalIgnoreCase) &&
                    !headerRegex.IsMatch(candidateCarrier))
                {
                    carrier = candidateCarrier;
                }
            }

            events.Add(new TrackingEvent
            {
                Timestamp = timestamp,
                Description = description,
                Location = location,
                Carrier = carrier
            });
        }

        return events;
    }

    private static TrackingDetails MapToTrackingDetails(string awb, JsonElement root, bool includeTimeline)
    {
        string GetString(string name)
            => root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
                ? (el.GetString() ?? "N/A")
                : "N/A";

        var r = new TrackingDetails
        {
            Awb = awb,
            LastStatusDescription = GetString("last_status_description"),
            Timestamp = GetString("last_status_timestamp"),
            LastFlight = GetString("last_flight"),
            Origin = GetString("origin"),
            Destination = GetString("destination"),
            LastStatusCode = NormalizeStatusCode(GetString("last_status_code")),
            Error = "",
            Timeline = new List<TrackingEvent>()
        };

        r.LastStatusDescription = string.IsNullOrWhiteSpace(r.LastStatusDescription) ? "N/A" : r.LastStatusDescription.Trim();
        r.Timestamp = string.IsNullOrWhiteSpace(r.Timestamp) ? "N/A" : r.Timestamp.Trim();
        r.LastFlight = string.IsNullOrWhiteSpace(r.LastFlight) ? "N/A" : r.LastFlight.Trim();
        r.Origin = string.IsNullOrWhiteSpace(r.Origin) ? "N/A" : r.Origin.Trim();
        r.Destination = string.IsNullOrWhiteSpace(r.Destination) ? "N/A" : r.Destination.Trim();
        r.LastStatusCode = NormalizeStatusCode(r.LastStatusCode);

        if (!includeTimeline)
            r.Timeline = new List<TrackingEvent>();

        return r;
    }
}