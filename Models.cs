// Models.cs
#nullable enable
using System.Text.Json.Serialization;

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

    public string Origin { get; set; } = "N/A";
    public string Destination { get; set; } = "N/A";

    public string LastFlight { get; set; } = "N/A";
    public string Source { get; set; } = "N/A";

    public List<TrackingEvent>? Timeline { get; set; } = new();

    public string? Error { get; set; }
    public string LastStatusCode { get; set; } = "N/A";

    public static TrackingDetails Empty(string awb, string error) => new()
    {
        Awb = awb,
        Error = error,
        Origin = "N/A",
        Destination = "N/A",
        LastFlight = "N/A",
        LastStatusCode = "N/A",
        Source = "N/A",
        Timeline = new List<TrackingEvent>()
    };
}