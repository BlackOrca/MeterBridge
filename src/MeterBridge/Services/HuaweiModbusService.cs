using System.Net.Sockets;
using System.Text.Json;
using MeterBridge.Mqtt;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NModbus;

namespace MeterBridge.Services;

/// <summary>
/// Liest zyklisch konfigurierbare Registergruppen ("Abrufgruppen") vom Huawei
/// SDongleA per Modbus TCP. Datenpunkte (Register/Gain/Signed/...) und
/// Abrufgruppen (Name/Intervall/Topic) kommen komplett aus Huawei:DataPoints
/// bzw. Huawei:PollGroups in appsettings.json - siehe HuaweiConfig.cs.
///
/// Jede Gruppe wird unabhängig von den anderen mit ihrem eigenen Intervall
/// gepollt und auf ihr eigenes MQTT-Topic published. Ein einzelner
/// fehlschlagender Gruppen-Read blockiert die anderen Gruppen nicht.
///
/// Registeradressen stammen aus der offiziellen Huawei "Solar Inverter Modbus
/// Interface Definitions" sowie Community-Dokumentation. Vor dem produktiven
/// Einsatz gegen dein konkretes Wechselrichter-Modell/Firmware verifizieren.
/// </summary>
public sealed class HuaweiModbusService : BackgroundService
{
    #region Status-Code-Übersetzungen

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

    private const int MaxRegistersPerRead = 125; // Modbus-Grenze für Function Code 3

    private readonly MqttPublisher _mqtt;
    private readonly IConfiguration _config;
    private readonly ILogger<HuaweiModbusService> _logger;
    private readonly string _dongleIp;
    private readonly int _port;
    private readonly byte _unitId;
    private readonly List<HuaweiPollGroupConfig> _pollGroups;
    private readonly Dictionary<string, List<HuaweiDataPointConfig>> _dataPointsByGroup;
    private readonly Dictionary<string, DateTime> _nextDueUtc;

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

        _pollGroups = config.GetSection("Huawei:PollGroups").Get<List<HuaweiPollGroupConfig>>() ?? [];
        var dataPoints = config.GetSection("Huawei:DataPoints").Get<List<HuaweiDataPointConfig>>() ?? [];

        var groupNames = _pollGroups.Select(g => g.Name).ToHashSet();
        foreach (var dp in dataPoints.Where(dp => !groupNames.Contains(dp.Group)))
        {
            _logger.LogWarning("Huawei-Datenpunkt {Name} referenziert unbekannte Abrufgruppe {Group}, wird ignoriert", dp.Name, dp.Group);
        }

        _dataPointsByGroup = dataPoints
            .Where(dp => groupNames.Contains(dp.Group))
            .GroupBy(dp => dp.Group)
            .ToDictionary(g => g.Key, g => g.ToList());

        if (_pollGroups.Count == 0 || dataPoints.Count == 0)
        {
            _logger.LogWarning("Huawei:PollGroups oder Huawei:DataPoints ist leer - es werden keine Register gelesen");
        }

        foreach (var group in _pollGroups)
        {
            var span = TryComputeSpan(group.Name);
            if (span is { Count: > MaxRegistersPerRead })
            {
                _logger.LogWarning(
                    "Abrufgruppe {Group} deckt {Count} Register ab (> {Max}), ein einzelner Modbus-Read könnte fehlschlagen",
                    group.Name, span.Value.Count, MaxRegistersPerRead);
            }
        }

        // Alle Gruppen sind beim ersten Zyklus sofort fällig.
        _nextDueUtc = _pollGroups.ToDictionary(g => g.Name, _ => DateTime.MinValue);
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

            if (_pollGroups.Count == 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                continue;
            }

            try
            {
                await EnsureConnectedAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                // Verbindungsebene (Connect/TCP) fehlgeschlagen - Verbindung verwerfen,
                // damit der nächste Zyklus sauber neu aufbaut. Betrifft alle Gruppen,
                // da noch keine einzelne Gruppe versucht wurde.
                _logger.LogWarning(ex, "Verbindung zum Dongle fehlgeschlagen, baue sie neu auf");
                DisposeConnection();

                foreach (var group in _pollGroups)
                {
                    await PublishTimestampAsync(group.Topic, "last_error", stoppingToken);
                }

                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                continue;
            }

            var now = DateTime.UtcNow;
            foreach (var group in _pollGroups.Where(g => now >= _nextDueUtc[g.Name]))
            {
                await ReadAndPublishGroupAsync(group, stoppingToken);
                _nextDueUtc[group.Name] = DateTime.UtcNow.AddSeconds(group.IntervalSeconds);
            }

            var delay = _nextDueUtc.Values.Min() - DateTime.UtcNow;
            if (delay < TimeSpan.FromSeconds(1))
            {
                delay = TimeSpan.FromSeconds(1);
            }

            await Task.Delay(delay, stoppingToken);
        }

        DisposeConnection();
    }

    /// <summary>
    /// Liest eine Abrufgruppe und published sie, fängt Fehler aber pro Gruppe ab
    /// (z.B. wenn keine Batterie installiert ist), damit die anderen Gruppen trotzdem
    /// weiterlaufen. last_success/last_error werden als reine Zeitstempel-Strings
    /// auf eigene Topics ({topic}/last_success, {topic}/last_error) published -
    /// kein JSON, kein value_template nötig, direkt als Home-Assistant-Timestamp-
    /// Sensor verwendbar.
    /// </summary>
    private async Task ReadAndPublishGroupAsync(HuaweiPollGroupConfig group, CancellationToken cancellationToken)
    {
        if (!_dataPointsByGroup.TryGetValue(group.Name, out var dataPoints) || dataPoints.Count == 0)
        {
            return;
        }

        try
        {
            var span = TryComputeSpan(group.Name);
            if (span is null)
            {
                return;
            }

            var raw = await ReadBlockAsync(span.Value.Start, span.Value.Count);
            if (raw is null)
            {
                return;
            }

            var values = ParseDataPoints(dataPoints, span.Value.Start, raw);
            ApplyEnrichment(values);

            if (values.Count > 0)
            {
                await _mqtt.PublishJsonAsync(group.Topic, JsonSerializer.Serialize(values), cancellationToken);
                await PublishTimestampAsync(group.Topic, "last_success", cancellationToken);
            }
        }
        catch (Exception ex)
        {
            // Sichtbar loggen (nicht nur Debug) und Verbindung verwerfen, damit der
            // nächste Zyklus garantiert frisch neu verbindet, statt möglicherweise
            // mit einer kaputten, aber als "Connected" markierten Verbindung weiterzulaufen.
            _logger.LogWarning(ex, "{Group}-Gruppe konnte nicht gelesen werden, verwerfe Verbindung", group.Name);
            DisposeConnection();

            await PublishTimestampAsync(group.Topic, "last_error", cancellationToken);
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

    /// <summary>
    /// Berechnet die minimale Registerspanne, die alle Datenpunkte einer Gruppe
    /// mit einem einzigen ReadHoldingRegisters-Aufruf abdeckt.
    /// </summary>
    private (int Start, int Count)? TryComputeSpan(string groupName)
    {
        if (!_dataPointsByGroup.TryGetValue(groupName, out var dataPoints) || dataPoints.Count == 0)
        {
            return null;
        }

        int start = dataPoints.Min(d => d.Register);
        int end = dataPoints.Max(d => d.Register + d.Words); // exklusiv
        return (start, end - start);
    }

    private async Task<ushort[]?> ReadBlockAsync(int blockStart, int blockCount)
    {
        if (_master is null)
        {
            return null;
        }

        return await _master.ReadHoldingRegistersAsync(_unitId, (ushort)blockStart, (ushort)blockCount);
    }

    private static Dictionary<string, object> ParseDataPoints(List<HuaweiDataPointConfig> dataPoints, int blockStart, ushort[] raw)
    {
        var result = new Dictionary<string, object>();

        foreach (var dp in dataPoints)
        {
            int offset = dp.Register - blockStart;
            if (offset < 0 || offset + dp.Words > raw.Length)
            {
                continue; // Register liegt außerhalb des gelesenen Blocks - überspringen
            }

            uint combined;
            if (dp.Words == 2)
            {
                combined = ((uint)raw[offset] << 16) | raw[offset + 1];
            }
            else
            {
                combined = raw[offset];
            }

            double value;
            if (dp.Signed)
            {
                int signedValue = dp.Words == 2 ? unchecked((int)combined) : unchecked((short)combined);
                value = signedValue / dp.Gain;
            }
            else
            {
                value = combined / dp.Gain;
            }

            result[dp.Name] = Math.Round(value, 3);
        }

        return result;
    }

    // Bleibt bewusst als kleines, namensbasiertes Enrichment hartcodiert (nicht in
    // Config) - reine Anzeige-Übersetzung von Rohcode auf Text, läuft für jede
    // Gruppe, die den jeweiligen Datenpunkt enthält.
    private static void ApplyEnrichment(Dictionary<string, object> values)
    {
        if (values.TryGetValue("device_status_code", out var dsObj) && dsObj is double ds)
        {
            int code = (int)ds;
            values["device_status"] = DeviceStatusCodes.GetValueOrDefault(code, $"unbekannt ({code})");
        }

        if (values.TryGetValue("running_status", out var rsObj) && rsObj is double rs)
        {
            int code = (int)rs;
            values["running_status_text"] = BatteryRunningStatusCodes.GetValueOrDefault(code, $"unbekannt ({code})");
        }
    }
}
