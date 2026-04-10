#nullable enable
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Linq;

public sealed class FirecrawlClient : IScraperClient
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

    private const string PromptTrackingMinimal = @"
Retorne SOMENTE JSON válido:
{
  ""origin"": string,
  ""destination"": string,
  ""last_flight"": string,
  ""last_status_code"": string
}

Regras:
- last_status_code só pode ser: BKD, FBW, RCS, RCT, MAN, FFM, DEP, LOF, TFD, AWD, ARR, DLH, NFD, RCF, DLV, POD, UNK
- origin/destination preferencialmente IATA 3 letras
- last_flight tipo LA1234 ou N/A
- Se não encontrar um campo, use N/A
";

    public Task<TrackingDetails> ScrapeAsync(string awb)
        => ScrapeInternalAsync(awb, PromptTrackingMinimal, includeTimeline: true);

    private async Task<TrackingDetails> ScrapeInternalAsync(string awb, string prompt, bool includeTimeline)
    {
        var url = $"https://parcelsapp.com/en/tracking/{Uri.EscapeDataString(awb)}";
        var endpoint = $"{_baseUrl}/scrape";

        Console.WriteLine($"[DIAG] AWB={awb} | URL={url} | Endpoint={endpoint}");

        var payload = new
        {
            url,
            maxAge = 0,
            timeout = 120000,
            waitFor = 10000,
            onlyMainContent = false,
            actions = new object[]
            {
                new { type = "wait", milliseconds = 2000 },
                new { type = "scroll", direction = "down", amount = 800 },
                new { type = "wait", milliseconds = 1500 },
                new { type = "scroll", direction = "down", amount = 1200 },
                new { type = "wait", milliseconds = 1500 },
                new { type = "scroll", direction = "down", amount = 1500 },
                new { type = "wait", milliseconds = 2000 },
                new { type = "scroll", direction = "up", amount = 1000 },
                new { type = "wait", milliseconds = 1000 }
            },
            formats = new object[]
            {
                "markdown",
                "html",
                "rawHtml",
                new 
                { 
                    type = "json",
                    prompt = prompt
                }
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
            Utils.WriteDiagFile(awb, "01_raw_body.json.txt", body);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DIAG] HTTP request failed: {ex.GetType().Name}: {ex.Message}");
            return TrackingDetails.Empty(awb, $"Firecrawl request failed: {ex.GetType().Name}: {ex.Message}");
        }

        Console.WriteLine($"[DIAG] HTTP Status: {(int)resp.StatusCode} {resp.StatusCode}");

        Console.WriteLine("[DIAG] === RAW BODY ===");
        Console.WriteLine(body.Length > 2000 ? body.Substring(0, 2000) + "\n...(truncado)" : body);

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
                Console.WriteLine("[DIAG] Firecrawl retornou success=false ou campo ausente.");
                var shortBody = body.Length > 900 ? body.Substring(0, 900) + "..." : body;
                return TrackingDetails.Empty(awb, $"Firecrawl success=false: {shortBody}");
            }

            if (!doc.RootElement.TryGetProperty("data", out var data))
            {
                Console.WriteLine("[DIAG] Campo 'data' não encontrado na resposta.");
                return TrackingDetails.Empty(awb, "Firecrawl response sem campo 'data'.");
            }

            Console.WriteLine("[DIAG] === DATA ELEMENT ===");
            var rawData = data.GetRawText();
            Utils.WriteDiagFile(awb, "02_data_raw.json.txt", rawData);
            Console.WriteLine(rawData.Length > 12000 ? rawData.Substring(0, 12000) + "\n...(truncado)" : rawData);

            var markdown = TryExtractMarkdown(data);
            if (!string.IsNullOrWhiteSpace(markdown) &&
                markdown.Contains("No information about your package", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"[DIAG] AWB={awb} | ParcelsApp retornou página genérica sem tracking.");
                return TrackingDetails.Empty(awb, "ParcelsApp retornou página genérica sem tracking.");
            }

            if (!string.IsNullOrWhiteSpace(markdown) &&
                markdown.Contains("Nenhuma informação sobre o seu pacote", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"[DIAG] AWB={awb} | ParcelsApp retornou página genérica sem tracking.");
                return TrackingDetails.Empty(awb, "ParcelsApp retornou página genérica sem tracking.");
            }
            var rawHtml = TryExtractRawHtml(data);
    Utils.WriteDiagFile(awb, "03_markdown.md", markdown);
Utils.WriteDiagFile(awb, "04_rawHtml.html", rawHtml);
            Console.WriteLine("[DIAG] === MARKDOWN EXTRAÍDO ===");
            if (string.IsNullOrWhiteSpace(markdown))
                Console.WriteLine("(null ou vazio)");
            else
                Console.WriteLine(markdown.Length > 2000 ? markdown.Substring(0, 2000) + "\n...(truncado)" : markdown);

            Console.WriteLine("[DIAG] === RAWHTML EXTRAÍDO ===");
            if (string.IsNullOrWhiteSpace(rawHtml))
                Console.WriteLine("(null ou vazio)");
            else
                Console.WriteLine(rawHtml.Length > 2000 ? rawHtml.Substring(0, 2000) + "\n...(truncado)" : rawHtml);

            string? extractedJson = TryExtractJsonString(data);

            Console.WriteLine("[DIAG] === EXTRACTED JSON (string) ===");
            Console.WriteLine(string.IsNullOrWhiteSpace(extractedJson) ? "(null ou vazio)" : extractedJson);

            if (string.IsNullOrWhiteSpace(extractedJson))
            {
                Console.WriteLine("[DIAG] extractedJson vazio, tentando TryExtractJsonObject...");

                var obj = TryExtractJsonObject(data);
                if (obj.HasValue)
                {
                    Console.WriteLine("[DIAG] === JSON OBJECT EXTRAÍDO ===");
                    Console.WriteLine(obj.Value.GetRawText());

                    var result = MapToTrackingDetails(awb, obj.Value, includeTimeline);
                    Utils.WriteDiagFile(awb, "05_result_summary.txt",
    $"AWB={awb}\nOrigin={result.Origin}\nDestination={result.Destination}\nFlight={result.LastFlight}\nStatus={result.LastStatusCode}\nTimelineCount={(result.Timeline == null ? 0 : result.Timeline.Count)}\nError={result.Error}");

                    if (includeTimeline)
                    {
                        result.Timeline = ExtractTimelinePreferRawHtml(rawHtml, markdown);

                        var timelineStatus = TrackingStatusMapper.GetLastStatusCode(result.Timeline);

                        if (!string.IsNullOrWhiteSpace(timelineStatus) && timelineStatus != "UNK")
                            result.LastStatusCode = timelineStatus;

                        Console.WriteLine($"[DIAG] Timeline parseada: {result.Timeline.Count} evento(s).");
                    }

                    Console.WriteLine($"[DIAG] TrackingDetails final => Origin={result.Origin} | Destination={result.Destination} | Flight={result.LastFlight} | Status={result.LastStatusCode}");

                    return result;
                }

                Console.WriteLine("[DIAG] Nenhum JSON extraível encontrado. Utilizando fallback para buscar a Timeline via HTML/Markdown.");
                
                var fallbackResult = new TrackingDetails
                {
                    Awb = awb,
                    Origin = "N/A",
                    Destination = "N/A",
                    LastFlight = "N/A",
                    LastStatusCode = "N/A",
                    Source = "ParcelsApp",
                    Error = "",
                    Timeline = new List<TrackingEvent>()
                };

                if (includeTimeline)
                {
                    fallbackResult.Timeline = ExtractTimelinePreferRawHtml(rawHtml, markdown);

                    var timelineStatus = TrackingStatusMapper.GetLastStatusCode(fallbackResult.Timeline);

                    if (!string.IsNullOrWhiteSpace(timelineStatus) && timelineStatus != "UNK")
                        fallbackResult.LastStatusCode = timelineStatus;

                    Console.WriteLine($"[DIAG] Timeline parseada (fallback): {fallbackResult.Timeline.Count} evento(s).");
                }

                return fallbackResult;
            }

            extractedJson = StripCodeFences(extractedJson.Trim());
            Console.WriteLine("[DIAG] === EXTRACTED JSON (após StripCodeFences) ===");
            Console.WriteLine(extractedJson);

            using var inner = JsonDocument.Parse(extractedJson);
            var parsed = MapToTrackingDetails(awb, inner.RootElement, includeTimeline);

            if (includeTimeline)
            {
                parsed.Timeline = ExtractTimelinePreferRawHtml(rawHtml, markdown);

                var timelineStatus = TrackingStatusMapper.GetLastStatusCode(parsed.Timeline);

                if (!string.IsNullOrWhiteSpace(timelineStatus) && timelineStatus != "UNK")
                    parsed.LastStatusCode = timelineStatus;

                Console.WriteLine($"[DIAG] Timeline parseada: {parsed.Timeline.Count} evento(s).");
            }

            Console.WriteLine($"[DIAG] TrackingDetails final => Origin={parsed.Origin} | Destination={parsed.Destination} | Flight={parsed.LastFlight} | Status={parsed.LastStatusCode}");

            return parsed;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DIAG] Exceção no parse: {ex.GetType().Name}: {ex.Message}");
            var shortBody = body.Length > 900 ? body.Substring(0, 900) + "..." : body;
            return TrackingDetails.Empty(awb, $"Parse Firecrawl failed: {ex.GetType().Name}: {ex.Message} | body={shortBody}");
        }
    }

    private static List<TrackingEvent> ExtractTimelinePreferRawHtml(string? rawHtml, string? markdown)
    {
        if (!string.IsNullOrWhiteSpace(rawHtml))
        {
            var fromHtml = ParseTimelineFromRawHtml(rawHtml);
            if (fromHtml.Count > 0)
            {
                Console.WriteLine($"[DIAG] Timeline obtida via rawHtml: {fromHtml.Count} evento(s).");
                return fromHtml;
            }

            Console.WriteLine("[DIAG] rawHtml não gerou eventos válidos. Fallback para markdown.");
        }

        if (!string.IsNullOrWhiteSpace(markdown))
        {
            var fromMarkdown = ParseTimelineFromMarkdown(markdown);
            Console.WriteLine($"[DIAG] Timeline obtida via markdown: {fromMarkdown.Count} evento(s).");
            return fromMarkdown;
        }

        return new List<TrackingEvent>();
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
        if (data.TryGetProperty("formats", out var formats) && formats.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in formats.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;

                if (item.TryGetProperty("type", out var typeEl) &&
                    typeEl.ValueKind == JsonValueKind.String &&
                    string.Equals(typeEl.GetString(), "json", StringComparison.OrdinalIgnoreCase))
                {
                    if (item.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                        return content.GetString();

                    if (item.TryGetProperty("json", out var j) && j.ValueKind == JsonValueKind.String)
                        return j.GetString();
                }
            }
        }

        if (data.TryGetProperty("json", out var jsonEl) && jsonEl.ValueKind == JsonValueKind.String)
            return jsonEl.GetString();

        if (data.TryGetProperty("content", out var contentEl) && contentEl.ValueKind == JsonValueKind.String)
            return contentEl.GetString();

        return null;
    }

    private static JsonElement? TryExtractJsonObject(JsonElement data)
    {
        if (data.TryGetProperty("formats", out var formats) && formats.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in formats.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;

                if (item.TryGetProperty("type", out var typeEl) &&
                    typeEl.ValueKind == JsonValueKind.String &&
                    string.Equals(typeEl.GetString(), "json", StringComparison.OrdinalIgnoreCase) &&
                    item.TryGetProperty("json", out var j) &&
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
                }
            }
        }

        return null;
    }

    private static string? TryExtractRawHtml(JsonElement data)
    {
        if (data.TryGetProperty("rawHtml", out var rh) && rh.ValueKind == JsonValueKind.String)
            return rh.GetString();

        if (data.TryGetProperty("formats", out var formats) && formats.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in formats.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;

                if (item.TryGetProperty("type", out var typeEl) &&
                    typeEl.ValueKind == JsonValueKind.String &&
                    string.Equals(typeEl.GetString(), "rawHtml", StringComparison.OrdinalIgnoreCase))
                {
                    if (item.TryGetProperty("content", out var contentEl) && contentEl.ValueKind == JsonValueKind.String)
                        return contentEl.GetString();
                }
            }
        }

        if (data.TryGetProperty("html", out var htmlEl) && htmlEl.ValueKind == JsonValueKind.String)
            return htmlEl.GetString();

        return null;
    }

    private static List<TrackingEvent> ParseTimelineFromRawHtml(string rawHtml)
    {
        var events = new List<TrackingEvent>();

        if (string.IsNullOrWhiteSpace(rawHtml))
            return events;

        var html = WebUtility.HtmlDecode(rawHtml);

        html = System.Text.RegularExpressions.Regex.Replace(
            html,
            @"<script[\s\S]*?</script>",
            " ",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        html = System.Text.RegularExpressions.Regex.Replace(
            html,
            @"<style[\s\S]*?</style>",
            " ",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        html = System.Text.RegularExpressions.Regex.Replace(
            html,
            @"<br\s*/?>",
            "\n",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        html = System.Text.RegularExpressions.Regex.Replace(
            html,
            @"</(div|p|li|section|article|tr|td|span|h1|h2|h3|h4|h5|h6)>",
            "\n",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        html = System.Text.RegularExpressions.Regex.Replace(html, @"<[^>]+>", " ");
        html = WebUtility.HtmlDecode(html);

        var lines = html
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(CleanupTextLine)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();

        Console.WriteLine($"[DIAG] ParseTimelineFromRawHtml: {lines.Count} linha(s) após limpeza.");

        var startIndex = TryFindFirstTimelineIndex(lines);
        if (startIndex < 0)
        {
            Console.WriteLine("[DIAG] ParseTimelineFromRawHtml: nenhum timestamp de timeline encontrado.");
            return events;
        }

        Console.WriteLine($"[DIAG] ParseTimelineFromRawHtml: timeline iniciando na linha {startIndex}: {lines[startIndex]}");

        var timelineLines = lines.Skip(startIndex).ToList();

        int i = 0;
        while (i < timelineLines.Count)
        {
            if (!LooksLikeTimestampLine(timelineLines[i]))
            {
                i++;
                continue;
            }

            var timestamp = timelineLines[i].Trim();
            i++;

            string description = "N/A";
            string location = "N/A";
            string carrier = "N/A";

            while (i < timelineLines.Count && !LooksLikeTimestampLine(timelineLines[i]))
            {
                var line = timelineLines[i];

                if (IsNoiseLine(line))
                {
                    i++;
                    continue;
                }

                if (description == "N/A" && LooksLikeEventDescription(line))
                {
                    description = line;
                    i++;
                    continue;
                }

                if (location == "N/A" && LooksLikeEventLocation(line))
                {
                    location = line;
                    i++;
                    continue;
                }

                if (carrier == "N/A" && LooksLikeEventCarrier(line))
                {
                    carrier = line;
                    i++;
                    continue;
                }

                i++;
            }

            Console.WriteLine($"[DIAG][TIMELINE EVENT][RAWHTML] Timestamp={timestamp} | Desc={description} | Loc={location} | Carrier={carrier}");

            events.Add(new TrackingEvent
            {
                Timestamp = timestamp,
                Description = description,
                Location = location,
                Carrier = carrier
            });
        }

        Console.WriteLine($"[DIAG] ParseTimelineFromRawHtml gerou {events.Count} evento(s).");

        return events
            .Where(e => !string.IsNullOrWhiteSpace(e.Timestamp))
            .OrderByDescending(e => ParseTimelineTimestamp(e.Timestamp) ?? DateTime.MinValue)
            .ToList();
    }

    private static string CleanupTextLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return "";

        line = line.Replace("\r", " ").Replace("\t", " ");
        line = WebUtility.HtmlDecode(line);
        line = System.Text.RegularExpressions.Regex.Replace(line, @"\s+", " ");
        return line.Trim();
    }

    private static int TryFindFirstTimelineIndex(List<string> lines)
    {
        for (int i = 0; i < lines.Count; i++)
        {
            if (LooksLikeTimestampLine(lines[i]))
            {
                var hasUsefulNextLines = false;

                for (int j = i + 1; j < Math.Min(i + 6, lines.Count); j++)
                {
                    if (LooksLikeLikelyTimelineContent(lines[j]))
                    {
                        hasUsefulNextLines = true;
                        break;
                    }
                }

                if (hasUsefulNextLines)
                    return i;
            }
        }

        return -1;
    }

    private static bool LooksLikeTimestampLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return false;

        return System.Text.RegularExpressions.Regex.IsMatch(
            line.Trim(),
            @"^\d{1,2}\s+[A-Za-zÀ-ÿ]{3}\s+\d{4}\s+\d{1,2}:\d{2}$");
    }

    private static bool LooksLikeLikelyTimelineContent(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return false;

        return LooksLikeEventDescription(line) ||
               LooksLikeEventLocation(line) ||
               LooksLikeEventCarrier(line);
    }

    private static bool IsNoiseLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return true;

        return line.Contains("Switch navigation", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("Track package", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("Track air cargo", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("Share to WhatsApp", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("Share to Telegram", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("Share to Viber", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("Bookmark this page", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("Tracking link", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("powered by", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("privacy", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("terms", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeEventDescription(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return false;

        return line.Contains("Departed", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("Arrived", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("Received from flight", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("Notified for delivery", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("Available for pickup", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("Documents available", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("Delivered", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("Proof of delivery", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("Manifested", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("Flight", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeEventLocation(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return false;

        return line.Contains("(") ||
               line.Contains(",") ||
               line.Contains("Germany", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("Brazil", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("Brasil", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("Frankfurt", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("Guarulhos", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("São Paulo", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("Sao Paulo", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeEventCarrier(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return false;

        return line.Contains("LATAM", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("LUFTHANSA", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("EMIRATES", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("QATAR", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("CARGO", StringComparison.OrdinalIgnoreCase);
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
            @"^- \*\*(?<date>\d{1,2}\s+[A-Za-zÀ-ÿ]{3}\s+\d{4})\*\*\s+(?<time>\d{1,2}:\d{2})$",
            System.Text.RegularExpressions.RegexOptions.Compiled);

        var descLocRegex = new System.Text.RegularExpressions.Regex(
            @"^\*\*(?<desc>.+?)\*\*\s+(?<loc>.+)$",
            System.Text.RegularExpressions.RegexOptions.Compiled);

        int matchCount = 0;

        for (int i = 0; i < lines.Count; i++)
        {
            var headerMatch = headerRegex.Match(lines[i]);
            if (!headerMatch.Success)
            {
                Console.WriteLine($"[DIAG][REGEX NO MATCH] linha {i}: {lines[i]}");
                continue;
            }

            matchCount++;
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
                    Console.WriteLine($"[DIAG][REGEX DESC NO MATCH] linha {i + 1}: {lines[i + 1]}");
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

            Console.WriteLine($"[DIAG][TIMELINE EVENT][MD] Timestamp={timestamp} | Desc={description} | Loc={location} | Carrier={carrier}");

            events.Add(new TrackingEvent
            {
                Timestamp = timestamp,
                Description = description,
                Location = location,
                Carrier = carrier
            });
        }

        Console.WriteLine($"[DIAG] ParseTimelineFromMarkdown: {matchCount} header(s) casado(s), {events.Count} evento(s) gerado(s).");

        return events
            .OrderByDescending(e => ParseTimelineTimestamp(e.Timestamp) ?? DateTime.MinValue)
            .ToList();
    }

    private static string NormalizeTimelineTimestamp(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return "";

        input = input.Trim();

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Jan"] = "Jan",
            ["Fev"] = "Feb",
            ["Mar"] = "Mar",
            ["Abr"] = "Apr",
            ["Mai"] = "May",
            ["Jun"] = "Jun",
            ["Jul"] = "Jul",
            ["Ago"] = "Aug",
            ["Set"] = "Sep",
            ["Out"] = "Oct",
            ["Nov"] = "Nov",
            ["Dez"] = "Dec"
        };

        var parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 4)
        {
            var month = parts[1].Trim().TrimEnd('.');
            if (map.TryGetValue(month, out var normalizedMonth))
                parts[1] = normalizedMonth;

            return string.Join(" ", parts);
        }

        return input;
    }

    private static DateTime? ParseTimelineTimestamp(string? timestamp)
    {
        if (string.IsNullOrWhiteSpace(timestamp))
            return null;

        timestamp = NormalizeTimelineTimestamp(timestamp);

        var formats = new[]
        {
        "dd MMM yyyy HH:mm",
        "d MMM yyyy HH:mm",
        "dd MMM yyyy",
        "d MMM yyyy"
    };

        if (DateTime.TryParseExact(
                timestamp,
                formats,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AllowWhiteSpaces | System.Globalization.DateTimeStyles.AssumeLocal,
                out var parsed))
        {
            return parsed;
        }

        Console.WriteLine($"[DIAG] ParseTimelineTimestamp falhou para: '{timestamp}'");
        return null;
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
            Origin = GetString("origin"),
            Destination = GetString("destination"),
            LastFlight = GetString("last_flight"),
            LastStatusCode = GetString("last_status_code"),
            Source = "ParcelsApp",
            Error = "",
            Timeline = new List<TrackingEvent>()
        };

        if (includeTimeline &&
            root.TryGetProperty("timeline", out var tl) &&
            tl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in tl.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;

                string GetTimelineString(string prop)
                    => item.TryGetProperty(prop, out var e) && e.ValueKind == JsonValueKind.String
                        ? (e.GetString() ?? "N/A")
                        : "N/A";

                r.Timeline!.Add(new TrackingEvent
                {
                    Timestamp = GetTimelineString("timestamp"),
                    Description = GetTimelineString("description"),
                    Location = GetTimelineString("location"),
                    Carrier = GetTimelineString("carrier")
                });
            }
        }

        r.Origin = string.IsNullOrWhiteSpace(r.Origin) ? "N/A" : r.Origin.Trim();
        r.Destination = string.IsNullOrWhiteSpace(r.Destination) ? "N/A" : r.Destination.Trim();
        r.LastFlight = string.IsNullOrWhiteSpace(r.LastFlight) ? "N/A" : r.LastFlight.Trim();
        r.LastStatusCode = string.IsNullOrWhiteSpace(r.LastStatusCode) ? "N/A" : r.LastStatusCode.Trim().ToUpperInvariant();
        r.Source = "ParcelsApp";

        return r;
    }
}