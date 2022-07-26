// ReSharper disable StringLiteralTypo

using Confluent.Kafka;
using Confluent.Kafka.Admin;
using DotNetStockDemo;
using Hazelcast;
using Hazelcast.Core;
using Hazelcast.Models;

const string kafkaServer = "192.168.1.189";
const string kafkaPort = "9092";
const string kafkaTopicName = "trades";

const string hazelcastServer = "192.168.1.89";
const string hazelcastPort = "5701";
const string hazelcastClusterName = "dotnet-stock-demo";

var kafkaIndex = 0;

// ----------------------------------------------------------------

Console.WriteLine();
Console.WriteLine("BEGIN");

var options = new HazelcastOptionsBuilder()
    .With(o =>
    {
        o.ClusterName = hazelcastClusterName;
        o.Networking.SmartRouting = false; // FIXME because the Docker member will only report its internal address :(
        o.Networking.Addresses.Add($"{hazelcastServer}:{hazelcastPort}");
        o.Networking.ConnectionRetry.ClusterConnectionTimeoutMilliseconds = 1000;
    })
    .WithConsoleLogger()
    .Build();

await InitializeKafka();
await PurgeKafka();

await using (var client = await HazelcastClientFactory.StartNewClientAsync(options))
{
    await InitializeHazelcast(client);
    await FeedAndRead(client);

    Console.WriteLine("");
    await using (var map = await client.GetMapAsync<string, HazelcastJsonValue>("trade_map"))
    {
        var count = await map.GetSizeAsync();
        Console.WriteLine($"TradeMap Count={count}");
    }
    await using (var map = await client.GetMapAsync<string, HazelcastJsonValue>("trades"))
    {
        var count = await map.GetSizeAsync();
        Console.WriteLine($"Trades Count={count}"); // will be zero
    }
}

Console.WriteLine();
Console.WriteLine("END");

// ----------------------------------------------------------------

const string createTradesMapping = $@"CREATE OR REPLACE MAPPING trades (
    id     BIGINT,
    ticker VARCHAR,
    price  DECIMAL,
    qty    BIGINT
)
TYPE Kafka
OPTIONS (
    'valueFormat' = 'json-flat',
    'bootstrap.servers' = '{kafkaServer}:{kafkaPort}'
);";

const string createCompaniesMapping = @"CREATE OR REPLACE MAPPING companies (
    ticker VARCHAR,
    name   VARCHAR,
    cap    DECIMAL
)
TYPE IMAP
OPTIONS (
    'keyFormat'='varchar',
    'valueFormat'='json-flat'
);";

const string createTradesMapMapping = @"CREATE OR REPLACE MAPPING trade_map (
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
const string createIngestTradesJob = @"CREATE JOB ingest_trades AS
SINK INTO trade_map (__key, id, ticker, name, price, qty)
SELECT trades.id, trades.id, trades.ticker, companies.name, trades.price, trades.qty
FROM trades
JOIN companies
ON companies.ticker = trades.ticker;";

// ----------------------------------------------------------------

async Task InitializeHazelcast(IHazelcastClient client)
{
    Console.WriteLine();
    Console.WriteLine("Create TRADES mapping");
    var rc = await client.Sql.ExecuteCommandAsync(createTradesMapping);
    Console.WriteLine($"RC={rc}");

    Console.WriteLine();
    Console.WriteLine("Create COMPANIES mapping");
    // FIXME replacing the mapping does not purge the map?
    // NOTE: code to destroy without creating exists but is internal
    var m = await client.GetMapAsync<object, object>("companies");
    await client.DestroyAsync(m);
    rc = await client.Sql.ExecuteCommandAsync(createCompaniesMapping);
    Console.WriteLine($"RC={rc}");

    Console.WriteLine();
    Console.WriteLine("Insert COMPANIES data");
    await using (var map = await client.GetMapAsync<string, HazelcastJsonValue>("companies"))
    {
        await map.SetAllAsync(new Dictionary<string, HazelcastJsonValue>
        {
            { "GOOG", new HazelcastJsonValue("{ \"ticker\": \"GOOG\", \"name\": \"Google\", \"cap\": 123456.789 }") },
            { "APPL", new HazelcastJsonValue("{ \"ticker\": \"APPL\", \"name\": \"Apple\", \"cap\": 654321.987 }") },
        });
        var count = await map.GetSizeAsync();
        Console.WriteLine($"Count={count}");
    }

    Console.WriteLine();
    Console.WriteLine("Create TRADE_MAP mapping");
    // FIXME replacing the mapping does not purge the map?
    // NOTE: code to destroy without creating exists but is internal
    m = await client.GetMapAsync<object, object>("trade_map"); 
    await client.DestroyAsync(m);
    rc = await client.Sql.ExecuteCommandAsync(createTradesMapMapping);
    Console.WriteLine($"RC={rc}");

    Console.WriteLine();
    Console.WriteLine("Create INGEST_TRADES job");
    rc = await client.Sql.ExecuteCommandAsync("DROP JOB IF EXISTS ingest_trades");
    Console.WriteLine($"RC={rc}");
    rc = await client.Sql.ExecuteCommandAsync(createIngestTradesJob);
    Console.WriteLine($"RC={rc}");
}

async Task ReadFromTrades(IHazelcastClient client, CancellationToken token)
{
    Console.WriteLine();
    Console.WriteLine("Read TRADES");

    var result = await client.Sql.ExecuteQueryAsync("SELECT * FROM trades");

    await foreach (var row in result.WithCancellation(token))
    {
        try
        {
            Console.WriteLine($"  trades:{row.GetColumn<long>("id"):0000} {row.GetColumn<string>("ticker")} {row.GetColumn<HBigDecimal>("price").ToString(3)} {row.GetColumn<long>("qty")}");
        }
        catch (Exception e)
        {
            Console.WriteLine($"  trades! {e.GetType()} {e.Message}");
        }
    }

    Console.WriteLine("Done TRADES");
}

async Task ReadFromTradeMap(IHazelcastClient client, CancellationToken token)
{
    Console.WriteLine();
    Console.WriteLine("Read TRADE_MAP");

    var result = await client.Sql.ExecuteQueryAsync("SELECT id, ticker, name, price, qty FROM trade_map");

    await foreach (var row in result.WithCancellation(token))
    {
        try
        {
            Console.WriteLine($"  trade_map:{row.GetColumn<long>("id"):0000} {row.GetColumn<string>("ticker")} {row.GetColumn<string>("name")} {row.GetColumn<HBigDecimal>("price").ToString(3)} {row.GetColumn<long>("qty")}");
        }
        catch (Exception e)
        {
            Console.WriteLine($"  trade_map! {e.GetType()} {e.Message}");
        }
    }

    Console.WriteLine("Done TRADE_MAP");
}

async Task FeedAndRead(IHazelcastClient client)
{
    // should return immediately with whatever is in the trade_map
    await ReadFromTradeMap(client, CancellationToken.None);

    // reading from trades will stream and not return until canceled
    var cancellation = new CancellationTokenSource();
    var readingFromTrades = ReadFromTrades(client, cancellation.Token);

    // now push some data into Kafka
    await Task.Delay(1000);
    await FeedKafka(10);
    await Task.Delay(1000);
    await FeedKafka(10);
    await Task.Delay(1000);

    // cancel reading from trades
    cancellation.Cancel();
    try { await readingFromTrades; } catch (OperationCanceledException) { }

    // let's see what we have in trade_map
    await ReadFromTradeMap(client, CancellationToken.None);
}

// ----------------------------------------------------------------

async Task FeedKafka(int count)
{
    var producerConfig = new ProducerConfig
    {
        BootstrapServers = $"{kafkaServer}:{kafkaPort}"
    };

    Console.WriteLine("");
    Console.WriteLine($"Feed {count} items into Kafka");

    using var producer = new ProducerBuilder<long, string>(producerConfig).Build();

    for (var i = 0; i < count; i++)
    {
        var value = $"{{ \"id\": {kafkaIndex}, \"ticker\": \"GOOG\", \"price\": 1.12, \"qty\": 20 }}";
        await producer.ProduceAsync(kafkaTopicName, new Message<long, string> { Key = kafkaIndex, Value = value });
        kafkaIndex++;
    }

    Console.WriteLine("Done");
}

async Task InitializeKafka()
{
    Console.WriteLine("");
    Console.WriteLine("Initialize Kafka");

    var adminConfig = new AdminClientConfig { BootstrapServers = $"{kafkaServer}:{kafkaPort}" };

    using var adminClient = new AdminClientBuilder(adminConfig)
        .SetLogHandler((_, message) => Console.WriteLine($"KAFKA.LOG: {message.Message}"))
        .SetErrorHandler((_, error) => Console.WriteLine($"KAFKA.ERR: {error.Code} {error.Reason}"))
        .Build();

    // this is how we would delete the topic (assuming Kafka accepts it)
    //await adminClient.DeleteTopicsAsync(new[] { kafkaTopicName });

    var metadata = adminClient.GetMetadata(Timeout.InfiniteTimeSpan);
    var topicExists = metadata.Topics.Any(x => x.Topic == kafkaTopicName);

    if (topicExists)
    {
        Console.WriteLine($"Kafka topic '{kafkaTopicName}' exists");
    }
    else
    {
        Console.WriteLine($"Create Kafka topic '{kafkaTopicName}'");
        await adminClient.CreateTopicsAsync(new[]
        {
            new TopicSpecification { Name = kafkaTopicName, ReplicationFactor = 1, NumPartitions = 1 }
        });
    }

    Console.WriteLine("Done");
}

async Task PurgeKafka()
{
    Console.WriteLine("");
    Console.WriteLine("Purge Kafka topic");

    using var adminClient = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = $"{kafkaServer}:{kafkaPort}" }).Build();

    var entry = new ConfigEntry { Name = "retention.ms", Value = "100" };
    var configs = new Dictionary<ConfigResource, List<ConfigEntry>>
    {
        { new ConfigResource { Name = kafkaTopicName, Type = ResourceType.Topic }, new() { entry } }
    };

    await adminClient.AlterConfigsAsync(configs);

    await Task.Delay(4000); // give it time to purge

    entry.Value = "-1"; // back to infinite retention
    await adminClient.AlterConfigsAsync(configs);

    Console.WriteLine("Done");
}

// ----------------------------------------------------------------
