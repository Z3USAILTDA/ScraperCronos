// Db.cs
#nullable enable
using MySqlConnector;
using System.Text.Json;

public static class Db
{
    public static async Task<LastSnapshot?> GetLastSnapshotAsync(string awb)
    {
        var cs = BuildMariaDbConnString();
        await using var conn = new MySqlConnection(cs);
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
        await using var cmd = new MySqlCommand(sql, conn);
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

    private static string? ExtractFlightCode(string? s)
    {
        if (string.IsNullOrWhiteSpace(s) || s == "N/A")
            return null;

        var m = System.Text.RegularExpressions.Regex.Match(
            s.ToUpperInvariant(),
            @"\b([A-Z]{2,3})\s?([0-9]{2,5})\b"
        );

        if (m.Success)
            return m.Groups[1].Value + m.Groups[2].Value;

        return null;
    }

    public static async Task<List<TrackingJob>> LoadJobsFromMariaDbAsync()
    {
        var cs = BuildMariaDbConnString();
        await using var conn = new MySqlConnection(cs);
        await conn.OpenAsync();

        var sql = @"
SELECT tmd.awb_number, tmd.hawb_number
FROM cronos.t_aereo tmd
WHERE tmd.awb_number IS NOT NULL AND TRIM(tmd.awb_number) <> ''
ORDER BY tmd.awb_number;
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
                job = new TrackingJob { Awb = awb };
                map[awb] = job;
            }

            if (reader.FieldCount > 1 && !reader.IsDBNull(1))
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

        var hawbsJson = JsonSerializer.Serialize(r.Hawbs ?? new List<string>());
        var timelineJson = r.Timeline is null ? null : JsonSerializer.Serialize(r.Timeline);

        var sql = @"
INSERT INTO cronos.t_aereo_ws
(
  awb,
  hawbs_json,
  scraped_at,
  last_flight,
  origin,
  destination,
  last_status_code,
  last_status_description,
  last_status_timestamp,
  timeline_json,
  error_msg,
  tipo_servico
)
VALUES
(
  @awb,
  @hawbs_json,
  CURRENT_TIMESTAMP(3),
  @last_flight,
  @origin,
  @destination,
  @last_status_code,
  @last_status_description,
  @last_status_timestamp,
  @timeline_json,
  @error_msg,
  @tipo_servico
)
ON DUPLICATE KEY UPDATE
  scraped_at = VALUES(scraped_at),
  last_flight = VALUES(last_flight),
  origin = VALUES(origin),
  destination = VALUES(destination),
  last_status_code = VALUES(last_status_code),
  last_status_description = VALUES(last_status_description),
  last_status_timestamp = VALUES(last_status_timestamp),
  timeline_json = IF(
    VALUES(timeline_json) IS NULL,
    timeline_json,
    VALUES(timeline_json)
  ),
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

        var origin3 = ToIata3(r.Origin) ?? "N/A";
        var dest3 = ToIata3(r.Destination) ?? "N/A";

        var flightCode = ExtractFlightCode(r.LastFlight);
        cmd.Parameters.AddWithValue("@last_flight", (object?)flightCode ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@origin", (object?)Trunc(origin3, 10) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@destination", (object?)Trunc(dest3, 10) ?? DBNull.Value);

        cmd.Parameters.AddWithValue("@last_status_code", NullIfNA(r.LastStatusCode));
        cmd.Parameters.AddWithValue("@last_status_description", NullIfNA(r.LastStatusDescription));
        cmd.Parameters.AddWithValue("@last_status_timestamp", NullIfNA(r.Timestamp));
        cmd.Parameters.AddWithValue("@timeline_json", (object?)timelineJson ?? DBNull.Value);

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