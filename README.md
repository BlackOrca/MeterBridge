# Huawei/Stromzähler/Gaszähler → MQTT Bridge (Raspberry Pi)

Grundgerüst, kein fertiges Produkt. Alle drei Services (Huawei Modbus,
Gaszähler-GPIO, Stromzähler-SML) sind implementiert. Der SML-Parser im
Stromzähler-Service ist dabei fix auf die OBIS-Codes und den Divisor aus
der alten Tasmota-Konfiguration für den Zähler "MT631" eingestellt (siehe
unten) - gegen echte Rohdaten noch nicht verifiziert.

## 1. Netzwerk auf dem Pi einrichten

Ziel: Ethernet bleibt Default-Route fürs Heimnetz/MQTT-Broker, WLAN verbindet
sich nur mit dem isolierten Dongle-Netz, ohne dass dessen (nutzloses) Gateway
die Default-Route kapert.

**Wichtig:** Aktuelles Raspberry Pi OS (Bookworm und neuer) nutzt standardmäßig
NetworkManager, nicht mehr dhcpcd. `/etc/dhcpcd.conf` existiert dort gar nicht
mehr bzw. wird ignoriert - falls du eine ältere Anleitung mit dhcpcd findest,
gilt sie hier nicht. Mit `nmcli`:

```bash
sudo nmcli device wifi connect "SDongleA-XXXXXXXX" password "<dongle-passwort>"
sudo nmcli connection modify "SDongleA-XXXXXXXX" ipv4.never-default yes
```

`ipv4.never-default yes` ist das NetworkManager-Äquivalent zum alten dhcpcd
`nogateway`: WLAN bekommt weiterhin eine lokale Route ins Dongle-Subnetz,
aber sein Gateway wird nie zur System-Default-Route.

Prüfen mit `ip route`: die Default-Route (`default via ...`) sollte über
`eth0` laufen, für `192.168.200.0/24` sollte eine direkte Route über `wlan0`
stehen, aber **keine** zweite `default`-Zeile über `wlan0`.

## 2. Hardware anschließen

- **IR-Lesekopf** (Stromzähler): per USB, taucht i.d.R. als `/dev/ttyUSB0`
  auf (`ls /dev/ttyUSB*` zum Prüfen, ggf. Port in `appsettings.json`
  anpassen).
- **Gaszähler-Kontakt**: zwischen GPIO17 (physischer Pin 11) und einem GND-Pin
  (z.B. Pin 9). Der interne Pull-Up wird im Code aktiviert, der Kontakt zieht
  beim Schließen gegen GND. Pin bei Bedarf in `appsettings.json` anpassen.
  `CubicMetersPerPulse` auf den tatsächlichen Wert deines Zählers setzen
  (steht meist auf dem Zähler selbst, z.B. 0.01 m³/Impuls).

## 3. Bauen und deployen

### Als Container (empfohlen)

Quellcode liegt in `src/MeterBridge/`. Das Image wird ohne Dockerfile über
das im .NET SDK eingebaute `PublishContainer`-Target gebaut und direkt nach
`ghcr.io/blackorca/meterbridge` gepusht (Konfiguration dazu steht in
`src/MeterBridge/MeterBridge.csproj`).

Auf dem Windows-Rechner einmalig einloggen, dann bauen/pushen:

```powershell
docker login ghcr.io -u <github-user> -p <PAT mit write:packages>
pwsh scripts/build-push.ps1            # Patch-Version hochzählen (Default)
pwsh scripts/build-push.ps1 -Bump minor -Arch arm   # z.B. für 32-bit Pi
```

Auf dem Pi reicht anschließend ein Deploy-Verzeichnis mit `compose.yml`
(Projekt-Root) und einer eigenen `appsettings.json` (mit den echten
Zugangsdaten/Ports für diesen Pi, siehe Abschnitt 2 unten):

```bash
docker compose pull
docker compose up -d
docker compose logs -f
```

`compose.yml` mappt `/dev/gpiomem`, `/dev/gpiochip0`, `/dev/ttyUSB0` und
`/dev/serial0` durch - nicht vorhandene Geräte auf dem jeweiligen Pi dort
entfernen, sonst schlägt der Start fehl. Der Gaszähler-Stand wird per
Bind-Mount in `gasmeter_state.json` im Deploy-Verzeichnis persistiert -
diese Datei vor dem ersten Start einmal leer anlegen (`touch
gasmeter_state.json`), sonst legt Docker dort ein Verzeichnis statt einer
Datei an und der Stand geht bei jedem Neustart verloren.

### Ohne Container (klassisch)

```bash
dotnet publish src/MeterBridge/MeterBridge.csproj -c Release -r linux-arm64 --self-contained false
# Ergebnis nach /home/pi/meterbridge kopieren (scp o.ä.)
```

`-r linux-arm64` für 64-bit Raspberry Pi OS, `-r linux-arm` für 32-bit.
`--self-contained false` setzt eine installierte .NET-Runtime auf dem Pi
voraus (`sudo apt install dotnet-runtime-10.0`, oder falls das Paket in
deiner Distro noch nicht verfügbar ist, über dotnet-install.sh von
Microsoft). Wichtig: Projekt zielt jetzt auf net10.0 - auf dem Pi muss die
Runtime-Major-Version zur `TargetFramework` in der .csproj passen.

Als systemd-Service (`/etc/systemd/system/meterbridge.service`):

```ini
[Unit]
Description=Meter Bridge
After=network-online.target
Wants=network-online.target

[Service]
WorkingDirectory=/home/pi/meterbridge
ExecStart=/usr/bin/dotnet /home/pi/meterbridge/MeterBridge.dll
Restart=always
RestartSec=10
User=pi
# Für GPIO-Zugriff ohne root: pi-User muss in der gpio-Gruppe sein
# (sudo usermod -aG gpio pi), für /dev/ttyUSB0 in der dialout-Gruppe
# (sudo usermod -aG dialout pi)

[Install]
WantedBy=multi-user.target
```

```bash
sudo systemctl enable --now meterbridge
journalctl -u meterbridge -f
```

## 4. Offene Punkte

- **Stromzähler/SML**: `StromzaehlerService.cs` erkennt vollständige
  SML-Frames und liest drei fest verdrahtete OBIS-Codes aus (Bezug 1.8.0,
  Einspeisung 2.8.0, Momentanleistung 16.7.0), inklusive Divisor - 1:1
  übernommen aus der alten Tasmota-Skriptkonfiguration für den Zähler
  "MT631". Der SML-Scaler pro Feld wird dabei bewusst ignoriert, da der
  Divisor schon fest kalibriert ist. Falls dein Zähler andere OBIS-Codes
  liefert oder die Werte unplausibel sind: `LogAllObisEntries` loggt auf
  Debug-Level *alle* im Frame gefundenen OBIS-Einträge samt Rohwert,
  Scaler und berechnetem Wert (`Logging:LogLevel:MeterBridge.Services.StromzaehlerService`
  in `appsettings.json` steht bereits auf `Debug`) - damit lassen sich die
  `ObisFields`-Marker in `StromzaehlerService.cs` an den eigenen Zähler
  anpassen.
- **NuGet-Paketversionen** in der `.csproj` sind Momentaufnahmen - vor dem
  ersten Build lieber mit `dotnet add package <Name>` die aktuell verfügbaren
  Versionen ziehen, statt die eingetragenen blind zu vertrauen.
- **Register-Adressen** (Huawei) wie schon beim Tasmota-Ansatz: gegen dein
  konkretes Modell/Firmware verifizieren.
- Code ist nicht kompiliert/getestet (keine .NET-Umgebung in diesem
  Container verfügbar) - vor dem Deployment auf dem Pi bzw. in einer
  normalen Dev-Umgebung durchbauen.
