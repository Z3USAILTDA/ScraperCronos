// Models.cs
#nullable enable

public sealed class TrackingJob
{
    public string Awb { get; set; } = "";
    public List<string> Hawbs { get; set; } = new();
    public string? TipoServico { get; set; }
}

public sealed class TrackingEvent
{
    public string Timestamp { get; set; } = "N/A";
    public string Description { get; set; } = "N/A";
    public string Location { get; set; } = "N/A";
    public string Carrier { get; set; } = "N/A";
}

public sealed class SidebarSummary
{
    public string TrackingNumber { get; set; } = "N/A";
    public string From { get; set; } = "N/A";
    public string To { get; set; } = "N/A";
    public string OriginCountry { get; set; } = "N/A";
    public string DestinationCountry { get; set; } = "N/A";
    public string FoundIn { get; set; } = "N/A";
    public string TrackedWithCouriers { get; set; } = "N/A";
    public string Pieces { get; set; } = "N/A";
    public string DaysInTransit { get; set; } = "N/A";
    public List<string> FlightLegs { get; set; } = new();
}

public sealed class TrackingDetails
{
    public string Awb { get; set; } = "";
    public List<string> Hawbs { get; set; } = new();
    public string? TipoServico { get; set; }

    public string LastFlight { get; set; } = "N/A";
    public string Origin { get; set; } = "N/A";
    public string Destination { get; set; } = "N/A";

    public string LastStatusCode { get; set; } = "N/A";
    public string LastStatusDescription { get; set; } = "N/A";
    public string Timestamp { get; set; } = "N/A";

    public List<TrackingEvent>? Timeline { get; set; } = new();

    public string? Error { get; set; }

    public SidebarSummary Sidebar { get; set; } = new();

    public static TrackingDetails Empty(string awb, string error) => new()
    {
        Awb = awb,
        Error = error,
        LastFlight = "N/A",
        Origin = "N/A",
        Destination = "N/A",
        LastStatusCode = "N/A",
        LastStatusDescription = "N/A",
        Timestamp = "N/A",
        Timeline = new List<TrackingEvent>()
    };
}