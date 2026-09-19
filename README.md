# Dyson Spot+Scrub AI pour Windows

Application Windows non officielle pour contrôler le robot aspirateur **Dyson Spot+Scrub AI** ([en](https://www.dyson.com/vacuum-cleaners/robot/spot-scrub-ai), [fr-CH](https://www.dyson.ch/fr_ch/aspirateurs/robot/spot-scrub-ai)) (nom interne RB05).

Le robot n'expose aucun service sur le réseau local : il n'est joignable que via le cloud Dyson
(MQTT sur WebSocket vers AWS IoT). Ce dépôt reproduit donc le protocole de [l'application mobile MyDyson](https://play.google.com/store/apps/details?id=com.dyson.mobile.android).

## État du projet

Étapes 1 et 2 (prototype en ligne de commande, fondations en lecture) : **tout fonctionne sauf l'interface graphique, qui n'est pas commencée.**

| Fonction | État |
|---|---|
| Connexion au compte (mot de passe + code à usage unique) | fonctionne |
| Liste des appareils, firmware, préfixe MQTT | fonctionne |
| État de connexion du robot au cloud | fonctionne |
| Credentials AWS IoT | fonctionne |
| Connexion MQTT au broker AWS IoT | fonctionne |
| Abonnement aux topics et réception des messages | fonctionne |
| Publication de commandes vers le robot | fonctionne |
| Modèle d'état typé, corrélation requête-réponse | fonctionne |
| Reconnexion automatique avec credentials renouvelés | fonctionne |
| Cartes, position en direct, historique des nettoyages (REST) | fonctionne |
| Tests unitaires | 20 tests |
| Interface graphique | pas commencée |

Vérifié le 19 septembre 2026 sur un RB05 en ligne, firmware `RB05PR.01.000.0436`.

### Le transport compte autant que les credentials

Ce point a coûté une journée et n'est documenté nulle part ailleurs. Le jeton du custom authorizer
que renvoie `POST /v2/authorize/iot-credentials` donne des droits différents selon la façon dont il
est présenté au broker.

| Transport | Où passe le jeton | Droits accordés |
|---|---|---|
| MQTT sur WebSocket | chaîne de requête de l'URL | connexion et abonnement, **publication refusée** |
| WebSocket présigné SigV4 (`iot-role-credentials`) | signature de l'URL | connexion seule |
| **MQTT direct sur TLS, port 443, ALPN `mqtt`** | **nom d'utilisateur MQTT** | **tout, y compris la publication** |

Le troisième mode est celui de l'application officielle, retrouvé par décompilation (classe `y50.e`,
qui utilise `AwsIotMqttConnectionBuilder` du SDK AWS IoT). Le nom d'utilisateur MQTT a cette forme :

```
?x-amz-customauthorizer-name=NOM&x-amz-customauthorizer-signature=SIGNATURE_ENCODEE&token=VALEUR
```

Pas de mot de passe, identifiant client fourni par Dyson, keep-alive de 300 s, session propre.
Le Lambda authorizer de Dyson reçoit le jeton par un canal différent selon le transport, et ne
renvoie la politique complète que par le nom d'utilisateur MQTT. Les projets communautaires qui
passent par WebSocket sont donc limités à la lecture sans le savoir.

Détail pratique : la vérification de révocation du certificat AWS doit être désactivée, le serveur
de révocation n'étant pas joignable depuis certains réseaux. Le certificat lui-même est validé.

Les deux autres modes restent disponibles dans la ligne de commande (`--websocket`, `--sigv4`) à
titre de comparaison et de diagnostic.

## Architecture

- `MyDyson.Core`
  - `DysonCloudClient` : API REST `appapi.cp.dyson.com` : compte, appareils, credentials, cartes, historique.
  - `RobotMqttClient` : MQTT direct sur TLS comme l'application, commandes des deux dialectes,
    corrélation requête-réponse (`RequestStateAsync`, `RequestJdmAsync`).
  - `RobotSession` : connexion longue durée, reconnexion avec credentials renouvelés, expose un `RobotStateTracker`.
  - `RobotState`, `RobotStateTracker`, `JdmProperties`, `RoomPreference` : modèle d'état typé des deux dialectes.
  - `MapModels` : cartes, zones, position en direct, historique.
  - `AwsSigV4`, `RawMqttProbe` : diagnostics des autres transports.
  - `SessionStore` : bearer token chiffré avec DPAPI dans `%APPDATA%\MyDyson\session.bin`.
- `MyDyson.Cli` : `login`, `devices`, `iot`, `status`, `watch`, `maps`, `map`, `live`, `history`,
  `clean`, `send`, `api`, `probe`, `wstest`.
- `tests/MyDyson.Core.Tests` : casse des requêtes, signature SigV4, nom d'utilisateur MQTT, modèle
  d'état, préférences de pièces.

Le protocole retrouvé par décompilation et par captures est documenté dans [docs/protocole.md](docs/protocole.md).

## Prérequis

- Windows 10/11, [.NET SDK 10](https://dotnet.microsoft.com/download)
- Un compte MyDyson avec le robot déjà enregistré dans l'application mobile

## Utilisation

```bash
dotnet build
dotnet run --project src/MyDyson.Cli -- login --country CH --culture fr-CH
dotnet run --project src/MyDyson.Cli -- devices
dotnet run --project src/MyDyson.Cli -- iot    --serial XXX-XX-XXXXXXXX
dotnet run --project src/MyDyson.Cli -- status --serial XXX-XX-XXXXXXXX
dotnet run --project src/MyDyson.Cli -- watch  --serial XXX-XX-XXXXXXXX --log capture.jsonl
dotnet run --project src/MyDyson.Cli -- send   --serial XXX-XX-XXXXXXXX maps
dotnet run --project src/MyDyson.Cli -- send   --serial XXX-XX-XXXXXXXX zone --map ID --zones 11
dotnet run --project src/MyDyson.Cli -- history --serial XXX-XX-XXXXXXXX
dotnet test
```

`watch` enregistre aussi les commandes publiées par l'application mobile, ce qui permet de
constituer des captures de référence en pilotant le robot depuis le téléphone.

## Protocole

| Étape | Requête |
|---|---|
| Provisioning (obligatoire avant tout) | `GET /v1/provisioningservice/application/Android/version` |
| Statut du compte | `POST /v3/userregistration/email/userstatus?country=CH` |
| Début de login, envoi du code | `POST /v3/userregistration/email/auth?country=CH&culture=fr-CH` |
| Fin de login, bearer token | `POST /v3/userregistration/email/verify?country=CH&culture=fr-CH` |
| Appareils du compte | `GET /v3/manifest` |
| Robot en ligne ou non | `GET /v1/messageprocessor/devices/{serial}/connectionstatus` |
| Jeton custom authorizer | `POST /v2/authorize/iot-credentials` `{"Serial": "..."}` |
| Credentials IAM temporaires | `POST /v1/authorize/iot-role-credentials` `{"Serial": "..."}` |

Topics MQTT : `RB05/{serial}/status`, `RB05/{serial}/status/jdm`, `RB05/{serial}/command`,
`RB05/{serial}/command/jdm`. Le préfixe vaut les quatre premiers caractères de la version du firmware.

Deux dialectes coexistent sur ces topics : le format Dyson classique
(`{"msg":"START","mode-reason":"RAPP"}`) et une couche JSON-RPC nommée jdm
(`{"method":"service.set_room_clean","params":{...}}`).

**Piège** : l'API Dyson est sensible à la casse des propriétés des corps de requête. `{"Serial": ...}`
est accepté, `{"serial": ...}` renvoie HTTP 400. Le sérialiseur JSON ne doit donc appliquer aucune
politique de renommage, d'où `PropertyNamingPolicy = null` dans `DysonCloudClient`.

## Avertissements

- API non officielle, susceptible de changer sans préavis. Usage personnel.
- Les fichiers APK dans `APK/` servent uniquement à l'analyse et ne sont pas versionnés.
- Les credentials AWS IoT sont de courte durée (environ 20 minutes) et apparaissent en clair dans les
  réponses de l'API. Évitez de les journaliser.

## Références

- [thoukydides/matterbridge-dyson-robot](https://github.com/thoukydides/matterbridge-dyson-robot) ([issue #46](https://github.com/thoukydides/matterbridge-dyson-robot/issues/46) : capture complète RB05)
- [UNsync3D/ha-dyson-spot-scrub](https://github.com/UNsync3D/ha-dyson-spot-scrub)
- [libdyson-wg/appapi](https://github.com/libdyson-wg/appapi)
- [Diagnostic des connexions AWS IoT](https://docs.aws.amazon.com/iot/latest/developerguide/diagnosing-connectivity-issues.html)
