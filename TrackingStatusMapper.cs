#nullable enable
using System.Globalization;

public static class TrackingStatusMapper
{
    private static readonly (string Keyword, string Code)[] StatusRules =
    {
        ("proof of delivery", "POD"),
        ("comprovante de entrega", "POD"),
        ("pod", "POD"),

        ("shipment delivered", "DLV"),
        ("delivery completed", "DLV"),
        ("delivered to the consignee", "DLV"),
        ("physically delivered to the consignee", "DLV"),
        ("consignment has been physically delivered", "DLV"),
        ("the consignment has been physically delivered", "DLV"),
        ("pieces delivered at", "DLV"),
        ("delivered (dlv)", "DLV"),
        ("shipment delivered.", "DLV"),
        ("shipment delivered", "DLV"),
        ("delivery", "DLV"),
        ("entregue ao consignatário", "DLV"),
        ("entregue ao destinatário", "DLV"),
        ("entrega concluída", "DLV"),
        ("entrega realizada", "DLV"),
        ("mercadoria entregue", "DLV"),
        ("carga entregue", "DLV"),
        ("entregue", "DLV"),
        ("delivered -", "DLV"),
        ("delivered,", "DLV"),
        ("delivered.", "DLV"),
        ("delivered", "DLV"),

        ("awb documentation has been delivered to the consignee", "AWD"),
        ("arrival documents delivered to consignee/agent", "AWD"),
        ("arrival documents delivered", "AWD"),
        ("documents delivered", "AWD"),
        ("document delivered", "AWD"),
        ("documents available on", "AWD"),
        ("awa documents available", "AWD"),
        ("documents available", "AWD"),
        ("arrived with documents", "AWD"),
        ("arrival with documents", "AWD"),
        ("documentos de chegada entregues", "AWD"),
        ("documentação awb entregue", "AWD"),
        ("documentação entregue", "AWD"),
        ("documentos entregues", "AWD"),
        ("documento entregue", "AWD"),
        ("documentos disponíveis", "AWD"),
        ("documentos disponíveis em", "AWD"),
        ("documentos disponíveis para retirada", "AWD"),
        ("(awa)", "AWD"),

        ("air waybill data received", "FWB"),
        ("documents received,", "FWB"),
        ("documents received", "FWB"),
        ("fwb.", "FWB"),
        ("fwb", "FWB"),
        ("dados do conhecimento aéreo recebidos", "FWB"),
        ("dados da awb recebidos", "FWB"),
        ("documentos recebidos", "FWB"),
        ("documentação recebida", "FWB"),

        ("agent has been notified of arrival of shipment", "NFD"),
        ("consignee/agent notified of arrival", "NFD"),
        ("cargo and documents ready for pick-up", "NFD"),
        ("arrival notification sent", "NFD"),
        ("consignee informed", "NFD"),
        ("agent notified", "NFD"),
        ("available for pickup", "NFD"),
        ("available for collection", "NFD"),
        ("ready for pick up", "NFD"),
        ("ready for pickup", "NFD"),
        ("ready for collection", "NFD"),
        ("notified for delivery", "NFD"),
        ("consignatário notificado", "NFD"),
        ("consignatario notificado", "NFD"),
        ("agente notificado", "NFD"),
        ("cliente notificado", "NFD"),
        ("notificação de chegada enviada", "NFD"),
        ("consignatário informado", "NFD"),
        ("destinatário informado", "NFD"),
        ("disponível para retirada", "NFD"),
        ("disponível para coleta", "NFD"),
        ("pronto para retirada", "NFD"),
        ("pronto para coleta", "NFD"),
        ("notificado", "NFD"),
        ("notified", "NFD"),

        ("consignment physically received from the flight", "RCF"),
        ("has arrived in the cargo bay at final destination", "RCF"),
        ("received from flight (rcf)", "RCF"),
        ("received from flight,", "RCF"),
        ("received from flight.", "RCF"),
        ("received from flight", "RCF"),
        ("recebida no destino (rcf)", "RCF"),
        ("recebido do voo", "RCF"),
        ("recebida do voo", "RCF"),
        ("recebido do vôo", "RCF"),
        ("recebida do vôo", "RCF"),
        ("recebido do flight", "RCF"),
        ("desembarcado no destino", "RCF"),
        ("rcf", "RCF"),

        ("entry processed:cbp general examination", "CUS"),
        ("customs inspection", "CUS"),
        ("customs processing", "CUS"),
        ("customs clearance processing", "CUS"),
        ("inspeção aduaneira", "CUS"),
        ("processamento aduaneiro", "CUS"),
        ("em processamento aduaneiro", "CUS"),
        ("em inspeção aduaneira", "CUS"),

        ("cleared by customs (ccd)", "CCD"),
        ("cleared by customs", "CCD"),
        ("liberado pela alfândega", "CCD"),
        ("liberado pela aduana", "CCD"),
        ("desembaraçado pela alfândega", "CCD"),
        ("ccd", "CCD"),

        ("delay handling", "DLH"),
        ("delayed handling", "DLH"),
        ("handling delay", "DLH"),
        ("atraso no manuseio", "DLH"),
        ("manuseio atrasado", "DLH"),
        ("atraso operacional", "DLH"),

        ("flight arrived.", "ARR"),
        ("cargo and documents arrived at", "ARR"),
        ("arrived at facility", "ARR"),
        ("arrived at terminal", "ARR"),
        ("arrived at hub", "ARR"),
        ("arrived,", "ARR"),
        ("arrived.", "ARR"),
        ("chegada ao destino", "ARR"),
        ("carga chegou ao destino", "ARR"),
        ("mercadoria chegou", "ARR"),
        ("chegou ao terminal", "ARR"),
        ("chegou ao hub", "ARR"),
        ("chegou", "ARR"),
        ("arrival", "ARR"),
        ("arrived", "ARR"),

        ("to be transferred to another airline", "TRM"),
        ("received from carrier/transfer", "RCT"),
        ("transferred", "TFD"),
        ("transfered", "TFD"),
        ("transfer", "TFD"),
        ("transferido", "TFD"),
        ("transferida", "TFD"),
        ("em transferência", "TFD"),
        ("a transferir para outra companhia aérea", "TRM"),

        ("cargo loaded", "CLD"),
        ("loaded on flight", "LOF"),
        ("loaded", "LOF"),
        ("carga carregada", "CLD"),
        ("carregado no voo", "LOF"),
        ("carregada no voo", "LOF"),
        ("embarcado no voo", "LOF"),
        ("embarcada no voo", "LOF"),

        ("flight departed.", "DEP"),
        ("departed on flight", "DEP"),
        ("cargo and documents departed at", "DEP"),
        ("departed from", "DEP"),
        ("departed (dep)", "DEP"),
        ("departed.", "DEP"),
        ("voo partido", "DEP"),
        ("voo partiu", "DEP"),
        ("partiu", "DEP"),
        ("partida", "DEP"),
        ("decolou", "DEP"),
        ("saiu do aeroporto", "DEP"),
        ("departure", "DEP"),
        ("departed", "DEP"),

        ("freight manifest", "FFM"),
        ("manifest sent", "FFM"),
        ("electronic manifest", "FFM"),
        ("included in manifest", "MAN"),
        ("manifested.", "MAN"),
        ("manifested", "MAN"),
        ("manifesto de carga", "FFM"),
        ("manifesto enviado", "FFM"),
        ("manifesto eletrônico", "FFM"),
        ("manifestado", "MAN"),
        ("carga manifestada", "MAN"),
        ("incluído no manifesto", "MAN"),
        ("manifest", "MAN"),

        ("received from shipper (rcs)", "RCS"),
        ("received from shipper", "RCS"),
        ("cargo received and accepted", "RCS"),
        ("received and accepted", "RCS"),
        ("accepted from shipper", "RCS"),
        ("ready for carriage", "RCS"),
        ("recebido do embarcador", "RCS"),
        ("recebida do embarcador", "RCS"),
        ("recebido e aceito", "RCS"),
        ("carga recebida e aceita", "RCS"),
        ("pronto para transporte", "RCS"),
        ("pronta para transporte", "RCS"),
        ("accepted", "RCS"),

        ("received from carrier/transfer", "RCT"),
        ("received from truck", "RCT"),
        ("received via truck", "RCT"),
        ("truck received", "RCT"),
        ("recebido da transportadora", "RCT"),
        ("recebida da transportadora", "RCT"),
        ("recebido por transferência", "RCT"),
        ("recebida por transferência", "RCT"),
        ("recebido via caminhão", "RCT"),
        ("recebida via caminhão", "RCT"),

        ("forwarded to a bonded warehouse", "FBW"),
        ("freight booked with carrier", "FBW"),
        ("booked with carrier", "FBW"),
        ("shipment booked", "BKD"),
        ("planned for flight", "BKD"),
        ("latest booking update", "BKD"),
        ("booking confirmed", "BKD"),
        ("space confirmed", "BKD"),
        ("reserva confirmada", "BKD"),
        ("reserva efetuada", "BKD"),
        ("embarque reservado", "BKD"),
        ("planejado para voo", "BKD"),
        ("planejada para voo", "BKD"),
        ("espaço confirmado", "BKD"),
        ("booking atualizado", "BKD"),
        ("shipment booked", "BKD"),
        ("booked.", "BKD"),
        ("booked ", "BKD"),
        ("booked", "BKD")
    };

    public static string MapStatusCode(string description)
    {
        var normalized = (description ?? "").Trim().ToLowerInvariant();

        foreach (var (keyword, code) in StatusRules)
        {
            if (normalized.Contains(keyword))
                return code;
        }

        return "";
    }

    public static string GetLastStatusCode(IEnumerable<TrackingEvent>? events)
    {
        if (events is null)
            return "";

        var orderedEvents = events
            .Where(e => e is not null)
            .OrderByDescending(e => ParseTimestamp(e.Timestamp) ?? DateTime.MinValue)
            .ToList();

        if (orderedEvents.Count == 0)
            return "";

        return MapStatusCode(orderedEvents[0].Description ?? "");
    }

    private static DateTime? ParseTimestamp(string? timestamp)
    {
        if (string.IsNullOrWhiteSpace(timestamp))
            return null;

        var formats = new[]
        {
            "dd MMM yyyy HH:mm",
            "dd MMM yyyy",
            "dd/MM/yyyy HH:mm:ss",
            "dd/MM/yyyy HH:mm",
            "dd/MM/yyyy",
            "yyyy-MM-dd HH:mm:ss",
            "yyyy-MM-dd HH:mm",
            "yyyy-MM-dd"
        };

        if (DateTime.TryParseExact(
                timestamp.Trim(),
                formats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal,
                out var parsedExact))
        {
            return parsedExact;
        }

        if (DateTime.TryParse(
                timestamp.Trim(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal,
                out var parsed))
        {
            return parsed;
        }

        return null;
    }
}
