// FirecrawlClient.cs
#nullable enable
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

public sealed class FirecrawlClient
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _proxy;
    private readonly int _timeoutMs;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public FirecrawlClient(HttpClient http, string baseUrl, string proxy, int timeoutMs)
    {
        _http = http;
        _baseUrl = baseUrl.TrimEnd('/');
        _proxy = proxy;
        _timeoutMs = timeoutMs;
    }

    public async Task<TrackingDetails> ScrapeParcelsAsync(string awb)
    {
        var url = $"https://parcelsapp.com/pt/tracking/{Uri.EscapeDataString(awb)}";

        var prompt = @"
Extraia APENAS dados de rastreio (parcelsapp). Retorne JSON com este formato:
{
  ""last_status_description"": string,
  ""last_status_timestamp"": string,
  ""last_flight"": string,
  ""origin"": string,
  ""destination"": string,
  ""sidebar"": {
    ""trackingNumber"": string,
    ""from"": string,
    ""to"": string,
    ""originCountry"": string,
    ""destinationCountry"": string,
    ""foundIn"": string,
    ""trackedWithCouriers"": string,
    ""pieces"": string,
    ""daysInTransit"": string,
    ""flightLegs"": string[]
  },
  ""timeline"": [
    { ""timestamp"": string, ""description"": string, ""location"": string, ""carrier"": string }
  ]
}
Se não encontrar algum campo, use ""N/A"". Se não houver eventos, timeline deve ser [].
";

        var payload = new
        {
            url,
            // V2: formats agora pode ser uma lista de objetos.
            // JSON extraction: { type:"json", prompt, schema? }  (schema opcional)
            formats = new object[]
    {
        new
        {
            type = "json",
            prompt
            // schema = new { ... } // opcional (se quiser “travar” o formato)
        }
    },

            // mantém opções úteis
            onlyMainContent = false,
            proxy = _proxy,           // basic/enhanced/auto (depende do plano)
            timeout = _timeoutMs,

            // se você quiser esperar:
        };

        var reqJson = JsonSerializer.Serialize(payload, JsonOpts);

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/scrape")
        {
            Content = new StringContent(reqJson, Encoding.UTF8, "application/json")
        };

        using var resp = await _http.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();

        if (!resp.IsSuccessStatusCode)
            return TrackingDetails.Empty(awb, $"Firecrawl HTTP {(int)resp.StatusCode}: {body}");

        var parsed = JsonSerializer.Deserialize<FirecrawlScrapeResponse>(body, JsonOpts);
        if (parsed is null || !parsed.Success || parsed.Data?.Json is null)
            return TrackingDetails.Empty(awb, $"Firecrawl resposta inválida: {body}");

        var j = parsed.Data.Json;

        var details = new TrackingDetails
        {
            Awb = awb,
            LastStatusDescription = string.IsNullOrWhiteSpace(j.LastStatusDescription) ? "N/A" : j.LastStatusDescription,
            Timestamp = string.IsNullOrWhiteSpace(j.LastStatusTimestamp) ? "N/A" : j.LastStatusTimestamp,
            LastFlight = string.IsNullOrWhiteSpace(j.LastFlight) ? "N/A" : j.LastFlight,
            Origin = string.IsNullOrWhiteSpace(j.Origin) ? "N/A" : j.Origin,
            Destination = string.IsNullOrWhiteSpace(j.Destination) ? "N/A" : j.Destination,
            Sidebar = MapSidebar(j.Sidebar),
            Timeline = MapTimeline(j.Timeline),
            Error = ""
        };

        details.LastStatusCode = Utils.InferAirStatusCode(details.LastStatusDescription);

        if (details.LastStatusDescription == "N/A" && (details.Timeline?.Count ?? 0) == 0)
            details.Error = "Firecrawl não retornou eventos úteis (timeline vazia).";

        return details;
    }

    private static SidebarSummary MapSidebar(FirecrawlJsonSidebar? s)
    {
        return new SidebarSummary
        {
            TrackingNumber = s?.TrackingNumber ?? "N/A",
            From = s?.From ?? "N/A",
            To = s?.To ?? "N/A",
            OriginCountry = s?.OriginCountry ?? "N/A",
            DestinationCountry = s?.DestinationCountry ?? "N/A",
            FoundIn = s?.FoundIn ?? "N/A",
            TrackedWithCouriers = s?.TrackedWithCouriers ?? "N/A",
            Pieces = s?.Pieces ?? "N/A",
            DaysInTransit = s?.DaysInTransit ?? "N/A",
            FlightLegs = s?.FlightLegs ?? new List<string>()
        };
    }

    private static List<TrackingEvent> MapTimeline(List<FirecrawlJsonEvent>? tl)
    {
        if (tl is null) return new();
        return tl.Select(e => new TrackingEvent
        {
            Timestamp = string.IsNullOrWhiteSpace(e.Timestamp) ? "N/A" : e.Timestamp!,
            Description = string.IsNullOrWhiteSpace(e.Description) ? "N/A" : e.Description!,
            Location = string.IsNullOrWhiteSpace(e.Location) ? "N/A" : e.Location!,
            Carrier = string.IsNullOrWhiteSpace(e.Carrier) ? "N/A" : e.Carrier!,
        }).ToList();
    }

    // DTOs
    private sealed class FirecrawlScrapeResponse
    {
        [JsonPropertyName("success")] public bool Success { get; set; }
        [JsonPropertyName("data")] public FirecrawlScrapeData? Data { get; set; }
    }

    private sealed class FirecrawlScrapeData
    {
        [JsonPropertyName("json")] public FirecrawlJsonRoot? Json { get; set; }
    }

    private sealed class FirecrawlJsonRoot
    {
        [JsonPropertyName("last_status_description")] public string? LastStatusDescription { get; set; }
        [JsonPropertyName("last_status_timestamp")] public string? LastStatusTimestamp { get; set; }
        [JsonPropertyName("last_flight")] public string? LastFlight { get; set; }
        [JsonPropertyName("origin")] public string? Origin { get; set; }
        [JsonPropertyName("destination")] public string? Destination { get; set; }
        [JsonPropertyName("sidebar")] public FirecrawlJsonSidebar? Sidebar { get; set; }
        [JsonPropertyName("timeline")] public List<FirecrawlJsonEvent>? Timeline { get; set; }
    }

    private sealed class FirecrawlJsonSidebar
    {
        [JsonPropertyName("trackingNumber")] public string? TrackingNumber { get; set; }
        [JsonPropertyName("from")] public string? From { get; set; }
        [JsonPropertyName("to")] public string? To { get; set; }
        [JsonPropertyName("originCountry")] public string? OriginCountry { get; set; }
        [JsonPropertyName("destinationCountry")] public string? DestinationCountry { get; set; }
        [JsonPropertyName("foundIn")] public string? FoundIn { get; set; }
        [JsonPropertyName("trackedWithCouriers")] public string? TrackedWithCouriers { get; set; }
        [JsonPropertyName("pieces")] public string? Pieces { get; set; }
        [JsonPropertyName("daysInTransit")] public string? DaysInTransit { get; set; }
        [JsonPropertyName("flightLegs")] public List<string>? FlightLegs { get; set; }
    }

    private sealed class FirecrawlJsonEvent
    {
        [JsonPropertyName("timestamp")] public string? Timestamp { get; set; }
        [JsonPropertyName("description")] public string? Description { get; set; }
        [JsonPropertyName("location")] public string? Location { get; set; }
        [JsonPropertyName("carrier")] public string? Carrier { get; set; }
    }
}