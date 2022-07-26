using Confluent.Kafka.Admin;
using Confluent.Kafka;
using Microsoft.Extensions.Logging;

namespace DotNetStockDemo;

internal class KafkaFeed
{
    private readonly ILogger _logger;

    private readonly string _kafkaServer;
    private readonly int _kafkaPort;
    private readonly string _kafkaTopicName;
    private int _idSequence;

    public KafkaFeed(string kafkaServer, int kafkaPort, string kafkaTopicName, ILogger<KafkaFeed> logger)
    {
        _kafkaServer = kafkaServer;
        _kafkaPort = kafkaPort;
        _kafkaTopicName = kafkaTopicName;
        _logger = logger;
    }

    public async Task SendToKafka(string ticker, float price, int qty)
    {
        var producerConfig = new ProducerConfig
        {
            BootstrapServers = $"{_kafkaServer}:{_kafkaPort}"
        };

        _logger.LogInformation($"Send to Kafka: {ticker} {qty} @ {price:F3}");

        // TODO do not recreate this all the time
        using var producer = new ProducerBuilder<long, string>(producerConfig).Build();

        var id = Interlocked.Increment(ref _idSequence);
        var value = $"{{ \"id\": {id}, \"ticker\": \"{ticker}\", \"price\": {price}, \"qty\": {qty} }}";
        await producer.ProduceAsync(_kafkaTopicName, new Message<long, string> { Key = id, Value = value });
    }

    public async Task InitializeKafka()
    {
        _logger.LogInformation("Initialize Kafka");

        using var adminClient = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = $"{_kafkaServer}:{_kafkaPort}" }).Build();

        // this is how we would delete the topic (assuming Kafka accepts it)
        //await adminClient.DeleteTopicsAsync(new[] { kafkaTopicName });

        var metadata = adminClient.GetMetadata(Timeout.InfiniteTimeSpan);
        var topicExists = metadata.Topics.Any(x => x.Topic == _kafkaTopicName);

        if (topicExists)
        {
            _logger.LogInformation($"Kafka topic '{_kafkaTopicName}' exists");
        }
        else
        {
            _logger.LogInformation($"Create Kafka topic '{_kafkaTopicName}'");
            await adminClient.CreateTopicsAsync(new[]
            {
            new TopicSpecification { Name = _kafkaTopicName, ReplicationFactor = 1, NumPartitions = 1 }
        });
        }

        _logger.LogInformation("Kafka has been initialized");
    }

    public async Task PurgeKafka()
    {
        _logger.LogInformation($"Purge Kafka topic '{_kafkaTopicName}'");

        using var adminClient = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = $"{_kafkaServer}:{_kafkaPort}" }).Build();

        var entry = new ConfigEntry { Name = "retention.ms", Value = "100" };
        var configs = new Dictionary<ConfigResource, List<ConfigEntry>>
        {
            { new ConfigResource { Name = _kafkaTopicName, Type = ResourceType.Topic }, new() { entry } }
        };

        await adminClient.AlterConfigsAsync(configs);

        await Task.Delay(4000); // give it time to purge

        entry.Value = "-1"; // back to infinite retention
        await adminClient.AlterConfigsAsync(configs);

        _logger.LogInformation($"Kafka topic '{_kafkaTopicName}' has been purged");
    }
}