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
        var huaweiInverter = new HaDevice("meterbridge_huawei_inverter", "Huawei SUN2000", "Huawei", "SUN2000");
        var huaweiMeter = new HaDevice("meterbridge_huawei_meter", "Huawei Smartmeter", "Huawei");
        var huaweiBattery = new HaDevice("meterbridge_huawei_battery", "Huawei LUNA2000", "Huawei", "LUNA2000");
        var gas = new HaDevice("meterbridge_gas", "Gaszähler");
        var strom = new HaDevice("meterbridge_strom", "Stromzähler");

        string inverterTopic = _config["Huawei:Topic"] ?? "tele/huawei/pv";
        string meterTopic = _config["Huawei:MeterTopic"] ?? "tele/huawei/meter";
        string batteryTopic = _config["Huawei:BatteryTopic"] ?? "tele/huawei/battery";
        string gasTopic = _config["GasMeter:Topic"] ?? "tele/gaszaehler/pv";
        string gasSetTopic = _config["GasMeter:SetTopic"] ?? "cmnd/gaszaehler/zaehlerstand";
        string stromTopic = _config["Stromzaehler:Topic"] ?? "tele/stromzaehler/pv";
        int pvStringCount = int.Parse(_config["Huawei:PvStringCount"] ?? "2");

        var list = new List<Entity>
        {
            // --- Inverter ---
            new("input_power_kw", "PV Eingangsleistung", inverterTopic, "input_power_kw", huaweiInverter, "kW", "power", "measurement"),
            new("line_v_ab", "Netzspannung AB", inverterTopic, "line_v_ab", huaweiInverter, "V", "voltage", "measurement"),
            new("line_v_bc", "Netzspannung BC", inverterTopic, "line_v_bc", huaweiInverter, "V", "voltage", "measurement"),
            new("line_v_ca", "Netzspannung CA", inverterTopic, "line_v_ca", huaweiInverter, "V", "voltage", "measurement"),
            new("active_power_kw", "Wirkleistung AC", inverterTopic, "active_power_kw", huaweiInverter, "kW", "power", "measurement"),
            new("power_factor", "Leistungsfaktor", inverterTopic, "power_factor", huaweiInverter, null, "power_factor", "measurement"),
            new("grid_frequency", "Netzfrequenz", inverterTopic, "grid_frequency", huaweiInverter, "Hz", "frequency", "measurement"),
            new("internal_temperature_c", "Interne Temperatur", inverterTopic, "internal_temperature_c", huaweiInverter, "°C", "temperature", "measurement"),
            new("fault_code", "Fehlercode", inverterTopic, "fault_code", huaweiInverter),
            new("total_yield_kwh", "Gesamtertrag", inverterTopic, "total_yield_kwh", huaweiInverter, "kWh", "energy", "total_increasing"),
            new("daily_yield_kwh", "Tagesertrag", inverterTopic, "daily_yield_kwh", huaweiInverter, "kWh", "energy", "total_increasing"),
            new("device_status", "Gerätestatus", inverterTopic, "device_status", huaweiInverter, IsText: true),

            // --- Meter ---
            new("meter_voltage_a", "Netzspannung A", meterTopic, "voltage_a", huaweiMeter, "V", "voltage", "measurement"),
            new("meter_voltage_b", "Netzspannung B", meterTopic, "voltage_b", huaweiMeter, "V", "voltage", "measurement"),
            new("meter_voltage_c", "Netzspannung C", meterTopic, "voltage_c", huaweiMeter, "V", "voltage", "measurement"),
            new("meter_current_a", "Netzstrom A", meterTopic, "current_a", huaweiMeter, "A", "current", "measurement"),
            new("meter_current_b", "Netzstrom B", meterTopic, "current_b", huaweiMeter, "A", "current", "measurement"),
            new("meter_current_c", "Netzstrom C", meterTopic, "current_c", huaweiMeter, "A", "current", "measurement"),
            new("meter_active_power_w", "Netzleistung (Meter)", meterTopic, "active_power_w", huaweiMeter, "W", "power", "measurement"),
            new("meter_power_factor", "Leistungsfaktor (Meter)", meterTopic, "power_factor", huaweiMeter, null, "power_factor", "measurement"),
            new("meter_grid_frequency", "Netzfrequenz (Meter)", meterTopic, "grid_frequency", huaweiMeter, "Hz", "frequency", "measurement"),
            new("meter_exported_kwh", "Einspeisung gesamt (Meter)", meterTopic, "exported_kwh", huaweiMeter, "kWh", "energy", "total_increasing"),
            new("meter_imported_kwh", "Bezug gesamt (Meter)", meterTopic, "imported_kwh", huaweiMeter, "kWh", "energy", "total_increasing"),
            new("meter_status", "Meter-Status (Rohcode)", meterTopic, "status", huaweiMeter, EntityCategory: "diagnostic"),

            // --- Battery ---
            new("battery_charge_discharge_kw", "Lade-/Entladeleistung", batteryTopic, "charge_discharge_power_kw", huaweiBattery, "kW", "power", "measurement"),
            new("battery_bus_voltage", "Busspannung", batteryTopic, "bus_voltage", huaweiBattery, "V", "voltage", "measurement"),
            new("battery_soc", "Ladezustand (SOC)", batteryTopic, "soc_percent", huaweiBattery, "%", "battery", "measurement"),
            new("battery_daily_charge_kwh", "Tagesladung", batteryTopic, "daily_charge_kwh", huaweiBattery, "kWh", "energy", "total_increasing"),
            new("battery_daily_discharge_kwh", "Tagesentladung", batteryTopic, "daily_discharge_kwh", huaweiBattery, "kWh", "energy", "total_increasing"),
            new("battery_bus_current", "Busstrom", batteryTopic, "bus_current", huaweiBattery, "A", "current", "measurement"),
            new("battery_temperature_c", "Batterietemperatur", batteryTopic, "battery_temperature_c", huaweiBattery, "°C", "temperature", "measurement"),
            new("battery_total_charge_kwh", "Gesamtladung", batteryTopic, "total_charge_kwh", huaweiBattery, "kWh", "energy", "total_increasing"),
            new("battery_total_discharge_kwh", "Gesamtentladung", batteryTopic, "total_discharge_kwh", huaweiBattery, "kWh", "energy", "total_increasing"),
            new("battery_running_status", "Batteriestatus", batteryTopic, "running_status_text", huaweiBattery, IsText: true),

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

        // PV-Strings dynamisch je nach konfigurierter Anzahl
        for (int n = 1; n <= pvStringCount; n++)
        {
            list.Add(new($"pv{n}_voltage", $"PV String {n} Spannung", inverterTopic, $"pv{n}_voltage", huaweiInverter, "V", "voltage", "measurement"));
            list.Add(new($"pv{n}_current", $"PV String {n} Strom", inverterTopic, $"pv{n}_current", huaweiInverter, "A", "current", "measurement"));
        }

        // --- Diagnose: Zeitstempel letztes erfolgreiches/fehlerhaftes Auslesen ---
        // Eigene Topics ({topic}/last_success, {topic}/last_error) mit reinem
        // Zeitstempel-String als Payload - kein value_template nötig.
        foreach (var (prefix, topic, device) in new[]
        {
            ("huawei_inverter", inverterTopic, huaweiInverter),
            ("huawei_meter", meterTopic, huaweiMeter),
            ("huawei_battery", batteryTopic, huaweiBattery),
            ("gas", gasTopic, gas),
            ("strom", stromTopic, strom),
        })
        {
            list.Add(new($"{prefix}_last_success", "Letztes erfolgreiches Auslesen", $"{topic}/last_success", "", device,
                DeviceClass: "timestamp", EntityCategory: "diagnostic", HasValueTemplate: false));

            // Gaszähler hat aktuell keinen definierten Fehlerfall (reine GPIO-
            // Pulszählung, kein Verbindungsaufbau der fehlschlagen könnte) -
            // last_error entsprechend nur für die anderen vier anlegen.
            if (prefix != "gas")
            {
                list.Add(new($"{prefix}_last_error", "Letzter Fehler beim Auslesen", $"{topic}/last_error", "", device,
                    DeviceClass: "timestamp", EntityCategory: "diagnostic", HasValueTemplate: false));
            }
        }

        return list;
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
