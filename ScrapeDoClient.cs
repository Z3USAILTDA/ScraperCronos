#nullable enable
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Linq;

public sealed class ScrapeDoClient : IScraperClient
{
    private readonly HttpClient _http;
    private readonly string _token;
    private readonly int _timeoutMs;
    private readonly string _superMode;
    private readonly bool _render;
    private readonly string _waitUntil;
    private readonly int _customWaitMs;
    private readonly bool _returnJson;
    private readonly bool _showFrames;

    public ScrapeDoClient(
        HttpClient http,
        string token,
        bool render,
        string superMode,
        int timeoutMs,
        string waitUntil,
        int customWaitMs,
        bool returnJson,
        bool showFrames)
    {
        _http = http;
        _token = token;
        _superMode = superMode;
        _timeoutMs = timeoutMs;
        _render = render;
        _waitUntil = string.IsNullOrWhiteSpace(waitUntil) ? "networkidle2" : waitUntil;
        _customWaitMs = customWaitMs;
        _returnJson = returnJson;
        _showFrames = showFrames;
    }

    public async Task<TrackingDetails> ScrapeAsync(string awb)
    {
        // Adicionamos um Cache Buster agressivo com chave aleatória para evitar detecção de padrão
        var cbKey = "_" + Random.Shared.Next(1000, 9999);
        var cbVal = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var targetUrl = $"https://parcelsapp.com/pt/tracking/{Uri.EscapeDataString(awb)}?{cbKey}={cbVal}";
        var encodedTargetUrl = WebUtility.UrlEncode(targetUrl);

        // ParcelsApp mostra dados cacheados no render inicial.
        // Para forçar dados frescos, precisamos:
        // 1) Esperar o render inicial
        // 2) Clicar no botão "Tente novamente" (se existir) para forçar re-fetch da companhia aérea
        // 3) Esperar os dados frescos carregarem via AJAX
        var waitMs = _customWaitMs > 0 ? _customWaitMs : 10000;
        var browserActions = new object[]
        {
            new { Action = "Wait", Timeout = 8000 },                                          // Espera render inicial + AJAX
            new { Action = "Click", Selector = "a.btn.btn-default[href*='retry']" },           // Clica "Tente novamente" (link com retry)
            new { Action = "Wait", Timeout = 3000 },                                           // Pausa curta
            new { Action = "Click", Selector = "button.btn.btn-default" },                     // Fallback: clica botão genérico "Tente novamente"
            new { Action = "Wait", Timeout = waitMs },                                         // Espera dados frescos carregarem
        };

        var browserActionsJson = JsonSerializer.Serialize(browserActions);
        var encodedBrowserActions = WebUtility.UrlEncode(browserActionsJson);

        var requestUrl =
            $"https://api.scrape.do/?" +
            $"token={Uri.EscapeDataString(_token)}" +
            $"&url={encodedTargetUrl}" +
            $"&render={(_render ? "true" : "false")}" +
            $"&waitUntil={Uri.EscapeDataString(_waitUntil)}" +
            $"&output=markdown" +
            $"&customHeaders=true" +
            (_superMode.Equals("true", StringComparison.OrdinalIgnoreCase) ? "&super=true" : "") +
            $"&playWithBrowser={encodedBrowserActions}";

        string body;
        HttpResponseMessage resp;

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(_timeoutMs));
            using var req = new HttpRequestMessage(HttpMethod.Get, requestUrl);
            req.Headers.TryAddWithoutValidation("Accept-Language", "pt-BR,pt;q=0.9,en-US,en;q=0.8");
            req.Headers.TryAddWithoutValidation("Cache-Control", "no-cache");
            req.Headers.TryAddWithoutValidation("Pragma", "no-cache");
            req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/123.0.0.0 Safari/537.36");

            resp = await _http.SendAsync(req, cts.Token);
            body = await resp.Content.ReadAsStringAsync(cts.Token);
            Utils.WriteDiagFile(awb, "01_raw_body.md", body);
        }
        catch (Exception ex)
        {
            return TrackingDetails.Empty(awb, $"Scrape.do request failed: {ex.GetType().Name}: {ex.Message}");
        }

        Console.Error.WriteLine($"SCRAPEDO_REQUEST_URL={requestUrl}");
        Console.Error.WriteLine($"SCRAPEDO_HTTP={(int)resp.StatusCode}");
        Console.Error.WriteLine($"SCRAPEDO_BODY_LEN={body.Length}");

        if (!resp.IsSuccessStatusCode)
        {
            var shortBody = body.Length > 900 ? body.Substring(0, 900) + "..." : body;
            return TrackingDetails.Empty(awb, $"Scrape.do HTTP {(int)resp.StatusCode}: {shortBody}");
        }

        try
        {
            var cleaned = CleanupMarkdown(body);
            Utils.WriteDiagFile(awb, "02_cleaned.md", cleaned);

            Console.Error.WriteLine($"SCRAPEDO_CLEAN_LEN={cleaned.Length}");

            var timelineObj = ParseTimeline(cleaned);

            var result = new TrackingDetails
            {
                Awb = awb,
                Origin = FindOrigin(cleaned) ?? "N/A",
                Destination = FindDestination(cleaned) ?? "N/A",
                LastFlight = FindFlight(cleaned) ?? "N/A",
                LastStatusCode = NormalizeStatusCode(FindStatusCode(cleaned, timelineObj)),
                Source = "Scrape.do",
                Error = "",
                Timeline = timelineObj
            };

            // Se detectarmos mensagens de "sem informação", retornamos vazio para evitar lixo no DB
            if (cleaned.Contains("Nenhuma informação", StringComparison.OrdinalIgnoreCase) || 
                cleaned.Contains("No information", StringComparison.OrdinalIgnoreCase) ||
                cleaned.Contains("Information has not been found", StringComparison.OrdinalIgnoreCase))
            {
                return TrackingDetails.Empty(awb, "ParcelsApp retornou página sem informações de rastreio.");
            }

            // Se só temos N/A, também consideramos insuficiente
            if (result.Origin == "N/A" && result.Destination == "N/A" && (result.Timeline == null || result.Timeline.Count == 0))
            {
                return TrackingDetails.Empty(awb, "Resultado insuficiente (tudo N/A).");
            }

            Utils.WriteDiagFile(awb, "05_result_summary.txt",
                $"AWB={awb}\nOrigin={result.Origin}\nDestination={result.Destination}\nFlight={result.LastFlight}\nStatus={result.LastStatusCode}\nTimelineCount={(result.Timeline == null ? 0 : result.Timeline.Count)}");

            return result;
        }
        catch (Exception ex)
        {
            var shortBody = body.Length > 900 ? body.Substring(0, 900) + "..." : body;
            return TrackingDetails.Empty(awb, $"Parse Scrape.do failed: {ex.GetType().Name}: {ex.Message} | body={shortBody}");
        }
    }

    private static string CleanupMarkdown(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        var s = WebUtility.HtmlDecode(text);

        s = s.Replace("\r\n", "\n").Replace("\r", "\n");
        s = Regex.Replace(s, @"```[\s\S]*?```", " ", RegexOptions.Multiline);
        s = Regex.Replace(s, @"window\.[^\n]+", " ", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"function\s+[A-Za-z0-9_]+\s*\([^\)]*\)\s*\{[\s\S]*?\}", " ", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"gtag\([^\n]+\)", " ", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"[ \t]+", " ");
        s = Regex.Replace(s, @"\n{2,}", "\n");

        var lines = s
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(CleanLine)
            .Where(IsUsefulLine)
            .ToList();

        return string.Join('\n', lines);
    }

    private static string CleanLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return "";

        var s = line.Trim();
        s = s.Replace("*", "").Trim();
        if (s.StartsWith("-")) s = s.Substring(1).Trim();

        s = s.Replace("Maa", "Mar", StringComparison.OrdinalIgnoreCase);
        s = s.Replace("Fev", "Feb", StringComparison.OrdinalIgnoreCase);
        s = s.Replace("Abr", "Apr", StringComparison.OrdinalIgnoreCase);
        s = s.Replace("Mai", "May", StringComparison.OrdinalIgnoreCase);
        s = s.Replace("Ago", "Aug", StringComparison.OrdinalIgnoreCase);
        s = s.Replace("Set", "Sep", StringComparison.OrdinalIgnoreCase);
        s = s.Replace("Out", "Oct", StringComparison.OrdinalIgnoreCase);
        s = s.Replace("Dez", "Dec", StringComparison.OrdinalIgnoreCase);

        s = Regex.Replace(s, @"\s+", " ").Trim();
        return s;
    }

    private static bool IsUsefulLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return false;

        var s = line.Trim();

        if (s.Length < 3)
            return false;

        if (s.StartsWith("window.", StringComparison.OrdinalIgnoreCase))
            return false;

        if (s.Contains("dataLayer", StringComparison.OrdinalIgnoreCase))
            return false;

        if (s.Contains("gtag(", StringComparison.OrdinalIgnoreCase))
            return false;

        if (s.Contains("consent", StringComparison.OrdinalIgnoreCase) && s.Contains("storage", StringComparison.OrdinalIgnoreCase))
            return false;

        if (s.Contains("analytics_storage", StringComparison.OrdinalIgnoreCase))
            return false;

        if (s.Contains("ad_personalization", StringComparison.OrdinalIgnoreCase))
            return false;

        if (s.Contains("document.cookie", StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }

    private static string? FindOrigin(string text)
    {
        var patterns = new[]
        {
            @"(?i)\bOrigin\b(?-i)\s*[\|:\-]?\s*([A-Z]{3})\b",
            @"(?i)\bOrigem\b(?-i)\s*[\|:\-]?\s*([A-Z]{3})\b",
            @"(?i)\bFrom\b(?-i)\s*[\|:\-]?\s*([A-Z]{3})\b",
            @"(?i)De.*?\((?-i)([A-Z]{3})\)",
            @"(?i)From.*?\((?-i)([A-Z]{3})\)",
            @"(?i)De(?-i)([A-Z]{3}),",
            @"(?i)From(?-i)([A-Z]{3}),"
        };

        foreach (var pattern in patterns)
        {
            var m = Regex.Match(text, pattern);
            if (m.Success)
                return m.Groups[1].Value.ToUpperInvariant();
        }

        return null;
    }

    private static string? FindDestination(string text)
    {
        var patterns = new[]
        {
            @"(?i)\bDestination\b(?-i)\s*[\|:\-]?\s*([A-Z]{3})\b",
            @"(?i)\bDestino\b(?-i)\s*[\|:\-]?\s*([A-Z]{3})\b",
            @"(?i)\bTo\b(?-i)\s*[\|:\-]?\s*([A-Z]{3})\b",
            @"(?i)Para.*?\((?-i)([A-Z]{3})\)",
            @"(?i)To.*?\((?-i)([A-Z]{3})\)",
            @"(?i)Para(?-i)([A-Z]{3}),",
            @"(?i)To(?-i)([A-Z]{3}),"
        };

        foreach (var pattern in patterns)
        {
            var m = Regex.Match(text, pattern);
            if (m.Success)
                return m.Groups[1].Value.ToUpperInvariant();
        }

        return null;
    }

    private static string? FindFlight(string text)
    {
        var explicitMatch = Regex.Match(text, @"(?:Flight|Voo)[\s:]*([A-Z0-9]{4,7})\b", RegexOptions.IgnoreCase);
        if (explicitMatch.Success) return explicitMatch.Groups[1].Value.ToUpperInvariant();

        var matches = Regex.Matches(text, @"\b([A-Z]{2,3}\s?\d{2,5})\b", RegexOptions.IgnoreCase);
        foreach (Match m in matches)
        {
            var value = m.Groups[1].Value.Replace(" ", "").ToUpperInvariant();
            if (value.Length >= 4 && value.Length <= 8 && !IsNoiseFlight(value))
                return value;
        }

        return null;
    }

    private static bool IsNoiseFlight(string s)
    {
        if (s.StartsWith("JAN") || s.StartsWith("FEB") || s.StartsWith("MAR") || s.StartsWith("APR") || 
            s.StartsWith("MAY") || s.StartsWith("JUN") || s.StartsWith("JUL") || s.StartsWith("AUG") || 
            s.StartsWith("SEP") || s.StartsWith("OCT") || s.StartsWith("NOV") || s.StartsWith("DEC")) return true;

        return s.Equals("THE", StringComparison.OrdinalIgnoreCase) ||
               s.Equals("GET", StringComparison.OrdinalIgnoreCase) ||
               s.Equals("AWB", StringComparison.OrdinalIgnoreCase);
    }

    private static string? FindStatusCode(string text, List<TrackingEvent>? timeline)
    {
        var matches = Regex.Matches(
            text,
            @"\b(BKD|FBW|RCS|RCT|MAN|FFM|DEP|LOF|TFD|AWD|ARR|DLH|NFD|RCF|DLV|POD)\b",
            RegexOptions.IgnoreCase);

        if (matches.Count > 0)
        {
            // O ParcelsApp geralmente lista os eventos mais recentes no topo.
            return matches[0].Groups[1].Value.ToUpperInvariant();
        }

        if (timeline != null && timeline.Count > 0)
        {
            var firstEventDesc = timeline[0].Description ?? "";
            
            if (Regex.IsMatch(firstEventDesc, @"\b(Delivered|Entrega)\b", RegexOptions.IgnoreCase)) return "DLV";
            if (Regex.IsMatch(firstEventDesc, @"\b(Arrived|Arrival|Chegada|Chegou)\b", RegexOptions.IgnoreCase)) return "ARR";
            if (Regex.IsMatch(firstEventDesc, @"\b(Departed|Departure|Partida|Partiu)\b", RegexOptions.IgnoreCase)) return "DEP";
            if (Regex.IsMatch(firstEventDesc, @"\b(Received|Ready for carriage|Recebido|Pronto para o transporte)\b", RegexOptions.IgnoreCase)) return "RCS";
            if (Regex.IsMatch(firstEventDesc, @"\b(Booked|Booking|Reservado)\b", RegexOptions.IgnoreCase)) return "BKD";
            if (Regex.IsMatch(firstEventDesc, @"\b(Notified|Notificado)\b", RegexOptions.IgnoreCase)) return "NFD";
            if (Regex.IsMatch(firstEventDesc, @"\b(Manifested|Manifestado)\b", RegexOptions.IgnoreCase)) return "MAN";
        }

        return null;
    }

    private static string NormalizeStatusCode(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "N/A";

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
            _ => "N/A"
        };
    }

    private static List<TrackingEvent> ParseTimeline(string text)
    {
        var result = new List<TrackingEvent>();

        var lines = text
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(CleanLine)
            .Where(IsUsefulLine)
            .ToList();

        var dateRegex = new Regex(
            @"(?<date>\d{1,2}\s+\S{3,}\s+\d{4})(?:\s+(?<time>\d{1,2}:\d{2}))?",
            RegexOptions.Compiled);

        for (int i = 0; i < lines.Count; i++)
        {
            var current = lines[i];
            var match = dateRegex.Match(current);
            if (!match.Success)
                continue;

            // Evitar que capture datas soltas no meio de frases longas (as de timeline costumam estar sozinhas)
            if (current.Length > match.Length + 4) continue; 

            var date = match.Groups["date"].Value.Trim();
            var time = match.Groups["time"].Success ? match.Groups["time"].Value.Trim() : "";
            var timestamp = string.IsNullOrWhiteSpace(time) ? date : $"{date} {time}";

            var nextLines = new List<string>();
            for (int j = i + 1; j < lines.Count && nextLines.Count < 3; j++)
            {
                var lineMatch = dateRegex.Match(lines[j]);
                if (lineMatch.Success && lines[j].Length <= lineMatch.Length + 4)
                    break;

                nextLines.Add(lines[j]);
            }

            var description = nextLines.Count > 0 ? nextLines[0] : "N/A";
            var location = "N/A";
            var carrier = nextLines.Count > 1 ? nextLines[^1] : "N/A";

            var locMatch = Regex.Match(description, @"\(([A-Z]{3})\)");
            if (locMatch.Success) 
            {
                location = locMatch.Groups[1].Value.ToUpperInvariant();
            }

            result.Add(new TrackingEvent
            {
                Timestamp = timestamp,
                Description = description,
                Location = location,
                Carrier = carrier
            });
        }

        return result;
    }
}