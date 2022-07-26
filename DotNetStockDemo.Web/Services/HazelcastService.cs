using System.Globalization;
using Hazelcast.Core;
using Hazelcast;

namespace DotNetStockDemo.Web.Services;

internal class HazelcastService : IAsyncDisposable
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly DemoOptions _options;
    private IHazelcastClient? _client;

    public HazelcastService(DemoOptions options, ILoggerFactory loggerFactory)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _logger = loggerFactory.CreateLogger<HazelcastService>();
    }

    public IHazelcastClient Client => _client ?? throw new InvalidOperationException("No client");

    public async ValueTask DisposeAsync()
    {
        // first version is not thread-safe as it is compiled as
        // if (_client != null) await _client.DisposeAsync()
        // and what if _client changes? second version captures
        // the variable before doing the comparison. duh.

        //if (_client is not null) 
        if (_client is IAsyncDisposable disposable)
            await disposable.DisposeAsync();
    }

    public async Task Connect()
    {
        var options = new HazelcastOptionsBuilder()
            .With(o =>
            {
                o.ClusterName = _options.HazelcastClusterName;
                o.Networking.SmartRouting = false; // FIXME because the Docker member will only report its internal address :(
                o.Networking.Addresses.Add($"{_options.HazelcastServer}:{_options.HazelcastPort}");
                o.Networking.ConnectionRetry.ClusterConnectionTimeoutMilliseconds = 1000;

                o.LoggerFactory.Creator = () => _loggerFactory;
            })
            .Build();

        _logger.LogInformation($"Connect to Hazelcast at {_options.HazelcastServer}:{_options.HazelcastPort}");

        _client = await HazelcastClientFactory.StartNewClientAsync(options);
    }

    public async Task Initialize()
    {
        if (_client == null) throw new InvalidOperationException("No client");

        _logger.LogInformation("Create TRADES mapping");
        var rc = await _client.Sql.ExecuteCommandAsync(CreateTradesMapping);

        _logger.LogInformation("Create COMPANIES mapping");
        // NOTE: replacing the mapping does not purge the map + code to destroy without creating exists but is internal
        var m = await _client.GetMapAsync<object, object>("companies");
        await _client.DestroyAsync(m);
        rc = await _client.Sql.ExecuteCommandAsync(CreateCompaniesMapping);

        _logger.LogInformation("Insert COMPANIES data");
        await using (var map = await _client.GetMapAsync<string, HazelcastJsonValue>("companies"))
        {
            var values = Stocks.All
                .Select(s => (s.Ticker, new HazelcastJsonValue($"{{ \"ticker\": \"{s.Ticker}\", \"name\": \"{s.Name}\", \"cap\": {s.Cap.ToString(CultureInfo.InvariantCulture)} }}")))
                .ToDictionary(x => x.Item1, x => x.Item2);

            await map.SetAllAsync(values);
        }

        _logger.LogInformation("Create TRADE_MAP mapping");
        // NOTE: replacing the mapping does not purge the map + code to destroy without creating exists but is internal
        m = await _client.GetMapAsync<object, object>("trade_map");
        await _client.DestroyAsync(m);
        rc = await _client.Sql.ExecuteCommandAsync(CreateTradesMapMapping);

        _logger.LogInformation("Create INGEST_TRADES job");
        rc = await _client.Sql.ExecuteCommandAsync("DROP JOB IF EXISTS ingest_trades");
        rc = await _client.Sql.ExecuteCommandAsync(CreateIngestTradesJob);
    }

    private string CreateTradesMapping => $@"CREATE OR REPLACE MAPPING trades (
    id     BIGINT,
    ticker VARCHAR,
    price  DECIMAL,
    qty    BIGINT
)
TYPE Kafka
OPTIONS (
    'valueFormat' = 'json-flat',
    'bootstrap.servers' = '{_options.KafkaServer}:{_options.KafkaPort}'
);";

    private const string CreateCompaniesMapping = @"CREATE OR REPLACE MAPPING companies (
    ticker VARCHAR,
    name   VARCHAR,
    cap    DECIMAL
)
TYPE IMAP
OPTIONS (
    'keyFormat'='varchar',
    'valueFormat'='json-flat'
);";

    private const string CreateTradesMapMapping = @"CREATE OR REPLACE MAPPING trade_map (
    id     BIGINT,
    ticker VARCHAR,
    name   VARCHAR,
    price  DECIMAL,
    qty    BIGINT
)
TYPE IMAP
OPTIONS (
    'keyFormat'='bigint',
    'valueFormat'='json-flat'
);";

    // alternatively with compact serialization: 'valueFormat'='compact','valueCompactTypeName'='trades_map_entry'

    // use SINK INTO (vs INSERT INTO) to support overwriting / replacing rows
    private const string CreateIngestTradesJob = @"CREATE JOB ingest_trades AS
SINK INTO trade_map (__key, id, ticker, name, price, qty)
SELECT trades.id, trades.id, trades.ticker, companies.name, trades.price, trades.qty
FROM trades
JOIN companies
ON companies.ticker = trades.ticker;";
}