// Utils.cs
#nullable enable
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

public static class Utils
{
    public static void LoadDotEnv(string filePath)
    {
        if (!File.Exists(filePath)) return;

        foreach (var rawLine in File.ReadAllLines(filePath))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#")) continue;

            var idx = line.IndexOf('=');
            if (idx <= 0) continue;

            var key = line[..idx].Trim();
            var value = line[(idx + 1)..].Trim().Trim('"');

            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(key)))
                Environment.SetEnvironmentVariable(key, value);
        }
    }

    public static bool HasAny(string text, params string[] needles)
    {
        foreach (var n in needles)
            if (text.Contains(n)) return true;
        return false;
    }

    public static string Normalize(string s)
    {
        s = (s ?? "").Trim().ToLowerInvariant();

        var formD = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(formD.Length);
        foreach (var ch in formD)
        {
            var uc = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (uc != UnicodeCategory.NonSpacingMark)
                sb.Append(ch);
        }
        s = sb.ToString().Normalize(NormalizationForm.FormC);

        s = Regex.Replace(s, @"\s+", " ");
        return s;
    }

    public static string? NormalizeAwb(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var digits = Regex.Replace(raw, @"\D+", "");
        if (digits.Length < 11) return null;

        return $"{digits.Substring(0, 3)}-{digits.Substring(3)}";
    }

    public static string? ExtractFirstIata(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = Regex.Match(text, @"\b([A-Z]{3})\b");
        return m.Success ? m.Groups[1].Value.ToUpperInvariant() : null;
    }

    public static string InferAirStatusCode(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "UNK";
        var t = Normalize(raw);

        if (HasAny(t, "proof of delivery", "pod", "comprovante de entrega", "assinad", "received by", "signed by")) return "POD";
        if (HasAny(t, "delivered", "entregue", "entrega realizada", "delivery completed", "delivered to")) return "DLV";
        if (HasAny(t, "available for pickup", "ready for pickup", "available for collection",
            "notified", "notification sent", "avisado", "notificacao", "disponivel para retirada",
            "cargo available", "available at")) return "NFD";
        if (HasAny(t, "received from flight", "rcf", "unloaded", "offloaded", "discharged",
            "descarg", "descarreg", "recebido do voo", "received after flight")) return "RCF";
        if (HasAny(t, "arrived with documents", "awd", "arrived w/ documents",
            "chegou com documentos", "chegada com documentos", "documents received at destination")) return "AWD";
        if (HasAny(t, "arrived at facility", "arrived at terminal", "arrived at hub", "arrived at airport",
            "arrived at", "chegou no terminal", "chegou no hub", "chegou no aeroporto", "chegada no")) return "ARR";
        if (HasAny(t, "delay handling", "dlh", "processing delay", "delayed handling",
            "atraso no processamento", "atraso no manuseio", "delay in handling", "delayed in")) return "DLH";
        if (HasAny(t, "transferred", "transfer", "tfd", "tranship", "transshipment", "forwarded",
            "transferido", "transferencia", "conexao", "em conexao", "handover to airline")) return "TFD";
        if (HasAny(t, "loaded on flight", "lof", "loaded", "on board", "onboard",
            "carregado no voo", "embarcado no voo", "loaded onto aircraft")) return "LOF";
        if (HasAny(t, "departed", "departure", "dep", "decolou", "partiu", "saiu do aeroporto", "saiu da origem",
            "left origin", "flight departed", "departed from")) return "DEP";
        if (HasAny(t, "freight manifest", "ffm", "manifest information sent", "edi sent",
            "manifesto enviado", "informacao de manifesto enviada", "manifest data sent")) return "FFM";
        if (HasAny(t, "manifested", "man", "included in manifest", "in manifest",
            "incluido no manifesto", "incluida no manifesto")) return "MAN";
        if (HasAny(t, "received from shipper", "rcs", "received by terminal", "accepted at origin",
            "received and accepted", "aceito no terminal", "recebido do embarcador", "recebido e aceito")) return "RCS";
        if (HasAny(t, "received from truck", "rct", "received by truck", "truck received",
            "recebido via caminhao", "recebido do caminhao", "chegada por caminhao")) return "RCT";
        if (HasAny(t, "freight booked with carrier", "fbw", "booked with carrier", "pre booked",
            "reservado na companhia", "carga reservada na companhia", "booking with carrier")) return "FBW";
        if (HasAny(t, "booked", "bkd", "space confirmed", "booking confirmed",
            "reserva confirmada", "espaco confirmado")) return "BKD";

        return "UNK";
    }
}