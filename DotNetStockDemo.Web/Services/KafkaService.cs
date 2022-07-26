using System.Globalization;
using Confluent.Kafka.Admin;
using Confluent.Kafka;

namespace DotNetStockDemo.Web.Services;

internal class KafkaService : IDisposable
{
    private readonly ILogger _logger;
    private readonly DemoOptions _options;
    private IProducer<long, string>? _producer;
    private int _idSequence;

    public KafkaService(DemoOptions options, ILoggerFactory loggerFactory)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = loggerFactory?.CreateLogger<KafkaService>() ?? throw new ArgumentNullException(nameof(loggerFactory));

        // note: do *not* create the producer here as it immediately connects
        // we do *not* want to connect in the ctor - we'll do it when initializing
    }

    public void Dispose()
    {
        _producer?.Dispose();
    }

    public async Task SendTrade(string ticker, float price, int qty)
    {
        if (_producer == null) throw new InvalidOperationException("No producer.");

        _logger.LogInformation($"Send to Kafka: {ticker} {qty} @ {price:F3}");

        var id = Interlocked.Increment(ref _idSequence);
        var value = $"{{ \"id\": {id}, \"ticker\": \"{ticker}\", \"price\": {price.ToString(CultureInfo.InvariantCulture)}, \"qty\": {qty} }}";
        await _producer.ProduceAsync(_options.KafkaTopicName, new Message<long, string> { Key = id, Value = value });
    }

    public async Task Initialize()
    {
        _logger.LogInformation($"Initialize Kafka at {_options.KafkaServer}:{_options.KafkaPort}");

        var adminConfig = new AdminClientConfig { BootstrapServers = $"{_options.KafkaServer}:{_options.KafkaPort}" };

        using var adminClient = new AdminClientBuilder(adminConfig)
            .SetLogHandler((_, message) => _logger.LogInformation($"Kafka: {message.Message}"))
            .SetErrorHandler((_, error) => _logger.LogWarning($"Kafka: {error.Code} {error.Reason}"))
            .Build();

        // this is how we would delete the topic (assuming Kafka accepts it)
        //await adminClient.DeleteTopicsAsync(new[] { kafkaTopicName });

        var metadata = adminClient.GetMetadata(TimeSpan.FromSeconds(10));
        var topicExists = metadata.Topics.Any(x => x.Topic == _options.KafkaTopicName);

        if (topicExists)
        {
            _logger.LogInformation($"Kafka topic '{_options.KafkaTopicName}' exists");
        }
        else
        {
            _logger.LogInformation($"Create Kafka topic '{_options.KafkaTopicName}'");
            await adminClient.CreateTopicsAsync(new[]
            {
                new TopicSpecification { Name = _options.KafkaTopicName, ReplicationFactor = 1, NumPartitions = 1 }
            });
        }

        _logger.LogInformation($"Create Kafka producer");
        var producerConfig = new ProducerConfig { BootstrapServers = $"{_options.KafkaServer}:{_options.KafkaPort}" };
        _producer = new ProducerBuilder<long, string>(producerConfig)
            .SetLogHandler((_, message) => _logger.LogInformation($"Kafka: {message.Message}"))
            .SetErrorHandler((_, error) => _logger.LogWarning($"Kafka: {error.Code} {error.Reason}"))
            .Build();

        _logger.LogInformation("Kafka has been initialized");
    }

    public async Task PurgeTopic()
    {
        _logger.LogInformation($"Purge Kafka topic '{_options.KafkaTopicName}'");

        var adminConfig = new AdminClientConfig { BootstrapServers = $"{_options.KafkaServer}:{_options.KafkaPort}" };

        using var adminClient = new AdminClientBuilder(adminConfig)
            .SetLogHandler((_, message) => _logger.LogInformation($"Kafka: {message.Message}"))
            .SetErrorHandler((_, error) => _logger.LogWarning($"Kafka: {error.Code} {error.Reason}"))
            .Build();

        var entry = new ConfigEntry { Name = "retention.ms", Value = "100" };
        var configs = new Dictionary<ConfigResource, List<ConfigEntry>>
        {
            { new ConfigResource { Name = _options.KafkaTopicName, Type = ResourceType.Topic }, new() { entry } }
        };

        await adminClient.AlterConfigsAsync(configs);

        await Task.Delay(4000); // give it time to purge

        entry.Value = "-1"; // back to infinite retention
        await adminClient.AlterConfigsAsync(configs);

        _logger.LogInformation($"Kafka topic '{_options.KafkaTopicName}' has been purged");
    }
}