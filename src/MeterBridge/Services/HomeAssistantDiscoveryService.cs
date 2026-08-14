using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MeterBridge.Mqtt;

namespace MeterBridge.Services;

/// <summary>
/// Published einmalig beim Start die Home Assistant MQTT-Discovery-Configs für
/// alle Sensoren, die die anderen drei Services erzeugen. Läuft als einfacher
/// IHostedService (kein Dauerlauf nötig), Discovery-Nachrichten werden retained
/// gesendet, damit Home Assistant sie auch nach einem eigenen Neustart sofort
/// wiederfindet, ohne dass MeterBridge sie erneut senden muss.
///
/// Topic-Schema: homeassistant/sensor/{device_id}/{object_id}/config
/// Doku: https://www.home-assistant.io/integrations/mqtt/#discovery-messages
/// </summary>
public sealed class HomeAssistantDiscoveryService : IHostedService
{
    private sealed record HaDevice(string Id, string Name, string? Manufacturer = null, string? Model = null);

    private sealed record Entity(
        string ObjectId,
        string Name,
        string StateTopic,
        string ValueKey,
        HaDevice Device,
        string? Unit = null,
        string? DeviceClass = null,
        string? StateClass = null,
        bool IsText = false,
        string Component = "sensor",
        string? CommandTopic = null,
        double? Min = null,
        double? Max = null,
        double? Step = null,
        string? EntityCategory = null,
        bool HasValueTemplate = true);

    // Bucket-Key (Huawei:PollGroups[].Device) -> HA-Gerätegruppierung. Presentation-
    // Metadaten (Hersteller/Modell), bewusst hier belassen statt in der Config, da
    // Huawei:PollGroups/DataPoints die Polling-Logik beschreiben, nicht die HA-UI.
    private static readonly Dictionary<string, HaDevice> HuaweiDevices = new()
    {
        ["inverter"] = new("meterbridge_huawei_inverter", "Huawei SUN2000", "Huawei", "SUN2000"),
        ["meter"] = new("meterbridge_huawei_meter", "Huawei Smartmeter", "Huawei"),
        ["battery"] = new("meterbridge_huawei_battery", "Huawei LUNA2000", "Huawei", "LUNA2000"),
    };

    private static HaDevice ResolveHuaweiDevice(string bucket) =>
        HuaweiDevices.TryGetValue(bucket, out var device) ? device : new HaDevice($"meterbridge_huawei_{bucket}", bucket);

    private readonly MqttPublisher _mqtt;
    private readonly IConfiguration _config;
    private readonly ILogger<HomeAssistantDiscoveryService> _logger;

    public HomeAssistantDiscoveryService(MqttPublisher mqtt, IConfiguration config, ILogger<HomeAssistantDiscoveryService> logger)
    {
        _mqtt = mqtt;
        _config = config;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Kurze Wartezeit, damit der MQTT-Client der anderen Services (bzw. dieser
        // hier, da MqttPublisher als Singleton geteilt wird) sicher verbunden ist,
        // bevor wir die retained Discovery-Nachrichten senden.
        await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);

        var entities = BuildEntities();
        _logger.LogInformation("Sende {Count} Home-Assistant-Discovery-Configs", entities.Count);

        foreach (var e in entities)
        {
            var payload = BuildDiscoveryPayload(e);
            string topic = $"homeassistant/{e.Component}/{e.Device.Id}/{e.ObjectId}/config";
            await _mqtt.PublishJsonAsync(topic, JsonSerializer.Serialize(payload), cancellationToken, retain: true);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private List<Entity> BuildEntities()
    {
        var gas = new HaDevice("meterbridge_gas", "Gaszähler");
        var strom = new HaDevice("meterbridge_strom", "Stromzähler");

        string gasTopic = _config["GasMeter:Topic"] ?? "tele/gaszaehler/pv";
        string gasSetTopic = _config["GasMeter:SetTopic"] ?? "cmnd/gaszaehler/zaehlerstand";
        string stromTopic = _config["Stromzaehler:Topic"] ?? "tele/stromzaehler/pv";

        var huaweiPollGroups = _config.GetSection("Huawei:PollGroups").Get<List<HuaweiPollGroupConfig>>() ?? [];
        var huaweiDataPoints = _config.GetSection("Huawei:DataPoints").Get<List<HuaweiDataPointConfig>>() ?? [];
        var huaweiGroupsByName = huaweiPollGroups.ToDictionary(g => g.Name);

        var list = new List<Entity>
        {
            // --- Gaszähler ---
            new("gas_verbrauch_m3", "Gasverbrauch gesamt", gasTopic, "verbrauch_m3", gas, "m³", "gas", "total_increasing"),
            new("gas_pulse_count", "Gaszähler Impulse", gasTopic, "pulse_count", gas),
            new("gas_zaehlerstand_set", "Gaszähler Stand setzen", gasTopic, "verbrauch_m3", gas, "m³",
                Component: "number", CommandTopic: gasSetTopic, Min: 0, Max: 999999, Step: 0.01),

            // --- Stromzähler ---
            new("strom_bezug_kwh", "Bezug gesamt", stromTopic, "Bezug", strom, "kWh", "energy", "total_increasing"),
            new("strom_einspeisung_kwh", "Einspeisung gesamt", stromTopic, "Einspeisung", strom, "kWh", "energy", "total_increasing"),
            new("strom_verbrauch_w", "Momentanleistung", stromTopic, "Verbrauch", strom, "W", "power", "measurement"),
        };

        // --- Huawei: dynamisch aus Huawei:DataPoints/PollGroups generiert ---
        // Jeder Datenpunkt wird 1:1 zu einer Entity; Topic/HA-Device kommen von
        // der referenzierten Abrufgruppe. Neue/geänderte Datenpunkte in
        // appsettings.json erscheinen so automatisch in Home Assistant, ohne
        // dass diese Datei angefasst werden muss.
        foreach (var dp in huaweiDataPoints)
        {
            if (!huaweiGroupsByName.TryGetValue(dp.Group, out var group))
            {
                _logger.LogWarning("Huawei-Datenpunkt {Name} referenziert unbekannte Abrufgruppe {Group}, wird übersprungen", dp.Name, dp.Group);
                continue;
            }

            list.Add(new(
                dp.HaObjectId ?? dp.Name,
                dp.Label ?? dp.Name,
                group.Topic,
                dp.Name,
                ResolveHuaweiDevice(group.Device),
                dp.Unit,
                dp.DeviceClass,
                dp.StateClass,
                EntityCategory: dp.EntityCategory));
        }

        // Abgeleitete Text-Entities ohne eigenes Register: nur emittiert, wenn der
        // zugrunde liegende Rohcode-Datenpunkt in der Config existiert. Object-Ids/
        // Namen hier hartcodiert, um die Bestands-unique_ids zu erhalten - analog zu
        // den ebenfalls hartcodiert belassenen Status-Code-Dictionaries in
        // HuaweiModbusService.
        var deviceStatusSource = huaweiDataPoints.FirstOrDefault(d => d.Name == "device_status_code");
        if (deviceStatusSource is not null && huaweiGroupsByName.TryGetValue(deviceStatusSource.Group, out var inverterGroup))
        {
            list.Add(new("device_status", "Gerätestatus", inverterGroup.Topic, "device_status",
                ResolveHuaweiDevice(inverterGroup.Device), IsText: true));
        }

        var runningStatusSource = huaweiDataPoints.FirstOrDefault(d => d.Name == "running_status");
        if (runningStatusSource is not null && huaweiGroupsByName.TryGetValue(runningStatusSource.Group, out var batteryGroup))
        {
            list.Add(new("battery_running_status", "Batteriestatus", batteryGroup.Topic, "running_status_text",
                ResolveHuaweiDevice(batteryGroup.Device), IsText: true));
        }

        // --- Diagnose: Zeitstempel letztes erfolgreiches/fehlerhaftes Auslesen ---
        // Eigene Topics ({topic}/last_success, {topic}/last_error) mit reinem
        // Zeitstempel-String als Payload - kein value_template nötig.
        foreach (var group in huaweiPollGroups)
        {
            string prefix = $"huawei_{group.Name.ToLowerInvariant()}";
            var device = ResolveHuaweiDevice(group.Device);

            list.Add(new($"{prefix}_last_success", "Letztes erfolgreiches Auslesen", $"{group.Topic}/last_success", "", device,
                DeviceClass: "timestamp", EntityCategory: "diagnostic", HasValueTemplate: false));
            list.Add(new($"{prefix}_last_error", "Letzter Fehler beim Auslesen", $"{group.Topic}/last_error", "", device,
                DeviceClass: "timestamp", EntityCategory: "diagnostic", HasValueTemplate: false));
        }

        foreach (var (prefix, topic, device) in new[]
        {
            ("gas", gasTopic, gas),
            ("strom", stromTopic, strom),
        })
        {
            list.Add(new($"{prefix}_last_success", "Letztes erfolgreiches Auslesen", $"{topic}/last_success", "", device,
                DeviceClass: "timestamp", EntityCategory: "diagnostic", HasValueTemplate: false));

            // Gaszähler hat aktuell keinen definierten Fehlerfall (reine GPIO-
            // Pulszählung, kein Verbindungsaufbau der fehlschlagen könnte) -
            // last_error entsprechend nur für Stromzähler anlegen.
            if (prefix != "gas")
            {
                list.Add(new($"{prefix}_last_error", "Letzter Fehler beim Auslesen", $"{topic}/last_error", "", device,
                    DeviceClass: "timestamp", EntityCategory: "diagnostic", HasValueTemplate: false));
            }
        }

        bool huaweiEnabled = _config.GetValue("Huawei:Enabled", true);
        bool gasEnabled = _config.GetValue("GasMeter:Enabled", true);
        bool stromEnabled = _config.GetValue("Stromzaehler:Enabled", true);

        return list.Where(e => e.Device.Id switch
        {
            _ when e.Device.Id.StartsWith("meterbridge_huawei_") => huaweiEnabled,
            "meterbridge_gas" => gasEnabled,
            "meterbridge_strom" => stromEnabled,
            _ => true,
        }).ToList();
    }

    private static Dictionary<string, object> BuildDiscoveryPayload(Entity e)
    {
        var payload = new Dictionary<string, object>
        {
            ["name"] = e.Name,
            ["unique_id"] = $"{e.Device.Id}_{e.ObjectId}",
            ["state_topic"] = e.StateTopic,
            ["device"] = BuildDeviceBlock(e.Device),
        };

        if (e.HasValueTemplate)
        {
            payload["value_template"] = $"{{{{ value_json.get('{e.ValueKey}') }}}}";
        }
        // sonst: state_topic liefert den Wert direkt als Rohstring (z.B. bei den
        // last_success/last_error-Zeitstempel-Topics), kein Template nötig.

        if (!e.IsText)
        {
            if (e.Unit is not null) payload["unit_of_measurement"] = e.Unit;
            if (e.DeviceClass is not null) payload["device_class"] = e.DeviceClass;
            if (e.StateClass is not null) payload["state_class"] = e.StateClass;
        }

        if (e.Component == "number")
        {
            // Bei number-Entities erwartet Home Assistant command_topic + min/max/step,
            // state_class/device_class sind hier nicht relevant (das ist kein reiner Sensor).
            if (e.CommandTopic is not null) payload["command_topic"] = e.CommandTopic;
            if (e.Min is not null) payload["min"] = e.Min;
            if (e.Max is not null) payload["max"] = e.Max;
            if (e.Step is not null) payload["step"] = e.Step;
            payload["mode"] = "box";
        }

        if (e.EntityCategory is not null)
        {
            payload["entity_category"] = e.EntityCategory;
        }

        return payload;
    }

    private static Dictionary<string, object> BuildDeviceBlock(HaDevice d)
    {
        var block = new Dictionary<string, object>
        {
            ["identifiers"] = new[] { d.Id },
            ["name"] = d.Name,
        };
        if (d.Manufacturer is not null) block["manufacturer"] = d.Manufacturer;
        if (d.Model is not null) block["model"] = d.Model;
        return block;
    }
}
