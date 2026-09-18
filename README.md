# MyDyson (Windows)

Application Windows non officielle pour contrôler le robot aspirateur **Dyson Spot+Scrub AI** (nom interne RB05).

Le robot n'expose aucun service sur le réseau local : il n'est joignable que via le cloud Dyson
(MQTT sur WebSocket vers AWS IoT). Ce dépôt reproduit donc le protocole de l'application mobile MyDyson.

## État du projet

Étape 1 : prototype en ligne de commande (`src/MyDyson.Cli`) au-dessus d'une bibliothèque (`src/MyDyson.Core`).

- `MyDyson.Core`
  - `DysonCloudClient` : API REST `appapi.cp.dyson.com` (login par e-mail + mot de passe + code à usage unique, manifest, credentials AWS IoT).
  - `RobotMqttClient` : connexion MQTT-over-WebSocket au broker AWS IoT avec le custom authorizer Dyson, abonnement `+/{serial}/#`, commandes connues.
  - `SessionStore` : bearer token chiffré avec DPAPI dans `%APPDATA%\MyDyson\session.bin`.
- `MyDyson.Cli` : commandes `login`, `devices`, `iot`, `status`, `watch`, `send`, `api`.

## Prérequis

- Windows 10/11, [.NET SDK 10](https://dotnet.microsoft.com/download)
- Un compte MyDyson avec le robot déjà enregistré dans l'application mobile

## Utilisation

```bash
dotnet build
dotnet run --project src/MyDyson.Cli -- login --country CH --culture fr-CH
dotnet run --project src/MyDyson.Cli -- devices
dotnet run --project src/MyDyson.Cli -- status --serial XXX-XX-XXXXXXXX
dotnet run --project src/MyDyson.Cli -- watch  --serial XXX-XX-XXXXXXXX --log capture.jsonl
dotnet run --project src/MyDyson.Cli -- send   --serial XXX-XX-XXXXXXXX start
```

`watch --log` enregistre tous les messages MQTT en JSON Lines : c'est la capture de référence à faire
pendant un cycle lancé depuis l'application officielle (étape 2 du plan).

## Protocole (résumé)

| Étape | Requête |
|---|---|
| Provisioning (obligatoire avant tout) | `GET /v1/provisioningservice/application/Android/version` |
| Statut du compte | `POST /v3/userregistration/email/userstatus?country=CH` |
| Début de login, envoi du code | `POST /v3/userregistration/email/auth?country=CH&culture=fr-CH` |
| Fin de login, bearer token | `POST /v3/userregistration/email/verify?country=CH&culture=fr-CH` |
| Appareils du compte | `GET /v3/manifest` |
| Credentials AWS IoT (courte durée) | `POST /v2/authorize/iot-credentials` `{ "Serial": "..." }` |
| Broker | `wss://{Endpoint}/mqtt?x-amz-customauthorizer-name=…&token=…&x-amz-customauthorizer-signature=…` |

Topics MQTT : `RB05/{serial}/status`, `RB05/{serial}/status/jdm`, `RB05/{serial}/command`, `RB05/{serial}/command/jdm`.

## Avertissements

- API non officielle, susceptible de changer sans préavis. Usage personnel.
- Les fichiers APK dans `APK/` servent uniquement à l'analyse et ne sont pas versionnés.

## Références

- [thoukydides/matterbridge-dyson-robot](https://github.com/thoukydides/matterbridge-dyson-robot) (issue #46 : capture complète RB05)
- [UNsync3D/ha-dyson-spot-scrub](https://github.com/UNsync3D/ha-dyson-spot-scrub)
- [libdyson-wg/appapi](https://github.com/libdyson-wg/appapi)
