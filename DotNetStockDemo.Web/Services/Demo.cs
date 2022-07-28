using DotNetStockDemo.Web.Hubs;
using Hazelcast;
using Hazelcast.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace DotNetStockDemo.Web.Services;

public class Demo : IHostedService
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly DemoOptions _options;
    private readonly CancellationTokenSource _cancellation;
    private readonly IHubContext<TradeHub> _hubContext;
    private Task? _runningDemo;

    public Demo(IOptions<DemoOptions> ioptions, IHubContext<TradeHub> hubContext, ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _logger = _loggerFactory.CreateLogger<Demo>();
        _options = ioptions.Value;
        _hubContext = hubContext ?? throw new ArgumentNullException(nameof(hubContext));
        _cancellation = new CancellationTokenSource();
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Demo is starting.");
        _runningDemo = Run(_cancellation.Token);
        _logger.LogInformation("Demo has started.");
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Demo is stopping.");
        if (_runningDemo != null)
        {
            _cancellation.Cancel();
            try
            {
                await _runningDemo.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            { }
            catch (Exception e)
            {
                _logger.LogError(e, "Demo has failed.");
            }
        }
        _logger.LogInformation("Demo has stopped.");
    }

    public async Task Run(CancellationToken cancellationToken)
    {
        KafkaService kafkaService = default!;
        HazelcastService hazelcastService = default!;

        // need to wait for long enough - give time for everything to setup in docker
        var timeout = TimeSpan.FromMinutes(4);

        var getServices = Task.Run(async () =>
        {
            // ensure Kafka first else Hazelcast JET job cannot run
            // we have no timeout on Kafka - this will hang until we can connect
            kafkaService = new KafkaService(_options, _loggerFactory);
            await kafkaService.Initialize().ConfigureAwait(false);
            await kafkaService.PurgeTopic().ConfigureAwait(false);

            // now it's OK to talk to Hazelcast
            hazelcastService = new HazelcastService(_options, (int)timeout.TotalMilliseconds, _loggerFactory);
            await hazelcastService.Connect().ConfigureAwait(false);
            await hazelcastService.Initialize().ConfigureAwait(false);
        });

        try
        {
            var timeoutTask = Task.Delay(timeout, cancellationToken);
            var task = await Task.WhenAny(getServices, timeoutTask).ConfigureAwait(false);
            if (task == timeoutTask)
            {
                // we are not going to wait for services - maybe the whole things hangs
                _logger.LogError("Timeout when getting services - the demo is NOT running");
                return;
            }

            // handles exceptions
            await getServices.ConfigureAwait(false);
            if (kafkaService == null || hazelcastService == null)
                throw new Exception("Missing services.");

            // handles cancellation
            cancellationToken.ThrowIfCancellationRequested();

            // run!
            _logger.LogInformation("Initialization completed, now running");
            var feedKafka = Try(() => FeedKafka(kafkaService, cancellationToken));
            var readHazelcast = Try(() => ReadTradeMap(hazelcastService.Client, cancellationToken));
            var readTrades = Try(() => ReadTrades(hazelcastService.Client, cancellationToken));

            // wait
            await Task.WhenAll(feedKafka, readHazelcast, readTrades).ConfigureAwait(false);
        }
        finally
        {
            _logger.LogInformation("Tearing down services");
            try { kafkaService?.Dispose(); } catch { }
            if (hazelcastService != null) try { await hazelcastService.DisposeAsync().ConfigureAwait(false); } catch { }
;        }
    }

    private async Task Try(Func<Task> f)
    {
        try
        {
            await f().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Exception! Aborting all tasks...");
            _cancellation.Cancel();
            throw;
        }
    }

    private async Task FeedKafka(KafkaService kafkaService, CancellationToken cancellationToken)
    {
        var rand = new Random();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ticker = Stocks.All[rand.Next(0, Stocks.All.Length)].Ticker;
            var price = 50f + rand.NextSingle() * 100;
            var qty = rand.Next(20, 1000);
            await kafkaService.SendTrade(ticker, price, qty).ConfigureAwait(false);
            await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ReadTradeMap(IHazelcastClient hazelcastClient, CancellationToken cancellationToken)
    {
        var lastId = -1L;

        while (true)
        {
            // if lastId is -1 we get an overflow exception, if it's zero we get a '0 parameters' exception
            // there is something obviously wrong in our client code here?!
            //var result = await hazelcastClient.Sql.ExecuteQueryAsync("SELECT id, ticker, name, price, qty FROM trade_map WHERE id > ?", new object[ lastId ]);
            var result = await hazelcastClient.Sql.ExecuteQueryAsync($"SELECT id, ticker, name, price, qty FROM trade_map WHERE id > {lastId}").ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            await foreach (var row in result.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    var id = row.GetColumn<long>("id");
                    var ticker = row.GetColumn<string>("ticker");
                    var name = row.GetColumn<string>("name");
                    var price = row.GetColumn<HBigDecimal>("price").ToDouble(); // assuming it won't overflow, but cannot send HBigDecimal to Javascript
                    var qty = row.GetColumn<long>("qty");

                    _logger.LogInformation($"trade_map:{id} {ticker} {name} {price:F3} {qty}");
                    await _hubContext.Clients.All.SendAsync("ReceiveTrade", id, ticker, name, qty, price, true, 0).ConfigureAwait(false);
                    lastId = Math.Max(lastId, id);

                }
                catch (Exception e)
                {
                    _logger.LogError($"  trade_map! {e.GetType()} {e.Message}");
                }
            }

            await Task.Delay(1000).ConfigureAwait(false); // throttle
        }
    }

    private async Task ReadTrades(IHazelcastClient hazelcastClient, CancellationToken cancellationToken)
    {
        var result = await hazelcastClient.Sql.ExecuteQueryAsync("SELECT * FROM trades").ConfigureAwait(false);

        // no need for a while (true) loop here: reading from trades is blocking
        await foreach (var row in result.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                var id = row.GetColumn<long>("id");
                var ticker = row.GetColumn<string>("ticker");
                var price = row.GetColumn<HBigDecimal>("price");
                var qty = row.GetColumn<long>("qty");

                _logger.LogInformation($"trades:{id} {ticker} {price.ToString(3)} {qty}");
            }
            catch (Exception e)
            {
                _logger.LogError($"  trades! {e.GetType()} {e.Message}");
            }
        }
    }
}