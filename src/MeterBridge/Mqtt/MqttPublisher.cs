using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;

namespace MeterBridge.Mqtt;

/// <summary>
/// Dünner Wrapper um den MQTTnet-Client. Wird als Singleton registriert und von
/// allen BackgroundServices gemeinsam genutzt, damit nicht jeder Service seine
/// eigene Broker-Verbindung aufmacht. Unterstützt sowohl Publish als auch
/// Subscribe (für "von außen setzbare" Werte wie den Gaszähler-Stand).
/// </summary>
public sealed class MqttPublisher : IAsyncDisposable
{
    private const string AvailabilityOnline = "online";
    private const string AvailabilityOffline = "offline";

    private readonly IMqttClient _client;
    private readonly MqttClientOptions _options;
    private readonly ILogger<MqttPublisher> _logger;
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly Dictionary<string, Func<string, Task>> _subscriptions = new();
    private bool _receivedHandlerRegistered;

    /// <summary>
    /// Bridge-weites Online/Offline-Topic, das von HomeAssistantDiscoveryService als
    /// availability_topic in jede Entity eingetragen wird. Wird als Last-Will beim
    /// Verbindungsaufbau hinterlegt (greift bei Absturz/Verbindungsverlust) und bei
    /// jedem (Re-)Connect sowie beim geordneten Beenden aktiv gesetzt.
    /// </summary>
    public string AvailabilityTopic { get; }

    public MqttPublisher(IConfiguration config, ILogger<MqttPublisher> logger)
    {
        _logger = logger;

        var host = config["Mqtt:Host"] ?? throw new InvalidOperationException("Mqtt:Host fehlt in appsettings.json");
        var port = int.Parse(config["Mqtt:Port"] ?? "1883");
        var clientId = config["Mqtt:ClientId"] ?? "meter-bridge-pi";
        var username = config["Mqtt:Username"];
        var password = config["Mqtt:Password"];
        AvailabilityTopic = config["Mqtt:AvailabilityTopic"] ?? "meterbridge/bridge/status";

        var factory = new MqttFactory();
        _client = factory.CreateMqttClient();

        var optionsBuilder = new MqttClientOptionsBuilder()
            .WithTcpServer(host, port)
            .WithClientId(clientId)
            .WithCleanSession()
            .WithWillTopic(AvailabilityTopic)
            .WithWillPayload(AvailabilityOffline)
            .WithWillRetain(true)
            .WithWillQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce);

        if (!string.IsNullOrEmpty(username))
        {
            optionsBuilder = optionsBuilder.WithCredentials(username, password);
        }

        _options = optionsBuilder.Build();
    }

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_client.IsConnected)
        {
            return;
        }

        await _connectLock.WaitAsync(cancellationToken);
        try
        {
            if (_client.IsConnected)
            {
                return;
            }

            _logger.LogInformation("Verbinde zum MQTT-Broker...");
            await _client.ConnectAsync(_options, cancellationToken);

            // Birth-Message: aktiv "online" setzen (retained), das Last-Will allein
            // greift nur beim ungeordneten Verbindungsabbruch, nicht direkt nach dem
            // Connect - ohne dies bliebe der Status auf einem frischen Broker leer.
            var birthMessage = new MqttApplicationMessageBuilder()
                .WithTopic(AvailabilityTopic)
                .WithPayload(AvailabilityOnline)
                .WithRetainFlag(true)
                .Build();
            await _client.PublishAsync(birthMessage, cancellationToken);

            // Nach (Re-)Connect alle bisherigen Subscriptions erneuern, da diese
            // beim Neuverbinden sonst verloren gehen (z.B. nach Broker-Neustart).
            foreach (var topic in _subscriptions.Keys)
            {
                await SubscribeOnBrokerAsync(topic, cancellationToken);
            }
        }
        finally
        {
            _connectLock.Release();
        }
    }

    public async Task PublishJsonAsync(string topic, string json, CancellationToken cancellationToken, bool retain = false)
    {
        try
        {
            await EnsureConnectedAsync(cancellationToken);

            var message = new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(json)
                .WithRetainFlag(retain)
                .Build();

            await _client.PublishAsync(message, cancellationToken);
        }
        catch (Exception ex)
        {
            // Bewusst nur loggen statt werfen: ein einzelner fehlgeschlagener Publish
            // (z.B. Broker kurz nicht erreichbar) soll den jeweiligen Service nicht abschießen.
            _logger.LogWarning(ex, "MQTT-Publish auf {Topic} fehlgeschlagen", topic);
        }
    }

    /// <summary>
    /// Registriert einen Handler für ein Topic (z.B. ein "cmnd/..."-Topic, über
    /// das von außen - etwa aus IP-Symcon - ein Wert gesetzt werden kann). Der
    /// Handler bekommt den rohen Payload-String übergeben.
    /// </summary>
    public async Task SubscribeAsync(string topic, Func<string, Task> handler, CancellationToken cancellationToken)
    {
        _subscriptions[topic] = handler;

        if (!_receivedHandlerRegistered)
        {
            _client.ApplicationMessageReceivedAsync += async e =>
            {
                if (_subscriptions.TryGetValue(e.ApplicationMessage.Topic, out var h))
                {
                    string payload = e.ApplicationMessage.ConvertPayloadToString();
                    try
                    {
                        await h(payload);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Fehler beim Verarbeiten einer MQTT-Nachricht auf {Topic}", e.ApplicationMessage.Topic);
                    }
                }
            };
            _receivedHandlerRegistered = true;
        }

        await EnsureConnectedAsync(cancellationToken);
        await SubscribeOnBrokerAsync(topic, cancellationToken);
    }

    private async Task SubscribeOnBrokerAsync(string topic, CancellationToken cancellationToken)
    {
        var subscribeOptions = new MqttFactory().CreateSubscribeOptionsBuilder()
            .WithTopicFilter(f => f.WithTopic(topic))
            .Build();
        await _client.SubscribeAsync(subscribeOptions, cancellationToken);
        _logger.LogInformation("MQTT-Subscription eingerichtet für {Topic}", topic);
    }

    public async ValueTask DisposeAsync()
    {
        if (_client.IsConnected)
        {
            // Bei geordnetem Beenden greift das Last-Will nicht (das feuert nur bei
            // ungeordnetem Verbindungsabbruch) - "offline" daher hier aktiv setzen,
            // damit HA den Bridge-Status auch nach einem sauberen Stop korrekt zeigt.
            try
            {
                var offlineMessage = new MqttApplicationMessageBuilder()
                    .WithTopic(AvailabilityTopic)
                    .WithPayload(AvailabilityOffline)
                    .WithRetainFlag(true)
                    .Build();
                await _client.PublishAsync(offlineMessage);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Konnte Offline-Status beim Beenden nicht mehr senden");
            }

            await _client.DisconnectAsync();
        }
        _client.Dispose();
    }
}
