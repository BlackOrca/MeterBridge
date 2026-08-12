#!/bin/bash
# Watchdog: prüft ob der Huawei-Dongle über WLAN erreichbar ist, und stößt
# notfalls eine Neuverbindung an. Läuft unabhängig davon, ob NetworkManagers
# eigene autoconnect-retries-Logik im Einzelfall greift oder nicht - reine
# Holzhammer-Absicherung für unbeaufsichtigten Betrieb.

DONGLE_IP="192.168.200.1"
WIFI_CONNECTION="SDongleA-BT22C0879112"

if ! ping -c 1 -W 3 "$DONGLE_IP" > /dev/null 2>&1; then
    logger "meterbridge-watchdog: Dongle nicht erreichbar, versuche WLAN neu zu verbinden"
    nmcli connection up "$WIFI_CONNECTION" > /dev/null 2>&1
fi
