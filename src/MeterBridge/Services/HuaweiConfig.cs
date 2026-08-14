namespace MeterBridge.Services;

/// <summary>
/// Bindungs-Records für Huawei:PollGroups / Huawei:DataPoints aus appsettings.json.
/// Werden sowohl von HuaweiModbusService (Registeradressen/Polling) als auch von
/// HomeAssistantDiscoveryService (Discovery-Configs) gelesen - deshalb hier zentral
/// statt als private Records in einer der beiden Dateien, um die beiden Verbraucher
/// nicht wieder manuell in Sync halten zu müssen.
/// </summary>
public sealed record HuaweiPollGroupConfig
{
    public string Name { get; init; } = "";
    public int IntervalSeconds { get; init; } = 30;
    public string Topic { get; init; } = "";

    /// <summary>HA-Device-Bucket: "inverter" | "meter" | "battery".</summary>
    public string Device { get; init; } = "";
}

public sealed record HuaweiDataPointConfig
{
    /// <summary>Technischer Key: JSON-Payload-Key, value_template-Key, Default-Object-Id.</summary>
    public string Name { get; init; } = "";

    /// <summary>Absolute Modbus-Registeradresse (z.B. 32064), keine Offset-Angabe.</summary>
    public int Register { get; init; }

    /// <summary>1 oder 2 Register (16 oder 32 Bit).</summary>
    public int Words { get; init; } = 1;

    public double Gain { get; init; } = 1.0;
    public bool Signed { get; init; }

    /// <summary>Name der PollGroup, zu der dieser Datenpunkt gehört.</summary>
    public string Group { get; init; } = "";

    /// <summary>HA-Anzeigename; wenn null, wird Name verwendet.</summary>
    public string? Label { get; init; }
    public string? Unit { get; init; }
    public string? DeviceClass { get; init; }
    public string? StateClass { get; init; }
    public string? EntityCategory { get; init; }

    /// <summary>
    /// MQTT-Discovery object_id/unique_id-Suffix; wenn null, wird Name verwendet.
    /// Nur zur Wahrung von Bestands-unique_ids gedacht (z.B. "meter_voltage_a").
    /// </summary>
    public string? HaObjectId { get; init; }
}
