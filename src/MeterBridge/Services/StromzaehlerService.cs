using System.IO.Ports;
using System.Text.Json;
using MeterBridge.Mqtt;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MeterBridge.Services;

/// <summary>
/// Liest den rohen Bytestrom vom IR-Lesekopf, erkennt vollständige SML-Datagramme
/// anhand der Start-/End-Escape-Sequenzen und extrahiert daraus gezielt die drei
/// OBIS-Werte, die aus der alten Tasmota-Skriptkonfiguration für den Zähler
/// "MT631" bekannt sind (Bezug 1.8.0, Einspeisung 2.8.0, Momentanleistung 16.7.0,
/// jeweils durch 20 geteilt).
///
/// WICHTIG: Das Überspringen der optionalen SML-Felder (Status/Zeitstempel/
/// Einheit/Skalierung) zwischen OBIS-Marker und eigentlichem Wert basiert auf der
/// dokumentierten SML-TLV-Struktur, ist aber nicht gegen echte Rohdaten dieses
/// Zählers getestet. Falls die Werte unplausibel sind (z.B. absurd groß/klein
/// oder immer 0), bitte einen Hexdump von /dev/serial0 schicken.
///
/// SML-Rahmen: Start = 1B 1B 1B 1B 01 01 01 01, Ende = 1B 1B 1B 1B 1A + Füllbyte-
/// Anzahl + 2 Byte CRC16.
/// </summary>
public sealed class StromzaehlerService : BackgroundService
{
    private static readonly byte[] StartSequence = { 0x1B, 0x1B, 0x1B, 0x1B, 0x01, 0x01, 0x01, 0x01 };
    private static readonly byte[] EndMarker = { 0x1B, 0x1B, 0x1B, 0x1B, 0x1A };

    // Aus der alten Tasmota-Skriptkonfiguration für Zähler "MT631" übernommen:
    // +1,3,s,0,9600,MT631
    // 1,77070100010800ff@20,Gesamtverbrauch,Wh,1.8.0,2
    // 1,77070100020800ff@20,Gesamteinspeisung,Wh,2.8.0,2
    // 1,77070100100700ff@20,Verbrauch,W,16.7.0,0
    private static readonly (string Key, byte[] Marker, double Divisor)[] ObisFields =
    {
        ("Bezug",       Convert.FromHexString("77070100010800ff"), 1.0),
        ("Einspeisung", Convert.FromHexString("77070100020800ff"), 1.0),
        ("Verbrauch",   Convert.FromHexString("77070100100700ff"), 1.0),
    };

    private readonly MqttPublisher _mqtt;
    private readonly IConfiguration _config;
    private readonly ILogger<StromzaehlerService> _logger;
    private readonly string _portName;
    private readonly int _baudRate;
    private readonly string _topic;

    public StromzaehlerService(MqttPublisher mqtt, IConfiguration config, ILogger<StromzaehlerService> logger)
    {
        _mqtt = mqtt;
        _config = config;
        _logger = logger;
        _portName = config["Stromzaehler:SerialPort"] ?? "/dev/ttyUSB0";
        _baudRate = int.Parse(config["Stromzaehler:BaudRate"] ?? "9600");
        _topic = config["Stromzaehler:Topic"] ?? "tele/stromzaehler/pv";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (!_config.GetValue("Stromzaehler:Enabled", true))
            {
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                continue;
            }

            try
            {
                await ReadLoopAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Serielle Verbindung zum IR-Lesekopf unterbrochen, versuche erneut in 5s");
                await PublishTimestampAsync("last_error", stoppingToken);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    /// <summary>
    /// Published einen reinen Zeitstempel-String (kein JSON) auf {Topic}/{suffix},
    /// z.B. tele/stromzaehler/pv/last_success - direkt als Home-Assistant-
    /// Timestamp-Sensor verwendbar, ohne value_template.
    /// </summary>
    private Task PublishTimestampAsync(string suffix, CancellationToken cancellationToken)
        => _mqtt.PublishJsonAsync($"{_topic}/{suffix}", DateTimeOffset.UtcNow.ToString("o"), cancellationToken);

    private async Task ReadLoopAsync(CancellationToken stoppingToken)
    {
        using var port = new SerialPort(_portName, _baudRate, Parity.None, 8, StopBits.One)
        {
            ReadTimeout = 5000,
        };
        port.Open();
        _logger.LogInformation("IR-Lesekopf geöffnet auf {Port} @ {Baud} Baud", _portName, _baudRate);

        var buffer = new List<byte>(2048);
        var readBuf = new byte[1024];

        while (!stoppingToken.IsCancellationRequested)
        {
            if (!_config.GetValue("Stromzaehler:Enabled", true))
            {
                _logger.LogInformation("Stromzähler deaktiviert, schließe IR-Lesekopf");
                return;
            }

            int n;
            try
            {
                n = await port.BaseStream.ReadAsync(readBuf, stoppingToken);
            }
            catch (TimeoutException)
            {
                continue;
            }

            if (n <= 0) continue;
            buffer.AddRange(readBuf.AsSpan(0, n).ToArray());

            // Nach vollständigem Frame suchen (Start...Ende), verarbeiten, dann
            // alles bis zum Frame-Ende aus dem Buffer entfernen.
            int startIdx = IndexOf(buffer, StartSequence, 0);
            if (startIdx < 0)
            {
                if (buffer.Count > 8192) buffer.Clear(); // Sicherheitsnetz gegen unbegrenztes Wachsen
                continue;
            }

            int endIdx = IndexOf(buffer, EndMarker, startIdx + StartSequence.Length);
            if (endIdx < 0)
            {
                if (buffer.Count > 8192) buffer.RemoveRange(0, startIdx); // Müll vor Start verwerfen
                continue;
            }

            // Frame inkl. Endmarker + 3 Folgebytes (Füllbyte-Anzahl + CRC16) extrahieren
            int frameEnd = Math.Min(endIdx + EndMarker.Length + 3, buffer.Count);
            var frame = buffer.GetRange(startIdx, frameEnd - startIdx).ToArray();
            buffer.RemoveRange(0, frameEnd);

            _logger.LogDebug("SML-Frame erkannt, {Bytes} Bytes", frame.Length);

            var values = ParseSmlFrame(frame);
            if (values is { Count: > 0 })
            {
                await _mqtt.PublishJsonAsync(_topic, JsonSerializer.Serialize(values), stoppingToken);
                await PublishTimestampAsync("last_success", stoppingToken);
            }
        }
    }

    /// <summary>
    /// Sucht alle bekannten OBIS-Marker im Frame und liest jeweils den direkt
    /// darauffolgenden SML-Wert aus (nach Überspringen von Status/Zeitstempel/
    /// Einheit/Skalierung).
    /// </summary>
    private Dictionary<string, double>? ParseSmlFrame(byte[] frame)
    {
        _logger.LogDebug("Frame-Hexdump: {Hex}", Convert.ToHexString(frame));
        LogAllObisEntries(frame);

        var result = new Dictionary<string, double>();

        foreach (var (key, marker, divisor) in ObisFields)
        {
            int idx = IndexOf(frame, marker, 0);
            if (idx < 0)
            {
                _logger.LogDebug("OBIS-Marker für {Key} nicht im Frame gefunden", key);
                continue;
            }

            int offset = idx + marker.Length;
            try
            {
                SkipElement(frame, ref offset); // status (optional)
                SkipElement(frame, ref offset); // valTime (optional)
                SkipElement(frame, ref offset); // unit (optional)
                SkipElement(frame, ref offset); // scaler (optional, bewusst ignoriert -
                                                 // wir nutzen den fest kalibrierten Divisor
                                                 // aus der alten Tasmota-Konfiguration)
                long raw = ReadIntegerElement(frame, ref offset); // value
                result[key] = Math.Round(raw / divisor, 3);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "OBIS-Feld {Key} konnte nicht geparst werden", key);
            }
        }

        return result;
    }

    /// <summary>
    /// Vollständiges Diagnose-Logging: scannt den kompletten Frame nach JEDEM
    /// OBIS-artigen Muster (nicht nur unseren drei bekannten Markern) und loggt
    /// Rohwert, Scaler und den daraus berechneten Wert. Hilfreich um z.B. zu
    /// prüfen, ob dein Zähler in anderen Frames doch mal 16.7.0 (Momentanleistung)
    /// mitsendet, oder um generell zu sehen, was er sonst noch überträgt.
    /// </summary>
    private void LogAllObisEntries(byte[] frame)
    {
        for (int i = 0; i <= frame.Length - 8; i++)
        {
            // Muster: 77 (Liste mit 7 Elementen) 07 (Octet-String Länge 6) + 6 Byte OBIS-Code
            if (frame[i] != 0x77 || frame[i + 1] != 0x07)
            {
                continue;
            }

            byte[] obis = frame[(i + 2)..(i + 8)];
            string obisStr = $"{obis[0]}-{obis[1]}:{obis[2]}.{obis[3]}.{obis[4]}*{obis[5]}";
            int offset = i + 8;

            try
            {
                SkipElement(frame, ref offset); // status
                SkipElement(frame, ref offset); // valTime
                SkipElement(frame, ref offset); // unit
                long scaler = ReadIntegerElement(frame, ref offset);
                long raw = ReadIntegerElement(frame, ref offset);
                double scaled = raw * Math.Pow(10, scaler);
                _logger.LogDebug(
                    "OBIS {Obis} @Offset {Offset}: raw={Raw} scaler={Scaler} → {Scaled}",
                    obisStr, i, raw, scaler, scaled);
            }
            catch (Exception ex)
            {
                // Häufig einfach ein False-Positive-Treffer auf zufällige Bytes,
                // die nicht wirklich ein OBIS-Eintrag sind - deswegen nur Debug,
                // kein Warning.
                _logger.LogDebug(ex, "OBIS {Obis} @Offset {Offset}: konnte nicht dekodiert werden (evtl. falscher Treffer)", obisStr, i);
            }
        }
    }

    /// <summary>
    /// Überspringt genau ein SML-TLV-Element ab <paramref name="offset"/>, inkl.
    /// rekursivem Überspringen bei Listen (Typ-Nibble 7). Ein TL-Byte 0x01 (Typ
    /// Octet-String, Länge 1 = 0 Nutzbytes) entspricht dabei automatisch dem
    /// SML-Konventionswert für "optionales Feld nicht vorhanden".
    /// Hinweis: Mehrbyte-Längenfelder (TL-Byte mit gesetztem 0x80-Bit) werden
    /// hier nicht unterstützt - für die kurzen Felder in einem SML_ListEntry
    /// (Status/Zeit/Einheit/Skalierung) in der Praxis nicht nötig.
    /// </summary>
    private static void SkipElement(byte[] buf, ref int offset)
    {
        if (offset >= buf.Length) throw new InvalidOperationException("Frame zu kurz");

        byte tl = buf[offset];
        int type = (tl & 0x70) >> 4;
        int len = tl & 0x0F;

        if (type == 7) // Liste: Länge = Anzahl Elemente, nicht Bytes
        {
            offset += 1;
            for (int i = 0; i < len; i++)
            {
                SkipElement(buf, ref offset);
            }
        }
        else
        {
            offset += Math.Max(len, 1); // len inkl. TL-Byte selbst; len=0 als Sicherheitsnetz vermeiden
        }
    }

    /// <summary>
    /// Liest ein SML-TLV-Element als Integer (signed bei Typ 5, sonst unsigned).
    /// </summary>
    private static long ReadIntegerElement(byte[] buf, ref int offset)
    {
        if (offset >= buf.Length) throw new InvalidOperationException("Frame zu kurz");

        byte tl = buf[offset];
        int type = (tl & 0x70) >> 4;
        int len = tl & 0x0F;
        int payloadLen = Math.Max(0, len - 1);
        int payloadStart = offset + 1;

        offset += Math.Max(len, 1);

        if (payloadLen == 0 || payloadStart + payloadLen > buf.Length)
        {
            return 0;
        }

        long result = 0;
        for (int i = 0; i < payloadLen; i++)
        {
            result = (result << 8) | buf[payloadStart + i];
        }

        bool signed = type == 5;
        if (signed && payloadLen < 8 && (buf[payloadStart] & 0x80) != 0)
        {
            result |= -1L << (payloadLen * 8); // Vorzeichen-Erweiterung
        }

        return result;
    }

    private static int IndexOf(List<byte> haystack, byte[] needle, int startAt)
    {
        for (int i = startAt; i <= haystack.Count - needle.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j]) { match = false; break; }
            }
            if (match) return i;
        }
        return -1;
    }

    private static int IndexOf(byte[] haystack, byte[] needle, int startAt)
    {
        for (int i = startAt; i <= haystack.Length - needle.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j]) { match = false; break; }
            }
            if (match) return i;
        }
        return -1;
    }
}
