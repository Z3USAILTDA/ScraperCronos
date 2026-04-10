#nullable enable
public interface IScraperClient
{
    Task<TrackingDetails> ScrapeAsync(string awb);
}