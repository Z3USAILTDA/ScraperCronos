#nullable enable
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Threading.Channels;

static class Program
{

    private static readonly int MaxParallel = Math.Clamp(
        int.TryParse(Environment.GetEnvironmentVariable("SCRAPER_PARALLEL"), out var p) ? p : 2, 1, 6);

    private static readonly int FirecrawlTimeoutMs =
        int.TryParse(Environment.GetEnvironmentVariable("FIRECRAWL_TIMEOUT_MS"), out var t) ? t : 60000;

    private static readonly int ScrapeDoTimeoutMs =
        int.TryParse(Environment.GetEnvironmentVariable("SCRAPEDO_TIMEOUT_MS"), out var st) ? st : 60000;

    private static string FirecrawlBaseUrl =>
        (Environment.GetEnvironmentVariable("FIRECRAWL_BASE_URL") ?? "https://api.firecrawl.dev/v2").TrimEnd('/');

    private static string FirecrawlProxy =>
        (Environment.GetEnvironmentVariable("FIRECRAWL_PROXY") ?? "auto").Trim();

    private static string FirecrawlApiKey =>
        Environment.GetEnvironmentVariable("FIRECRAWL_API_KEY")
        ?? throw new InvalidOperationException("FIRECRAWL_API_KEY não configurada.");

    private static string ScraperProvider =>
        (Environment.GetEnvironmentVariable("SCRAPER_PROVIDER") ?? "firecrawl").Trim().ToLowerInvariant();

    private static string ScrapeDoApiKey =>
        Environment.GetEnvironmentVariable("SCRAPEDO_API_KEY")
        ?? throw new InvalidOperationException("SCRAPEDO_API_KEY não configurada.");

    private static bool ScrapeDoRender =>
        (Environment.GetEnvironmentVariable("SCRAPEDO_RENDER") ?? "true").Trim().Equals("true", StringComparison.OrdinalIgnoreCase);

    private static string ScrapeDoSuper =>
        (Environment.GetEnvironmentVariable("SCRAPEDO_SUPER") ?? "false").Trim().ToLowerInvariant();

    private static string ScrapeDoWaitUntil =>
    (Environment.GetEnvironmentVariable("SCRAPEDO_WAIT_UNTIL") ?? "networkidle2").Trim();

    private static int ScrapeDoCustomWaitMs =>
        int.TryParse(Environment.GetEnvironmentVariable("SCRAPEDO_CUSTOM_WAIT_MS"), out var cw) ? cw : 8000;

    private static bool ScrapeDoReturnJson =>
        (Environment.GetEnvironmentVariable("SCRAPEDO_RETURN_JSON") ?? "true").Trim().Equals("true", StringComparison.OrdinalIgnoreCase);

    private static bool ScrapeDoShowFrames =>
        (Environment.GetEnvironmentVariable("SCRAPEDO_SHOW_FRAMES") ?? "true").Trim().Equals("true", StringComparison.OrdinalIgnoreCase);

    // ── Fast-loop config ──────────────────────────────────────────────
    private static readonly int FastLoopDelaySeconds =
        int.TryParse(Environment.GetEnvironmentVariable("FAST_LOOP_DELAY_SECONDS"), out var fl) ? fl : 120;

    private static readonly int FastLoopLookbackSeconds =
        int.TryParse(Environment.GetEnvironmentVariable("FAST_LOOP_LOOKBACK_SECONDS"), out var lb) ? lb : 1800;

    private static readonly int FastLoopLookbackMinutes = FastLoopLookbackSeconds / 60;

    public static async Task<int> Main(string[] args)
    {
        Utils.LoadDotEnv(Path.Combine(Environment.CurrentDirectory, ".env"));
        Utils.LoadDotEnv(Path.Combine(AppContext.BaseDirectory, ".env"));

        Directory.CreateDirectory(Logging.LogsDir);

        var mainDelaySeconds = int.TryParse(Environment.GetEnvironmentVariable("LOOP_DELAY_SECONDS"), out var d) ? d : 30;

        // Se argumentos foram passados, executa uma vez e sai
        if (args.Length > 0)
        {
            await RunOnceAsync(args);
            return 0;
        }

        Console.Error.WriteLine($"[CONFIG] Loop Principal: {mainDelaySeconds}s | Loop Rápido: {FastLoopDelaySeconds}s (Lookback: {FastLoopLookbackMinutes}min)");

        // Executa os dois loops em paralelo
        var mainLoop = RunMainLoopAsync(mainDelaySeconds);
        var fastLoop = RunFastLoopAsync();

        await Task.WhenAll(mainLoop, fastLoop);

        return 0; // nunca alcançado
    }

    // ── Loop Principal (intervalo longo – todos os AWBs elegíveis) ────
    private static async Task RunMainLoopAsync(int delaySeconds)
    {
        while (true)
        {
            var cycleId = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
            Console.Error.WriteLine($"===== MAIN_CYCLE_START {cycleId} =====");

            try
            {
                var jobs = await Db.LoadJobsFromMariaDbAsync();
                if (jobs.Count > 0)
                    Console.WriteLine($"[MAIN] Rastreando {jobs.Count} AWBs...");

                await ProcessJobsAsync(jobs, "MAIN");

                Console.Error.WriteLine($"===== MAIN_CYCLE_OK {cycleId} =====");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"===== MAIN_CYCLE_FAIL {cycleId}: {ex} =====");
            }

            Console.Error.WriteLine($"===== MAIN_CYCLE_SLEEP {delaySeconds}s =====");
            await Task.Delay(TimeSpan.FromSeconds(delaySeconds));
        }
    }

    // ── Loop Rápido (intervalo curto – somente AWBs recentes) ─────────
    private static async Task RunFastLoopAsync()
    {
        while (true)
        {
            var cycleId = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
            Console.Error.WriteLine($"===== FAST_CYCLE_START {cycleId} =====");

            try
            {
                var jobs = await Db.LoadRecentJobsFromMariaDbAsync(FastLoopLookbackMinutes);
                if (jobs.Count > 0)
                    Console.WriteLine($"[FAST] Rastreando {jobs.Count} AWBs recentes (últimos {FastLoopLookbackMinutes}min)...");

                await ProcessJobsAsync(jobs, "FAST");

                Console.Error.WriteLine($"===== FAST_CYCLE_OK {cycleId} =====");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"===== FAST_CYCLE_FAIL {cycleId}: {ex} =====");
            }

            Console.Error.WriteLine($"===== FAST_CYCLE_SLEEP {FastLoopDelaySeconds}s =====");
            await Task.Delay(TimeSpan.FromSeconds(FastLoopDelaySeconds));
        }
    }

    

    /// <summary>
    /// Execução avulsa via CLI (argumentos manuais).
    /// </summary>
    private static async Task RunOnceAsync(string[] args)
    {
        Console.WriteLine($"[INFO] Provedor Atual: {ScraperProvider.ToUpper()}");

        var jobs = args.Select(Utils.NormalizeAwb)
                       .Where(x => x is not null)
                       .Cast<string>()
                       .Distinct(StringComparer.OrdinalIgnoreCase)
                       .Select(a => new TrackingJob { Awb = a })
                       .ToList();

        await ProcessJobsAsync(jobs, "CLI");
    }

    /// <summary>
    /// Pipeline compartilhado: recebe a lista de jobs já filtrada e executa scraping + persistência.
    /// O parâmetro <paramref name="tag"/> é usado nos logs para distinguir MAIN / FAST / CLI.
    /// </summary>
    private static async Task ProcessJobsAsync(List<TrackingJob> jobs, string tag)
    {
        Console.WriteLine($"[INFO] [{tag}] Provedor Atual: {ScraperProvider.ToUpper()}");

        if (jobs.Count == 0)
        {
            Console.Error.WriteLine($"[{tag}] Nenhum AWB válido para processar.");
            return;
        }

        var channel = Channel.CreateBounded<TrackingDetails>(new BoundedChannelOptions(500)
        {
            SingleReader = true,
            SingleWriter = false
        });

        var dbWriter = Task.Run(async () =>
        {
            await foreach (var result in channel.Reader.ReadAllAsync())
            {
                try
                {
                    await Logging.LogAwbAsync(result.Awb, "DB_WRITE_START");
                    await Db.SaveResultToMariaDbAsync(result);

                    if (string.IsNullOrWhiteSpace(result.Error))
                    {
                        Console.WriteLine($"[{tag}][AWB {result.Awb}] \u2705 SUCESSO | Origem: {result.Origin} -> Destino: {result.Destination} | Voo: {result.LastFlight} ({result.LastStatusCode}) | Timeline: {(result.Timeline?.Count ?? 0)} eventos.");
                    }
                    else
                    {
                        Console.WriteLine($"[{tag}][AWB {result.Awb}] \u26A0\uFE0F FALHA GERAL | Erro: {result.Error}");
                    }

                    await Logging.LogAwbAsync(result.Awb, "DB_WRITE_OK");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[{tag}][DB] erro ao salvar {result.Awb}: {ex}");
                    await Logging.LogAwbAsync(result.Awb, "DB_WRITE_FAIL", ex);
                }
            }
        });

        var sem = new SemaphoreSlim(MaxParallel, MaxParallel);

        var tasks = jobs.Select(async job =>
        {
            await sem.WaitAsync();

            try
            {
                await Logging.LogAwbAsync(job.Awb, "JOB_START");

                if (job.Awb.Equals("NI", StringComparison.OrdinalIgnoreCase) ||
                    (job.Hawbs != null && job.Hawbs.Any(h => string.Equals(job.Awb, h, StringComparison.OrdinalIgnoreCase))))
                {
                    await Logging.LogAwbAsync(job.Awb, "JOB_INVALID_AWB");
                    Console.WriteLine($"[{tag}][AWB {job.Awb}] \uD83D\uDEAB AWB inválido (NI ou igual ao HAWB). Ignorando scrape.");
                    
                    var invalidResult = TrackingDetails.Empty(job.Awb, "AWB inválido");
                    invalidResult.Hawbs = job.Hawbs ?? new List<string>();
                    invalidResult.TipoServico = job.TipoServico;
                    invalidResult.Source = "N/A";

                    await channel.Writer.WriteAsync(invalidResult);
                    return;
                }

                var sw = Stopwatch.StartNew();

                await Logging.LogAwbAsync(job.Awb, "SCRAPER_START");
                var result = await ScrapeWithRetryAsync(job.Awb);
                await Logging.LogAwbAsync(
                    job.Awb,
                    $"SCRAPE_RESULT origin={result.Origin} dest={result.Destination} flight={result.LastFlight} timeline_count={(result.Timeline == null ? -1 : result.Timeline.Count)}"
                );
                await Logging.LogAwbAsync(job.Awb, $"SCRAPER_DONE ms={sw.ElapsedMilliseconds}");

                result.Hawbs = job.Hawbs ?? new List<string>();
                result.TipoServico = job.TipoServico;

                await Logging.LogAwbAsync(
                    job.Awb,
                    $"SCRAPE_RESULT origin={result.Origin} dest={result.Destination} source={result.Source} timeline_count={(result.Timeline == null ? -1 : result.Timeline.Count)}"
                );

                await Logging.LogAwbAsync(job.Awb, "ENQUEUE_TO_DBWRITER");
                await channel.Writer.WriteAsync(result);

                await Logging.LogAwbAsync(job.Awb, "JOB_DONE");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[{tag}][{job.Awb}] erro: {ex}");
                await Logging.LogAwbAsync(job.Awb, "JOB_EXCEPTION", ex);

                var fail = TrackingDetails.Empty(job.Awb, $"Erro no job: {ex.Message}");
                fail.Hawbs = job.Hawbs;
                fail.TipoServico = job.TipoServico;

                try
                {
                    await Logging.LogAwbAsync(job.Awb, "ENQUEUE_FAIL_TO_DBWRITER");
                    await channel.Writer.WriteAsync(fail);
                }
                catch
                {
                }
            }
            finally
            {
                sem.Release();
            }
        }).ToList();

        await Task.WhenAll(tasks);

        channel.Writer.Complete();
        await dbWriter;
    }

    private static async Task<TrackingDetails> ScrapeWithRetryAsync(string awb)
{
    TrackingDetails? lastResult = null;

    string[] firecrawlProxies = new[] { "auto", "enhanced", "enhanced" };
    int maxAttempts = ScraperProvider == "scrapedo" ? 3 : firecrawlProxies.Length;

    for (int attempt = 1; attempt <= maxAttempts; attempt++)
    {
        string proxy = ScraperProvider == "scrapedo"
            ? (attempt >= 2 ? "super" : "standard")
            : firecrawlProxies[attempt - 1];

        Console.WriteLine($"[AWB {awb}] \u23F3 Raspando ({ScraperProvider}) -> Tentativa {attempt}/{maxAttempts} [Proxy: {proxy.ToUpper()}]");

        using var http = new HttpClient
        {
            Timeout = TimeSpan.FromMilliseconds(Math.Max(30000, Math.Max(FirecrawlTimeoutMs, ScrapeDoTimeoutMs) + 5000))
        };

        IScraperClient scraper;

        if (ScraperProvider == "scrapedo")
        {
            string superMode = attempt >= 2 ? "true" : "false";

            scraper = new ScrapeDoClient(
                http,
                ScrapeDoApiKey,
                ScrapeDoRender,
                superMode,
                ScrapeDoTimeoutMs,
                ScrapeDoWaitUntil,
                ScrapeDoCustomWaitMs,
                ScrapeDoReturnJson,
                ScrapeDoShowFrames
            );
        }
        else
        {
            http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", FirecrawlApiKey);

            scraper = new FirecrawlClient(
                http,
                FirecrawlBaseUrl,
                proxy,
                FirecrawlTimeoutMs
            );
        }

        var result = await scraper.ScrapeAsync(awb);
        lastResult = result;

        var hasValidStatus =
            !string.IsNullOrWhiteSpace(result.LastStatusCode) &&
            result.LastStatusCode != "UNK" &&
            result.LastStatusCode != "N/A";

        var hasTimeline =
            result.Timeline != null &&
            result.Timeline.Count > 0;

        var hasAnyCoreData =
            (!string.IsNullOrWhiteSpace(result.Origin) && result.Origin != "N/A") ||
            (!string.IsNullOrWhiteSpace(result.Destination) && result.Destination != "N/A") ||
            (!string.IsNullOrWhiteSpace(result.LastFlight) && result.LastFlight != "N/A");

        if (hasValidStatus || hasTimeline || hasAnyCoreData)
        {
            await Logging.LogAwbAsync(awb, $"SCRAPE_SUCCESS attempt={attempt}/{maxAttempts} proxy={proxy}");
            return result;
        }

        await Logging.LogAwbAsync(awb, $"SCRAPE_EMPTY attempt={attempt}/{maxAttempts} proxy={proxy}");

        if (attempt < maxAttempts)
        {
            var delayMs = Random.Shared.Next(4000, 7000);
            Console.WriteLine($"[AWB {awb}] \u21BA Tentativa {attempt} Insuficiente. Aguardando {delayMs/1000}s...");
            await Task.Delay(delayMs);
        }
    }

    return lastResult ?? TrackingDetails.Empty(awb, "Todas as tentativas de scraping falharam.");
}


}