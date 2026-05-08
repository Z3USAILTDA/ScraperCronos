//Db.cs

#nullable enable
using MySqlConnector;
using System.Text;
using System.Text.Json;
public static class Db
{
    private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static async Task<LastSnapshot?> GetLastSnapshotAsync(string awb)
    {
        var cs = BuildMariaDbConnString();
        await using var conn = new MySqlConnector.MySqlConnection(cs);
        await conn.OpenAsync();

        var sql = @"
SELECT
  last_status_code,
  last_status_description,
  last_status_timestamp,
  last_flight,
  origin,
  destination
FROM cronos.t_aereo_ws
WHERE awb = @awb
LIMIT 1;
";
        await using var cmd = new MySqlConnector.MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@awb", awb);

        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return null;

        return new LastSnapshot
        {
            LastStatusCode = r.IsDBNull(0) ? null : r.GetString(0),
            LastStatusDescription = r.IsDBNull(1) ? null : r.GetString(1),
            LastStatusTimestamp = r.IsDBNull(2) ? null : r.GetString(2),
            LastFlight = r.IsDBNull(3) ? null : r.GetString(3),
            Origin = r.IsDBNull(4) ? null : r.GetString(4),
            Destination = r.IsDBNull(5) ? null : r.GetString(5),
        };
    }

    public sealed class LastSnapshot
    {
        public string? LastStatusCode { get; set; }
        public string? LastStatusDescription { get; set; }
        public string? LastStatusTimestamp { get; set; }
        public string? LastFlight { get; set; }
        public string? Origin { get; set; }
        public string? Destination { get; set; }
    }

    private static string NormalizeDisplayDateTime(string input)
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
        ["Dez"] = "Dec",
        // Fallback para Árabe (visto em diagnósticos do ParcelsApp)
        ["يناير"] = "Jan",
        ["فبراير"] = "Feb",
        ["مارس"] = "Mar",
        ["أبريل"] = "Apr",
        ["أبر"] = "Apr",
        ["مايو"] = "May",
        ["يونيو"] = "Jun",
        ["يوليو"] = "Jul",
        ["أغسطس"] = "Aug",
        ["سبتمبر"] = "Sep",
        ["أكتوبر"] = "Oct",
        ["نوفمبر"] = "Nov",
        ["ديسمبر"] = "Dec"
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

private static string NormalizeDisplayDate(string input)
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
        ["Dez"] = "Dec",
        // Fallback para Árabe
        ["يناير"] = "Jan",
        ["فبراير"] = "Feb",
        ["مارس"] = "Mar",
        ["أبريل"] = "Apr",
        ["أبر"] = "Apr",
        ["مايو"] = "May",
        ["يونيو"] = "Jun",
        ["يوليو"] = "Jul",
        ["أغسطس"] = "Aug",
        ["سبتمبر"] = "Sep",
        ["أكتوبر"] = "Oct",
        ["نوفمبر"] = "Nov",
        ["ديسمبر"] = "Dec"
    };

    var parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    if (parts.Length >= 3)
    {
        var month = parts[1].Trim().TrimEnd('.');
        if (map.TryGetValue(month, out var normalizedMonth))
            parts[1] = normalizedMonth;

        return string.Join(" ", parts);
    }

    return input;
}

    public static string BuildMariaDbConnString()
    {
        var host = Environment.GetEnvironmentVariable("DB_HOST") ?? "127.0.0.1";
        var port = Environment.GetEnvironmentVariable("DB_PORT") ?? "3306";
        var db = Environment.GetEnvironmentVariable("DB_NAME") ?? "";
        var user = Environment.GetEnvironmentVariable("DB_USER") ?? "";
        var pass = Environment.GetEnvironmentVariable("DB_PASS") ?? "";
        var ssl = Environment.GetEnvironmentVariable("DB_SSLMODE") ?? "None";

        return $"Server={host};Port={port};Database={db};User ID={user};Password={pass};SslMode={ssl};" +
               "Allow User Variables=True;Default Command Timeout=60;Connection Timeout=30;";
    }

    private static string? Trunc(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s) || s == "N/A") return null;
        s = s.Trim();
        return s.Length <= max ? s : s.Substring(0, max);
    }

    private static string? ToIata3(string? s)
    {
        if (string.IsNullOrWhiteSpace(s) || s == "N/A") return null;

        var m = System.Text.RegularExpressions.Regex.Match(s.ToUpperInvariant(), @"\b([A-Z]{3})\b");
        if (m.Success) return m.Groups[1].Value;

        m = System.Text.RegularExpressions.Regex.Match(s.ToUpperInvariant(), @"([A-Z]{3})");
        if (m.Success) return m.Groups[1].Value;

        return null;
    }

    private static string? TruncErr(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        return s.Length <= max ? s : s.Substring(0, max);
    }

    private static object NullIfNA(string? s)
    {
        if (string.IsNullOrWhiteSpace(s) || s == "N/A") return DBNull.Value;
        return s;
    }

    private static object TryGet(object? obj, string prop)
    {
        try
        {
            if (obj is null) return DBNull.Value;
            var v = obj.GetType().GetProperty(prop)?.GetValue(obj)?.ToString();
            return NullIfNA(v);
        }
        catch
        {
            return DBNull.Value;
        }
    }

    // FIX 1: regex agora aceita sufixo alfabético opcional no número do voo (ex: LA5202T)
    private static string? ExtractFlightCode(string? s)
    {
        if (string.IsNullOrWhiteSpace(s) || s == "N/A")
            return null;

        var m = System.Text.RegularExpressions.Regex.Match(
            s.ToUpperInvariant(),
            @"\b([A-Z]{2,3})\s?([0-9]{2,5}[A-Z]?)\b"
        );

        if (m.Success)
            return m.Groups[1].Value + m.Groups[2].Value;

        return null;
    }

    private static object TryGetInt(object? obj, string prop)
    {
        try
        {
            if (obj is null) return DBNull.Value;
            var raw = obj.GetType().GetProperty(prop)?.GetValue(obj);
            if (raw is null) return DBNull.Value;

            if (raw is int i) return i;
            if (int.TryParse(raw.ToString(), out int n)) return n;
            return DBNull.Value;
        }
        catch
        {
            return DBNull.Value;
        }
    }

    private static string? BuildTimelineJson(object? timeline)
{
    if (timeline is null)
        return null;

    if (timeline is not System.Collections.IEnumerable items)
        return null;

    var result = new List<object>();

    foreach (var item in items)
    {
        if (item is null)
            continue;

        var rawDateTime =
            GetStringProp(item, "DateTime") ??
            GetStringProp(item, "Timestamp") ??
            GetStringProp(item, "EventDateTime") ??
            GetStringProp(item, "OccurredAt");

        var rawDate =
            GetStringProp(item, "Date") ??
            GetStringProp(item, "EventDate");

        var rawTime =
            GetStringProp(item, "Time") ??
            GetStringProp(item, "EventTime");

        var description =
            GetStringProp(item, "Description") ??
            GetStringProp(item, "StatusDescription") ??
            GetStringProp(item, "Status") ??
            GetStringProp(item, "Event") ??
            "";

        var location =
            GetStringProp(item, "Location") ??
            GetStringProp(item, "Place") ??
            GetStringProp(item, "City") ??
            GetStringProp(item, "Station") ??
            "";

        var carrier =
            GetStringProp(item, "Carrier") ??
            GetStringProp(item, "Airline") ??
            "";

        string finalDateTime = "";

        if (!string.IsNullOrWhiteSpace(rawDateTime))
        {
            var parsed = TryParseDateTimeFlexible(rawDateTime);
            if (parsed.HasValue)
            {
                finalDateTime = parsed.Value.ToString("dd MMM yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture);
            }
            else
            {
                finalDateTime = NormalizeDisplayDateTime(rawDateTime);
            }
        }
        else
        {
            string finalDate = "";
            string finalTime = "";

            if (!string.IsNullOrWhiteSpace(rawDate))
            {
                var parsedDate = TryParseDateFlexible(rawDate);
                finalDate = parsedDate.HasValue
                    ? parsedDate.Value.ToString("dd MMM yyyy", System.Globalization.CultureInfo.InvariantCulture)
                    : NormalizeDisplayDate(rawDate);
            }

            if (!string.IsNullOrWhiteSpace(rawTime))
            {
                finalTime = NormalizeTime(rawTime);
            }

            finalDateTime = string.IsNullOrWhiteSpace(finalTime)
                ? finalDate
                : $"{finalDate} {finalTime}".Trim();
        }

        result.Add(new
        {
            date = finalDateTime,
            description = description,
            location = location,
            carrier = carrier
        });
    }

    return JsonSerializer.Serialize(result, JsonOpts);
}

    private static string? GetStringProp(object obj, string propName)
    {
        try
        {
            var prop = obj.GetType().GetProperty(propName);
            if (prop is null) return null;

            var value = prop.GetValue(obj);
            return value?.ToString()?.Trim();
        }
        catch
        {
            return null;
        }
    }

    private static DateTime? TryParseDateTimeFlexible(string input)
{
    if (string.IsNullOrWhiteSpace(input))
        return null;

    input = NormalizeDisplayDateTime(input);

    var formats = new[]
    {
        "dd MMM yyyy HH:mm:ss",
        "dd MMM yyyy HH:mm",
        "d MMM yyyy HH:mm:ss",
        "d MMM yyyy HH:mm",
        "dd/MM/yyyy HH:mm:ss",
        "dd/MM/yyyy HH:mm",
        "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-dd HH:mm",
        "yyyy-MM-ddTHH:mm:ss",
        "yyyy-MM-ddTHH:mm:ssZ",
        "yyyy-MM-ddTHH:mm:ss.fffZ",
        "MM/dd/yyyy HH:mm:ss",
        "MM/dd/yyyy HH:mm"
    };

    if (DateTime.TryParseExact(
            input,
            formats,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AllowWhiteSpaces | System.Globalization.DateTimeStyles.AssumeLocal,
            out var dtExact))
    {
        return dtExact;
    }

    if (DateTime.TryParse(
            input,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AllowWhiteSpaces | System.Globalization.DateTimeStyles.AssumeLocal,
            out var dt))
    {
        return dt;
    }

    if (DateTime.TryParse(
            input,
            new System.Globalization.CultureInfo("pt-BR"),
            System.Globalization.DateTimeStyles.AllowWhiteSpaces | System.Globalization.DateTimeStyles.AssumeLocal,
            out var dtBr))
    {
        return dtBr;
    }

    return null;
}

    private static DateTime? TryParseDateFlexible(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        var formats = new[]
        {
            "dd/MM/yyyy",
            "yyyy-MM-dd",
            "dd MMM yyyy",
            "MM/dd/yyyy"
        };

        if (DateTime.TryParseExact(
                input,
                formats,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AllowWhiteSpaces,
                out var dtExact))
        {
            return dtExact;
        }

        if (DateTime.TryParse(
                input,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AllowWhiteSpaces,
                out var dt))
        {
            return dt;
        }

        if (DateTime.TryParse(
                input,
                new System.Globalization.CultureInfo("pt-BR"),
                System.Globalization.DateTimeStyles.AllowWhiteSpaces,
                out var dtBr))
        {
            return dtBr;
        }

        return null;
    }

    private static string NormalizeTime(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return "";

        if (DateTime.TryParse(
                input,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AllowWhiteSpaces,
                out var dt))
        {
            return dt.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        }

        if (DateTime.TryParse(
                input,
                new System.Globalization.CultureInfo("pt-BR"),
                System.Globalization.DateTimeStyles.AllowWhiteSpaces,
                out var dtBr))
        {
            return dtBr.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        }

        return input.Trim();
    }

    // FIX 2: condição taf.source movida para o ON do LEFT JOIN,
    // evitando que o filtro no WHERE anule o LEFT JOIN para AWBs novos
    public static async Task<List<TrackingJob>> LoadJobsFromMariaDbAsync()
    {
        var cs = BuildMariaDbConnString();
        await using var conn = new MySqlConnection(cs);
        await conn.OpenAsync();

        var sql = @"
SELECT DISTINCT
    tda.awb_number,
    tda.hawb_number,
    taf.awb,
    taf.destination
FROM cronos.t_aereo tda
LEFT JOIN cronos.t_aereo_ws taf
    ON taf.awb = tda.awb_number
WHERE tda.awb_number IS NOT NULL
  AND TRIM(tda.awb_number) <> ''
  AND LEFT(tda.awb_number, 3) NOT IN ('001', '083', '016', '996', '139')
  AND (
        taf.awb IS NULL
        OR taf.last_status_code IS NULL
        OR taf.last_status_code NOT IN ('DLV', 'POD')
        OR (taf.origin IS NULL AND taf.destination IS NULL)
      )
  AND tda.created_at >= '2026-03-01'
ORDER BY tda.awb_number, tda.hawb_number;
";

        await using var cmd = new MySqlCommand(sql, conn);
        var map = new Dictionary<string, TrackingJob>(StringComparer.OrdinalIgnoreCase);

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var rawAwb = reader.IsDBNull(0) ? "" : reader.GetString(0);
            var awb = Utils.NormalizeAwb(rawAwb);
            if (awb is null) continue;

            if (!map.TryGetValue(awb, out var job))
            {
                job = new TrackingJob
                {
                    Awb = awb
                };
                map[awb] = job;
            }

            if (!reader.IsDBNull(1))
            {
                var hawb = reader.GetString(1).Trim();
                if (!string.IsNullOrWhiteSpace(hawb) && !job.Hawbs.Contains(hawb))
                    job.Hawbs.Add(hawb);
            }
        }

        return map.Values.ToList();
    }

    /// <summary>
    /// Carrega apenas AWBs que foram adicionados nos últimos <paramref name="lookbackMinutes"/> minutos.
    /// Usado pelo loop rápido para processar AWBs recém-inseridos com alta frequência.
    /// </summary>
    public static async Task<List<TrackingJob>> LoadRecentJobsFromMariaDbAsync(int lookbackMinutes)
    {
        var cs = BuildMariaDbConnString();
        await using var conn = new MySqlConnection(cs);
        await conn.OpenAsync();

        var sql = @"
SELECT DISTINCT
    tda.awb_number,
    tda.hawb_number,
    taf.awb,
    taf.destination
FROM cronos.t_aereo tda
LEFT JOIN cronos.t_aereo_ws taf
    ON taf.awb = tda.awb_number
WHERE tda.awb_number IS NOT NULL
  AND TRIM(tda.awb_number) <> ''
  AND (
        taf.awb IS NULL
        OR taf.last_status_code IS NULL
        OR taf.last_status_code NOT IN ('DLV', 'POD')
        OR (taf.origin IS NULL AND taf.destination IS NULL)
      )
  AND tda.created_at >= NOW() - INTERVAL @minutes MINUTE
ORDER by tda.awb_number, tda.hawb_number;
";

        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@minutes", lookbackMinutes);

        var map = new Dictionary<string, TrackingJob>(StringComparer.OrdinalIgnoreCase);

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var rawAwb = reader.IsDBNull(0) ? "" : reader.GetString(0);
            var awb = Utils.NormalizeAwb(rawAwb);
            if (awb is null) continue;

            if (!map.TryGetValue(awb, out var job))
            {
                job = new TrackingJob
                {
                    Awb = awb
                };
                map[awb] = job;
            }

            if (!reader.IsDBNull(1))
            {
                var hawb = reader.GetString(1).Trim();
                if (!string.IsNullOrWhiteSpace(hawb) && !job.Hawbs.Contains(hawb))
                    job.Hawbs.Add(hawb);
            }
        }

        return map.Values.ToList();
    }

    public static async Task SaveResultToMariaDbAsync(TrackingDetails r)
    {
        await Logging.LogAwbAsync(r.Awb, "DB_UPSERT_BEGIN");

        var cs = BuildMariaDbConnString();
        await using var conn = new MySqlConnection(cs);
        await conn.OpenAsync();

        var hawbsJson = JsonSerializer.Serialize(r.Hawbs ?? new List<string>(), JsonOpts);
        var timelineJson = BuildTimelineJson(r.Timeline);

        var sql = @"
INSERT INTO cronos.t_aereo_ws
(
  awb,
  hawbs_json,
  scraped_at,
  last_flight,
  last_status_code,
  origin,
  destination,
  timeline_json,
  source,
  error_msg,
  tipo_servico
)
VALUES
(
  @awb,
  @hawbs_json,
  CURRENT_TIMESTAMP(3),
  @last_flight,
  @last_status_code,
  @origin,
  @destination,
  @timeline_json,
  @source,
  @error_msg,
  @tipo_servico
)
ON DUPLICATE KEY UPDATE
  scraped_at = VALUES(scraped_at),
  last_flight = VALUES(last_flight),
  last_status_code = VALUES(last_status_code),
  origin = VALUES(origin),
  destination = VALUES(destination),
  timeline_json = VALUES(timeline_json),
  source = VALUES(source),
  error_msg = VALUES(error_msg),
  tipo_servico = IF(
    VALUES(tipo_servico) IS NULL OR VALUES(tipo_servico) = '',
    tipo_servico,
    VALUES(tipo_servico)
  ),
  hawbs_json = IF(
    VALUES(hawbs_json) IS NULL OR VALUES(hawbs_json) = '[]',
    hawbs_json,
    VALUES(hawbs_json)
  );
";

        await using var cmd = new MySqlCommand(sql, conn);

        cmd.Parameters.AddWithValue("@awb", r.Awb);
        cmd.Parameters.AddWithValue("@hawbs_json", hawbsJson);

        var flightCode = ExtractFlightCode(r.LastFlight);
        cmd.Parameters.AddWithValue("@last_flight", (object?)flightCode ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@last_status_code", NullIfNA(r.LastStatusCode));

        var origin3 = ToIata3(r.Origin) ?? "N/A";
        var dest3 = ToIata3(r.Destination) ?? "N/A";

        cmd.Parameters.AddWithValue("@origin", (object?)Trunc(origin3, 10) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@destination", (object?)Trunc(dest3, 10) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@timeline_json", (object?)timelineJson ?? DBNull.Value);

        cmd.Parameters.AddWithValue("@source", string.IsNullOrWhiteSpace(r.Source) ? "Desconhecido" : r.Source);

        var err = TruncErr(r.Error, 1000);
        cmd.Parameters.AddWithValue("@error_msg", (object?)err ?? DBNull.Value);

        cmd.Parameters.AddWithValue("@tipo_servico",
            string.IsNullOrWhiteSpace(r.TipoServico) ? DBNull.Value : r.TipoServico);

        if (!string.IsNullOrWhiteSpace(r.Origin) && r.Origin.Length > 10)
            await Logging.LogAwbAsync(r.Awb, $"WARN origin_long len={r.Origin.Length} value={r.Origin}");

        if (!string.IsNullOrWhiteSpace(r.LastFlight) && r.LastFlight.Length > 30)
            await Logging.LogAwbAsync(r.Awb, $"WARN last_flight_long len={r.LastFlight.Length} value={r.LastFlight}");

        await cmd.ExecuteNonQueryAsync();

        await Logging.LogAwbAsync(r.Awb, "DB_UPSERT_END");
    }
}