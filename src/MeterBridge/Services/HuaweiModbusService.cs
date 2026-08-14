using System.Net.Sockets;
using System.Text.Json;
using MeterBridge.Mqtt;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NModbus;

namespace MeterBridge.Services;

/// <summary>
/// Liest zyklisch drei Registerblöcke vom Huawei SDongleA per Modbus TCP:
///   1. Inverter-Block (32016-32115): PV-Strings, AC-Leistung, Temperatur, Status, Erträge
///   2. Meter-Block (37100-37122): Netzanschluss-/Smartmeter-Werte (Spannung, Strom, Bezug/Einspeisung)
///   3. Batterie-Block (37000-37069): LUNA2000-Energiespeicher, falls vorhanden
///
/// Jeder Block wird auf ein eigenes MQTT-Topic published. Ein einzelner
/// fehlschlagender Block (z.B. keine Batterie installiert) blockiert die
/// anderen beiden nicht.
///
/// Registeradressen stammen aus der offiziellen Huawei "Solar Inverter Modbus
/// Interface Definitions" sowie Community-Dokumentation. Vor dem produktiven
/// Einsatz gegen dein konkretes Wechselrichter-Modell/Firmware verifizieren -
/// insbesondere die Anzahl der PV-Strings (PvStringCount) auf deine Anlage
/// anpassen.
/// </summary>
public sealed class HuaweiModbusService : BackgroundService
{
    private readonly record struct RegisterDef(string Name, int WordOffset, int Words, double Gain, bool Signed);

    #region Registerdefinitionen

    // --- Inverter-Block, Start bei 32016 (erstes PV-String-Register) ---
    private const int InverterBlockStart = 32016;
    private const int InverterBlockCount = 100; // deckt 32016..32115 ab (bis Daily Yield)

    // Offsets relativ zu InverterBlockStart. PV-String-Register (2 pro String)
    // belegen die ersten 2*PvStringCount Offsets automatisch, siehe ReadInverterBlockAsync.
    private static readonly RegisterDef[] InverterFixedRegisters =
    {
        new("input_power_kw",        32064 - InverterBlockStart, 2, 1000.0, true),
        new("line_v_ab",              32066 - InverterBlockStart, 1, 10.0,   false),
        new("line_v_bc",               32067 - InverterBlockStart, 1, 10.0,   false),
        new("line_v_ca",               32068 - InverterBlockStart, 1, 10.0,   false),
        new("active_power_kw",        32080 - InverterBlockStart, 2, 1000.0, true),
        new("power_factor",            32084 - InverterBlockStart, 1, 1000.0, true),
        new("grid_frequency",          32085 - InverterBlockStart, 1, 100.0,  false),
        new("internal_temperature_c",  32087 - InverterBlockStart, 1, 10.0,   true),
        new("fault_code",              32090 - InverterBlockStart, 1, 1.0,    false),
        new("total_yield_kwh",         32106 - InverterBlockStart, 2, 100.0,  false),
        new("daily_yield_kwh",         32114 - InverterBlockStart, 2, 100.0,  false),
    };
    private const int DeviceStatusWordOffset = 32089 - InverterBlockStart;

    // --- Meter-Block (Netzanschluss/Smartmeter), Start bei 37100 ---
    private const int MeterBlockStart = 37100;
    private const int MeterBlockCount = 23; // deckt 37100..37122 ab

    private static readonly RegisterDef[] MeterRegisters =
    {
        new("status",                37100 - MeterBlockStart, 1, 1.0,   false),
        new("voltage_a",             37101 - MeterBlockStart, 2, 10.0,  true),
        new("voltage_b",             37103 - MeterBlockStart, 2, 10.0,  true),
        new("voltage_c",             37105 - MeterBlockStart, 2, 10.0,  true),
        new("current_a",             37107 - MeterBlockStart, 2, 100.0, true),
        new("current_b",             37109 - MeterBlockStart, 2, 100.0, true),
        new("current_c",             37111 - MeterBlockStart, 2, 100.0, true),
        new("active_power_w",        37113 - MeterBlockStart, 2, 1.0,   true), // >0 Einspeisung, <0 Bezug
        new("power_factor",          37117 - MeterBlockStart, 1, 1000.0, true),
        new("grid_frequency",        37118 - MeterBlockStart, 1, 100.0, true),
        new("exported_kwh",          37119 - MeterBlockStart, 2, 100.0, true),
        new("imported_kwh",          37121 - MeterBlockStart, 2, 100.0, true),
    };

    // --- Batterie-Block ([Energy storage unit 1]), Start bei 37000 ---
    private const int BatteryBlockStart = 37000;
    private const int BatteryBlockCount = 70; // deckt 37000..37069 ab

    private static readonly RegisterDef[] BatteryRegisters =
    {
        new("running_status",              37000 - BatteryBlockStart, 1, 1.0,   false),
        new("charge_discharge_power_kw",   37001 - BatteryBlockStart, 2, 1000.0, true), // >0 laden, <0 entladen
        new("bus_voltage",                 37003 - BatteryBlockStart, 1, 10.0,  false),
        new("soc_percent",                 37004 - BatteryBlockStart, 1, 10.0,  false),
        new("working_mode",                37006 - BatteryBlockStart, 1, 1.0,   false),
        new("fault_id",                    37014 - BatteryBlockStart, 1, 1.0,   false),
        new("daily_charge_kwh",            37015 - BatteryBlockStart, 2, 100.0, false),
        new("daily_discharge_kwh",         37017 - BatteryBlockStart, 2, 100.0, false),
        new("bus_current",                 37021 - BatteryBlockStart, 1, 10.0,  true),
        new("battery_temperature_c",       37022 - BatteryBlockStart, 1, 10.0,  true),
        new("total_charge_kwh",            37066 - BatteryBlockStart, 2, 100.0, false),
        new("total_discharge_kwh",         37068 - BatteryBlockStart, 2, 100.0, false),
    };

    // Bekannte Device-Status-Codes (Register 32089), siehe Huawei Modbus Interface Definitions
    private static readonly Dictionary<int, string> DeviceStatusCodes = new()
    {
        [0] = "Standby: initializing",
        [1] = "Standby: detecting insulation resistance",
        [2] = "Standby: detecting irradiation",
        [3] = "Standby: grid detecting",
        [256] = "Starting",
        [512] = "On-grid: running",
        [513] = "Grid connection: power limited",
        [514] = "Grid connection: self-derating",
        [515] = "Off-grid running",
        [768] = "Shutdown: fault",
        [769] = "Shutdown: command",
        [770] = "Shutdown: OVGR",
        [771] = "Shutdown: communication disconnected",
        [772] = "Shutdown: power limited",
        [773] = "Shutdown: manual startup required",
        [774] = "Shutdown: DC switches disconnected",
        [775] = "Shutdown: rapid cutoff",
        [776] = "Shutdown: input underpower",
        [780] = "Shutdown: battery end of discharge",
        [1025] = "Grid scheduling: cosΦ-P curve",
        [1026] = "Grid scheduling: Q-U curve",
        [1027] = "Grid scheduling: PF-U curve",
        [1028] = "Grid scheduling: dry contact",
        [1029] = "Grid scheduling: Q-P curve",
        [1280] = "Spot-check ready",
        [1281] = "Spot-checking",
        [1536] = "Inspecting",
        [1792] = "AFCI self check",
        [2048] = "I-V scanning",
        [2304] = "DC input detection",
        [2560] = "Running: off-grid charging",
        [40960] = "Standby: no irradiation",
    };

    private static readonly Dictionary<int, string> BatteryRunningStatusCodes = new()
    {
        [0] = "offline",
        [1] = "standby",
        [2] = "running",
        [3] = "fault",
        [4] = "sleep mode",
    };

    #endregion

    private readonly MqttPublisher _mqtt;
    private readonly IConfiguration _config;
    private readonly ILogger<HuaweiModbusService> _logger;
    private readonly string _dongleIp;
    private readonly int _port;
    private readonly byte _unitId;
    private readonly int _pollSeconds;
    private readonly int _pvStringCount;
    private readonly string _inverterTopic;
    private readonly string _meterTopic;
    private readonly string _batteryTopic;

    private TcpClient? _tcpClient;
    private IModbusMaster? _master;

    public HuaweiModbusService(MqttPublisher mqtt, IConfiguration config, ILogger<HuaweiModbusService> logger)
    {
        _mqtt = mqtt;
        _config = config;
        _logger = logger;
        _dongleIp = config["Huawei:DongleIp"] ?? "192.168.200.1";
        _port = int.Parse(config["Huawei:Port"] ?? "502");
        _unitId = byte.Parse(config["Huawei:UnitId"] ?? "0");
        _pollSeconds = int.Parse(config["Huawei:PollSeconds"] ?? "30");
        _pvStringCount = int.Parse(config["Huawei:PvStringCount"] ?? "2");
        _inverterTopic = config["Huawei:Topic"] ?? "tele/huawei/pv";
        _meterTopic = config["Huawei:MeterTopic"] ?? "tele/huawei/meter";
        _batteryTopic = config["Huawei:BatteryTopic"] ?? "tele/huawei/battery";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (!_config.GetValue("Huawei:Enabled", true))
            {
                DisposeConnection();
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                continue;
            }

            try
            {
                await EnsureConnectedAsync(stoppingToken);

                await ReadAndPublishAsync("Inverter", _inverterTopic,
                    () => ReadBlockAsync(InverterBlockStart, InverterBlockCount, ParseInverterBlock),
                    stoppingToken);

                await ReadAndPublishAsync("Meter", _meterTopic,
                    () => ReadBlockAsync(MeterBlockStart, MeterBlockCount, raw => ParseFixedRegisters(MeterRegisters, raw)),
                    stoppingToken);

                await ReadAndPublishAsync("Batterie", _batteryTopic,
                    () => ReadBlockAsync(BatteryBlockStart, BatteryBlockCount, ParseBatteryBlock),
                    stoppingToken);
            }
            catch (Exception ex)
            {
                // Verbindungsebene (Connect/TCP) fehlgeschlagen - Verbindung verwerfen,
                // damit der nächste Zyklus sauber neu aufbaut. Betrifft alle drei
                // Blöcke gleichzeitig, da noch kein einzelner Block versucht wurde.
                _logger.LogWarning(ex, "Verbindung zum Dongle fehlgeschlagen, baue sie neu auf");
                DisposeConnection();

                foreach (var topic in new[] { _inverterTopic, _meterTopic, _batteryTopic })
                {
                    await PublishTimestampAsync(topic, "last_error", stoppingToken);
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(_pollSeconds), stoppingToken);
        }

        DisposeConnection();
    }

    /// <summary>
    /// Liest einen Block und published ihn, fängt Fehler aber pro Block ab (z.B.
    /// wenn keine Batterie installiert ist), damit die anderen Blöcke trotzdem
    /// weiterlaufen. last_success/last_error werden als reine Zeitstempel-Strings
    /// auf eigene Topics ({topic}/last_success, {topic}/last_error) published -
    /// kein JSON, kein value_template nötig, direkt als Home-Assistant-Timestamp-
    /// Sensor verwendbar.
    /// </summary>
    private async Task ReadAndPublishAsync(
        string label,
        string topic,
        Func<Task<Dictionary<string, object>?>> reader,
        CancellationToken cancellationToken)
    {
        try
        {
            var values = await reader();
            if (values is { Count: > 0 })
            {
                await _mqtt.PublishJsonAsync(topic, JsonSerializer.Serialize(values), cancellationToken);
                await PublishTimestampAsync(topic, "last_success", cancellationToken);
            }
        }
        catch (Exception ex)
        {
            // Sichtbar loggen (nicht nur Debug) und Verbindung verwerfen, damit der
            // nächste Zyklus garantiert frisch neu verbindet, statt möglicherweise
            // mit einer kaputten, aber als "Connected" markierten Verbindung weiterzulaufen.
            _logger.LogWarning(ex, "{Label}-Block konnte nicht gelesen werden, verwerfe Verbindung", label);
            DisposeConnection();

            await PublishTimestampAsync(topic, "last_error", cancellationToken);
        }
    }

    private Task PublishTimestampAsync(string baseTopic, string suffix, CancellationToken cancellationToken)
        => _mqtt.PublishJsonAsync($"{baseTopic}/{suffix}", DateTimeOffset.UtcNow.ToString("o"), cancellationToken);

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_tcpClient is { Connected: true } && _master is not null)
        {
            return;
        }

        DisposeConnection();

        var tcpClient = new TcpClient();
        var connectTask = tcpClient.ConnectAsync(_dongleIp, _port, cancellationToken).AsTask();
        var timeoutTask = Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
        if (await Task.WhenAny(connectTask, timeoutTask) == timeoutTask)
        {
            tcpClient.Dispose();
            throw new TimeoutException($"Verbindung zum Dongle {_dongleIp}:{_port} timed out");
        }

        var factory = new ModbusFactory();
        var master = factory.CreateMaster(tcpClient);
        master.Transport.ReadTimeout = 3000;

        _tcpClient = tcpClient;
        _master = master;
        _logger.LogInformation("Verbindung zum Dongle {Ip}:{Port} aufgebaut", _dongleIp, _port);
    }

    private void DisposeConnection()
    {
        try { _master?.Dispose(); } catch { /* egal, wird eh verworfen */ }
        try { _tcpClient?.Close(); } catch { /* egal, wird eh verworfen */ }
        _tcpClient?.Dispose();
        _master = null;
        _tcpClient = null;
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        DisposeConnection();
        return base.StopAsync(cancellationToken);
    }

    private async Task<Dictionary<string, object>?> ReadBlockAsync(
        int blockStart,
        int blockCount,
        Func<ushort[], Dictionary<string, object>> parse)
    {
        if (_master is null)
        {
            return null;
        }

        ushort[] raw = await _master.ReadHoldingRegistersAsync(_unitId, (ushort)blockStart, (ushort)blockCount);
        return parse(raw);
    }

    private Dictionary<string, object> ParseInverterBlock(ushort[] raw)
    {
        var result = new Dictionary<string, object>();

        // PV-Strings: 2 Register pro String (Spannung, Strom), direkt am Blockanfang
        for (int n = 1; n <= _pvStringCount; n++)
        {
            int voltageOffset = 2 * (n - 1);
            int currentOffset = voltageOffset + 1;
            if (currentOffset >= raw.Length) break;

            result[$"pv{n}_voltage"] = Math.Round(ToSigned16(raw[voltageOffset]) / 10.0, 2);
            result[$"pv{n}_current"] = Math.Round(ToSigned16(raw[currentOffset]) / 100.0, 2);
        }

        foreach (var kv in ParseFixedRegisters(InverterFixedRegisters, raw))
        {
            result[kv.Key] = kv.Value;
        }

        int statusCode = raw[DeviceStatusWordOffset];
        result["device_status_code"] = statusCode;
        result["device_status"] = DeviceStatusCodes.GetValueOrDefault(statusCode, $"unbekannt ({statusCode})");

        return result;
    }

    private Dictionary<string, object> ParseBatteryBlock(ushort[] raw)
    {
        var result = ParseFixedRegisters(BatteryRegisters, raw);

        if (result.TryGetValue("running_status", out var statusObj) && statusObj is double statusCode)
        {
            var code = (int)statusCode;
            result["running_status_text"] = BatteryRunningStatusCodes.GetValueOrDefault(code, $"unbekannt ({code})");
        }

        return result;
    }

    private static Dictionary<string, object> ParseFixedRegisters(RegisterDef[] registers, ushort[] raw)
    {
        var result = new Dictionary<string, object>();

        foreach (var reg in registers)
        {
            if (reg.WordOffset < 0 || reg.WordOffset + reg.Words > raw.Length)
            {
                continue; // Register liegt außerhalb des gelesenen Blocks - überspringen
            }

            uint combined;
            if (reg.Words == 2)
            {
                combined = ((uint)raw[reg.WordOffset] << 16) | raw[reg.WordOffset + 1];
            }
            else
            {
                combined = raw[reg.WordOffset];
            }

            double value;
            if (reg.Signed)
            {
                int signedValue = reg.Words == 2 ? unchecked((int)combined) : unchecked((short)combined);
                value = signedValue / reg.Gain;
            }
            else
            {
                value = combined / reg.Gain;
            }

            result[reg.Name] = Math.Round(value, 3);
        }

        return result;
    }

    private static short ToSigned16(ushort value) => unchecked((short)value);
}
