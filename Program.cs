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

    private static string FirecrawlBaseUrl =>
        (Environment.GetEnvironmentVariable("FIRECRAWL_BASE_URL") ?? "https://api.firecrawl.dev/v2").TrimEnd('/');

    private static string FirecrawlProxy =>
        (Environment.GetEnvironmentVariable("FIRECRAWL_PROXY") ?? "auto").Trim();

    private static string FirecrawlApiKey =>
        Environment.GetEnvironmentVariable("FIRECRAWL_API_KEY")
        ?? throw new InvalidOperationException("FIRECRAWL_API_KEY não configurada.");

    public static async Task<int> Main(string[] args)
    {
        // Carrega env uma vez
        Utils.LoadDotEnv(Path.Combine(Environment.CurrentDirectory, ".env"));
        Utils.LoadDotEnv(Path.Combine(AppContext.BaseDirectory, ".env"));

        Directory.CreateDirectory(Logging.LogsDir);

        var delaySeconds = int.TryParse(Environment.GetEnvironmentVariable("LOOP_DELAY_SECONDS"), out var d) ? d : 30;

        while (true)
        {
            var cycleId = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
            Console.Error.WriteLine($"===== CYCLE_START {cycleId} =====");

            try
            {
                await RunOnceAsync(args);
                Console.Error.WriteLine($"===== CYCLE_OK {cycleId} =====");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"===== CYCLE_FAIL {cycleId}: {ex} =====");
            }

            Console.Error.WriteLine($"===== CYCLE_SLEEP {delaySeconds}s =====");
            await Task.Delay(TimeSpan.FromSeconds(delaySeconds));
        }
    }

    private static async Task RunOnceAsync(string[] args)
    {
        Console.Error.WriteLine("CWD=" + Environment.CurrentDirectory);
        Console.Error.WriteLine("BASE=" + AppContext.BaseDirectory);
        Console.Error.WriteLine("ENV(DB_HOST)=" + Environment.GetEnvironmentVariable("DB_HOST"));
        Console.Error.WriteLine("ENV(DB_NAME)=" + Environment.GetEnvironmentVariable("DB_NAME"));
        Console.Error.WriteLine("ENV(DB_USER)=" + Environment.GetEnvironmentVariable("DB_USER"));
        Console.Error.WriteLine($"PARALLEL={MaxParallel}");
        Console.Error.WriteLine($"FIRECRAWL_BASE_URL={FirecrawlBaseUrl} PROXY={FirecrawlProxy} TIMEOUT_MS={FirecrawlTimeoutMs}");
        Console.Error.WriteLine("FIRECRAWL_API_KEY set? " + (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FIRECRAWL_API_KEY"))));

        // Jobs
        List<TrackingJob> jobs;
        if (args.Length > 0)
        {
            jobs = args.Select(Utils.NormalizeAwb)
                       .Where(x => x is not null)
                       .Cast<string>()
                       .Distinct(StringComparer.OrdinalIgnoreCase)
                       .Select(a => new TrackingJob { Awb = a })
                       .ToList();
        }
        else
        {
            Console.Error.WriteLine("Sem AWBs na CLI. Buscando no MariaDB (AWB + HAWBs)...");
            jobs = await Db.LoadJobsFromMariaDbAsync();
            Console.Error.WriteLine($"Jobs carregados: {jobs.Count}");
        }

        if (jobs.Count == 0)
        {
            Console.Error.WriteLine("Nenhum AWB válido para processar.");
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
                    await Logging.LogAwbAsync(result.Awb, "DB_WRITE_OK");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[DB] erro ao salvar {result.Awb}: {ex}");
                    await Logging.LogAwbAsync(result.Awb, "DB_WRITE_FAIL", ex);
                }
            }
        });

        using var http = new HttpClient
        {
            Timeout = TimeSpan.FromMilliseconds(Math.Max(30000, FirecrawlTimeoutMs + 5000))
        };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", FirecrawlApiKey);

        var firecrawl = new FirecrawlClient(http, FirecrawlBaseUrl, FirecrawlProxy, FirecrawlTimeoutMs);
        var sem = new SemaphoreSlim(MaxParallel, MaxParallel);

        var tasks = jobs.Select(async job =>
        {
            await sem.WaitAsync();
            try
            {
                await Logging.LogAwbAsync(job.Awb, "JOB_START");

                var sw = Stopwatch.StartNew();
                await Logging.LogAwbAsync(job.Awb, "FIRECRAWL_START");

                var result = await firecrawl.ScrapeParcelsAsync(job.Awb);

                await Logging.LogAwbAsync(job.Awb, $"FIRECRAWL_DONE ms={sw.ElapsedMilliseconds}");

                result.Hawbs = job.Hawbs;
                result.TipoServico = job.TipoServico;

                await Logging.LogAwbAsync(job.Awb, "ENQUEUE_TO_DBWRITER");
                await channel.Writer.WriteAsync(result);

                await Logging.LogAwbAsync(job.Awb, "JOB_DONE");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[{job.Awb}] erro: {ex}");
                await Logging.LogAwbAsync(job.Awb, "JOB_EXCEPTION", ex);

                var fail = TrackingDetails.Empty(job.Awb, $"Erro no job: {ex.Message}");
                fail.Hawbs = job.Hawbs;
                fail.TipoServico = job.TipoServico;

                try
                {
                    await Logging.LogAwbAsync(job.Awb, "ENQUEUE_FAIL_TO_DBWRITER");
                    await channel.Writer.WriteAsync(fail);
                }
                catch { }
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
}