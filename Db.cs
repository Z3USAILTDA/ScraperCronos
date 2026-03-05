// Db.cs
#nullable enable
using MySqlConnector;
using System.Text;
using System.Text.Json;

public static class Db
{
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

        // pega primeiro AAA no texto
        var m = System.Text.RegularExpressions.Regex.Match(s.ToUpperInvariant(), @"\b([A-Z]{3})\b");
        if (m.Success) return m.Groups[1].Value;

        // se vier "GRU/BR" ou "GRU-" etc.
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
        catch { return DBNull.Value; }
    }

    private static string? ExtractFlightCode(string? s)
    {
        if (string.IsNullOrWhiteSpace(s) || s == "N/A")
            return null;

        // Ex: LH 1234, LH1234, AF 456, QR789
        var m = System.Text.RegularExpressions.Regex.Match(
            s.ToUpperInvariant(),
            @"\b([A-Z]{2,3})\s?([0-9]{2,5})\b"
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
        catch { return DBNull.Value; }
    }

    /// <summary>
    /// Ajuste o SQL conforme seu ambiente. Se você usa cronos.t_master_dados, altere aqui.
    /// </summary>
    public static async Task<List<TrackingJob>> LoadJobsFromMariaDbAsync()
    {
        var cs = BuildMariaDbConnString();
        await using var conn = new MySqlConnection(cs);
        await conn.OpenAsync();

    //ATENÇÃO: ajuste o schema / banco conforme o seu.
           var sql = @"
        SELECT tmd.awb_number, tmd.hawb_number
        FROM cronos.t_aereo tmd
        ORDER BY tmd.awb_number;
        ";

        //        var sql = @"
        //SELECT tmd.awb_number, tmd.hawb_number
        //FROM cronos.t_aereo tmd
        //WHERE tmd.awb_number IS NOT NULL AND tmd.awb_number <> ''
        //  AND tmd.hawb_number IS NOT NULL AND TRIM(tmd.hawb_number) <> ''
        //  AND tmd.updated_at >= '2026-03-05'
        //ORDER BY tmd.awb_number;
        //";

        await using var cmd = new MySqlCommand(sql, conn);
        var map = new Dictionary<string, TrackingJob>(StringComparer.OrdinalIgnoreCase);

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var rawAwb = reader.IsDBNull(0) ? "" : reader.GetString(0);
            var awb = Utils.NormalizeAwb(rawAwb);
            if (awb is null) continue;

            string? tipoServico = null; // <- aqui

            if (!map.TryGetValue(awb, out var job))
            {
                job = new TrackingJob { Awb = awb, TipoServico = tipoServico };
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

        var hawbsJson = JsonSerializer.Serialize(r.Hawbs ?? new List<string>());
        var sidebarJson = r.Sidebar is null ? null : JsonSerializer.Serialize(r.Sidebar);
        var timelineJson = r.Timeline is null ? null : JsonSerializer.Serialize(r.Timeline);

        // ATENÇÃO: ajuste o schema/tabela conforme o seu banco.
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

  sidebar_tracking_number,
  sidebar_from_text,
  sidebar_to_text,
  sidebar_origin_country,
  sidebar_destination_country,
  sidebar_found_in,
  sidebar_tracked_with_couriers,
  sidebar_pieces,
  sidebar_days_in_transit,

  sidebar_json,
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

  @sidebar_tracking_number,
  @sidebar_from_text,
  @sidebar_to_text,
  @sidebar_origin_country,
  @sidebar_destination_country,
  @sidebar_found_in,
  @sidebar_tracked_with_couriers,
  @sidebar_pieces,
  @sidebar_days_in_transit,

  @sidebar_json,
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

  sidebar_tracking_number = VALUES(sidebar_tracking_number),
  sidebar_from_text = VALUES(sidebar_from_text),
  sidebar_to_text = VALUES(sidebar_to_text),
  sidebar_origin_country = VALUES(sidebar_origin_country),
  sidebar_destination_country = VALUES(sidebar_destination_country),
  sidebar_found_in = VALUES(sidebar_found_in),
  sidebar_tracked_with_couriers = VALUES(sidebar_tracked_with_couriers),
  sidebar_pieces = VALUES(sidebar_pieces),
  sidebar_days_in_transit = VALUES(sidebar_days_in_transit),

  sidebar_json = VALUES(sidebar_json),
  timeline_json = VALUES(timeline_json),
  error_msg = VALUES(error_msg),

  tipo_servico = IF(VALUES(tipo_servico) IS NULL OR VALUES(tipo_servico) = '', tipo_servico, VALUES(tipo_servico)),

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

        // Ajuste os limites conforme o seu schema atual (vou usar valores comuns)
        var flightCode = ExtractFlightCode(r.LastFlight);
        cmd.Parameters.AddWithValue("@last_flight", (object?)flightCode ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@origin", (object?)Trunc(origin3, 10) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@destination", (object?)Trunc(dest3, 10) ?? DBNull.Value);

        cmd.Parameters.AddWithValue("@last_status_code", NullIfNA(r.LastStatusCode));
        cmd.Parameters.AddWithValue("@last_status_description", NullIfNA(r.LastStatusDescription));
        cmd.Parameters.AddWithValue("@last_status_timestamp", NullIfNA(r.Timestamp));

        var sb = (object?)r.Sidebar;
        cmd.Parameters.AddWithValue("@sidebar_tracking_number", TryGet(sb, "TrackingNumber"));
        cmd.Parameters.AddWithValue("@sidebar_from_text", TryGet(sb, "From"));
        cmd.Parameters.AddWithValue("@sidebar_to_text", TryGet(sb, "To"));
        cmd.Parameters.AddWithValue("@sidebar_origin_country", TryGet(sb, "OriginCountry"));
        cmd.Parameters.AddWithValue("@sidebar_destination_country", TryGet(sb, "DestinationCountry"));
        cmd.Parameters.AddWithValue("@sidebar_found_in", TryGet(sb, "FoundIn"));
        cmd.Parameters.AddWithValue("@sidebar_tracked_with_couriers", TryGet(sb, "TrackedWithCouriers"));
        cmd.Parameters.AddWithValue("@sidebar_pieces", TryGetInt(sb, "Pieces"));
        cmd.Parameters.AddWithValue("@sidebar_days_in_transit", TryGetInt(sb, "DaysInTransit"));

        cmd.Parameters.AddWithValue("@sidebar_json", (object?)sidebarJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@timeline_json", (object?)timelineJson ?? DBNull.Value);

        var err = TruncErr(r.Error, 1000); // escolha 1000 ou 2000
        cmd.Parameters.AddWithValue("@error_msg", (object?)err ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@tipo_servico", string.IsNullOrWhiteSpace(r.TipoServico) ? DBNull.Value : r.TipoServico);

        if (!string.IsNullOrWhiteSpace(r.Origin) && r.Origin.Length > 10)
            await Logging.LogAwbAsync(r.Awb, $"WARN origin_long len={r.Origin.Length} value={r.Origin}");

        if (!string.IsNullOrWhiteSpace(r.LastFlight) && r.LastFlight.Length > 30)
            await Logging.LogAwbAsync(r.Awb, $"WARN last_flight_long len={r.LastFlight.Length} value={r.LastFlight}");

        await cmd.ExecuteNonQueryAsync();

        await Logging.LogAwbAsync(r.Awb, "DB_UPSERT_END");
    }
}