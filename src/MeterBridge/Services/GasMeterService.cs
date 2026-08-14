using System.Device.Gpio;
using System.Globalization;
using System.Text.Json;
using MeterBridge.Mqtt;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MeterBridge.Services;

/// <summary>
/// Zählt Impulse eines potenzialfreien Kontakts am Gaszähler über GPIO.
/// Der Kontakt zieht bei Schließen den Pin gegen GND (interner Pull-Up aktiv),
/// gezählt wird auf der fallenden Flanke.
///
/// Der veröffentlichte Zählerstand (verbrauch_m3) setzt sich zusammen aus einem
/// Offset (dem "echten" Ausgangswert, initial aus appsettings.json, danach live
/// per MQTT setzbar) plus den seither gezählten Impulsen. Sowohl Impulszahl als
/// auch Offset werden persistiert, damit ein Neustart nichts verliert.
///
/// Offset setzen (z.B. aus IP-Symcon heraus): eine Zahl (z.B. "5568.93", Punkt
/// als Dezimaltrennzeichen) auf das in GasMeter:SetTopic konfigurierte Topic
/// publishen. Der Service übernimmt den Wert sofort als neuen Ist-Stand.
/// </summary>
public sealed class GasMeterService : BackgroundService
{
    private readonly record struct State(long PulseCount, double OffsetM3);

    private readonly MqttPublisher _mqtt;
    private readonly IConfiguration _config;
    private readonly ILogger<GasMeterService> _logger;
    private readonly int _pin;
    private readonly double _m3PerPulse;
    private readonly int _debounceMs;
    private readonly int _pollIntervalMs;
    private readonly string _stateFilePath;
    private readonly string _topic;
    private readonly string _setTopic;
    private readonly double _configDefaultOffsetM3;

    private GpioController? _controller;
    private CancellationTokenSource? _pollCts;
    private Task? _pollTask;
    private long _pulseCount;
    private double _offsetM3;
    private DateTime _lastPulseUtc = DateTime.MinValue;
    private readonly object _lock = new();

    public GasMeterService(MqttPublisher mqtt, IConfiguration config, ILogger<GasMeterService> logger)
    {
        _mqtt = mqtt;
        _config = config;
        _logger = logger;
        _pin = int.Parse(config["GasMeter:GpioPin"] ?? "17");
        _m3PerPulse = double.Parse(config["GasMeter:CubicMetersPerPulse"] ?? "0.01", CultureInfo.InvariantCulture);
        _debounceMs = int.Parse(config["GasMeter:DebounceMilliseconds"] ?? "100");
        _pollIntervalMs = int.Parse(config["GasMeter:PollIntervalMilliseconds"] ?? "5");
        _stateFilePath = config["GasMeter:StateFilePath"] ?? "gasmeter_state.json";
        _topic = config["GasMeter:Topic"] ?? "tele/gaszaehler/pv";
        _setTopic = config["GasMeter:SetTopic"] ?? "cmnd/gaszaehler/zaehlerstand";
        _configDefaultOffsetM3 = double.Parse(config["GasMeter:StartOffsetM3"] ?? "0", CultureInfo.InvariantCulture);
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        LoadState();

        // Impulse werden per Polling statt per GPIO-Interrupt erkannt: Das
        // sysfs-Interrupt-Interface, das System.Device.Gpio dafür intern
        // nutzt (/sys/class/gpio/gpioN/edge), ist auf aktuellen Raspberry Pi
        // OS-Versionen (Bookworm+) unzuverlässig - die udev-Regel, die die
        // Zugriffsrechte auf frisch exportierte Pins setzt, fehlt dort, was
        // zu einem Timeout beim Start führt. Direktes Lesen über
        // /dev/gpiomem funktioniert dagegen weiterhin problemlos.
        _controller = new GpioController();
        _controller.OpenPin(_pin, PinMode.InputPullUp);

        _pollCts = new CancellationTokenSource();
        _pollTask = Task.Run(() => PollPinAsync(_pollCts.Token));

        await _mqtt.SubscribeAsync(_setTopic, OnSetZaehlerstandAsync, cancellationToken);

        _logger.LogInformation(
            "Gaszähler-GPIO {Pin} aktiv, aktueller Stand: {Value} m³ ({Count} Impulse seit Offset-Setzung)",
            _pin, Math.Round(_offsetM3 + _pulseCount * _m3PerPulse, 3), _pulseCount);

        await base.StartAsync(cancellationToken);
    }

    private Task OnSetZaehlerstandAsync(string payload)
    {
        if (!double.TryParse(payload.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var newValue))
        {
            _logger.LogWarning("Ungültiger Wert für Gaszähler-Stand per MQTT empfangen: '{Payload}'", payload);
            return Task.CompletedTask;
        }

        lock (_lock)
        {
            // Offset so setzen, dass Offset + bisherige Impulse * m3PerPulse = newValue,
            // damit ab jetzt weitergezählte Impulse korrekt oben draufaddiert werden.
            _offsetM3 = newValue - _pulseCount * _m3PerPulse;
            SaveState(_pulseCount, _offsetM3);
        }

        _logger.LogInformation("Gaszähler-Stand per MQTT auf {Value} m³ gesetzt", newValue);
        return Task.CompletedTask;
    }

    private async Task PollPinAsync(CancellationToken token)
    {
        var previous = _controller!.Read(_pin);
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_pollIntervalMs, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            var current = _controller.Read(_pin);
            if (previous == PinValue.High && current == PinValue.Low)
            {
                OnPulse();
            }
            previous = current;
        }
    }

    private void OnPulse()
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            if ((now - _lastPulseUtc).TotalMilliseconds < _debounceMs)
            {
                return; // Prellen des Kontakts ignorieren
            }
            _lastPulseUtc = now;
            _pulseCount++;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Alle 30s: Stand publishen und persistieren. Reine Pulszählung passiert
        // asynchron im GPIO-Callback oben, unabhängig von diesem Intervall.
        while (!stoppingToken.IsCancellationRequested)
        {
            if (!_config.GetValue("GasMeter:Enabled", true))
            {
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                continue;
            }

            long currentCount;
            double currentOffset;
            lock (_lock)
            {
                currentCount = _pulseCount;
                currentOffset = _offsetM3;
            }

            var payload = new Dictionary<string, object>
            {
                ["pulse_count"] = currentCount,
                ["verbrauch_m3"] = Math.Round(currentOffset + currentCount * _m3PerPulse, 3),
            };

            await _mqtt.PublishJsonAsync(_topic, JsonSerializer.Serialize(payload), stoppingToken);
            await _mqtt.PublishJsonAsync($"{_topic}/last_success", DateTimeOffset.UtcNow.ToString("o"), stoppingToken);
            SaveState(currentCount, currentOffset);

            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }

    private void LoadState()
    {
        try
        {
            if (File.Exists(_stateFilePath))
            {
                var json = File.ReadAllText(_stateFilePath);
                var state = JsonSerializer.Deserialize<State>(json);
                _pulseCount = state.PulseCount;
                _offsetM3 = state.OffsetM3;
                return;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Konnte Gaszähler-Stand nicht aus {Path} laden, starte bei 0", _stateFilePath);
        }

        // Kein State-File vorhanden (Erststart) - Konfigurationswert als Startpunkt nehmen
        _pulseCount = 0;
        _offsetM3 = _configDefaultOffsetM3;
    }

    private void SaveState(long count, double offsetM3)
    {
        try
        {
            var json = JsonSerializer.Serialize(new State(count, offsetM3));
            File.WriteAllText(_stateFilePath, json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Konnte Gaszähler-Stand nicht speichern");
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_pollCts is not null)
        {
            await _pollCts.CancelAsync();
            if (_pollTask is not null)
            {
                await _pollTask;
            }
            _pollCts.Dispose();
        }

        _controller?.Dispose();
        await base.StopAsync(cancellationToken);
    }
}
