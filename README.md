# Dyson Spot+Scrub AI pour Windows

Application Windows non officielle pour contrôler le robot aspirateur **Dyson Spot+Scrub AI** ([en](https://www.dyson.com/vacuum-cleaners/robot/spot-scrub-ai), [fr-CH](https://www.dyson.ch/fr_ch/aspirateurs/robot/spot-scrub-ai)) (nom interne RB05).

Le robot n'expose aucun service sur le réseau local : il n'est joignable que via le cloud Dyson
(MQTT sur WebSocket vers AWS IoT). Ce dépôt reproduit donc le protocole de [l'application mobile MyDyson](https://play.google.com/store/apps/details?id=com.dyson.mobile.android).

## État du projet

Étape 1 (prototype en ligne de commande) : **la lecture fonctionne, l'envoi de commandes est bloqué.**

| Fonction | État |
|---|---|
| Connexion au compte (mot de passe + code à usage unique) | fonctionne |
| Liste des appareils, firmware, préfixe MQTT | fonctionne |
| État de connexion du robot au cloud | fonctionne |
| Credentials AWS IoT (deux variantes) | fonctionne |
| Connexion MQTT sur WebSocket | fonctionne |
| Abonnement aux topics d'état et réception des messages | fonctionne |
| Publication d'une commande vers le robot | **refusée par le broker** |

Vérifié le 19 septembre 2026 sur un RB05 en ligne, firmware `RB05PR.01.000.0436`.

### Le blocage sur la publication

Le broker AWS IoT ferme la connexion environ 100 ms après notre `PUBLISH`, ce qui est la façon
dont AWS signale un refus de politique. Constats :

- Le refus touche **tous les topics**, y compris un topic arbitraire comme `test/hello`. Ce n'est donc
  pas un problème de nom de topic mais une absence du droit `iot:Publish`.
- Le refus est reproduit **hors de MQTTnet**, avec un `ClientWebSocket` et un paquet MQTT CONNECT puis
  PUBLISH construits à la main (`RawMqttProbe`). Ce n'est donc pas un défaut de la bibliothèque.
- Les deux jeux de credentials que l'API Dyson délivre sont insuffisants :
  - `POST /v2/authorize/iot-credentials` (jeton de custom authorizer) autorise `CONNECT` et
    `SUBSCRIBE` sur n'importe quel filtre, mais pas `PUBLISH`.
  - `POST /v1/authorize/iot-role-credentials` (credentials IAM temporaires, signature SigV4)
    autorise `CONNECT` seul : même l'abonnement à `RB05/{serial}/status` est refusé.
- Le custom authorizer impose son propre identifiant client : la connexion échoue avec tout autre
  identifiant, et réussit avec celui renvoyé dans la réponse.
- Aucun endpoint REST de commande n'existe dans l'APK. Les seuls endpoints liés au robot concernent
  les cartes et les zones. Les commandes passent donc bien par MQTT.
- Les champs supplémentaires (`ClientId`, `Permissions`) sont ignorés par l'endpoint de credentials.
  Il n'existe pas de `/v3/authorize/iot-credentials` ni de `/v2/authorize/iot-role-credentials`.

Pistes non encore explorées, par ordre de vraisemblance :

1. **Enregistrement du client applicatif.** L'APK contient `/v1/device/register`,
   `/v1/device/client-metadata` et `/v1/device/registerDeviceCapabilities`. Le Lambda authorizer de
   Dyson accorde peut-être `iot:Publish` aux seules instances d'application enregistrées. Ces appels
   créent de l'état sur le compte, ils n'ont volontairement pas été tentés à l'aveugle.
2. **Capture du trafic de l'application officielle** pendant l'envoi d'une commande, pour voir quels
   credentials et quel identifiant client elle utilise. Nécessite de contourner le certificate
   pinning de l'APK (5 empreintes SHA-256 y sont épinglées).
3. **Durcissement récent côté Dyson.** L'API a été placée derrière Cloudflare mTLS vers août 2026.
   Les projets communautaires ne signalent pas de régression sur la publication à ce jour, mais leurs
   auteurs testent sur d'autres modèles et d'autres comptes.

## Architecture

- `MyDyson.Core`
  - `DysonCloudClient` : API REST `appapi.cp.dyson.com`.
  - `RobotMqttClient` : MQTT sur WebSocket, abonnements vérifiés, commandes classiques et couche JDM.
  - `AwsSigV4` : signature des URL WebSocket AWS IoT.
  - `RawMqttProbe` : diagnostic bas niveau, sépare une erreur de signature d'un refus de politique.
  - `SessionStore` : bearer token chiffré avec DPAPI dans `%APPDATA%\MyDyson\session.bin`.
- `MyDyson.Cli` : `login`, `devices`, `iot`, `status`, `watch`, `send`, `api`, `probe`, `wstest`.

## Prérequis

- Windows 10/11, [.NET SDK 10](https://dotnet.microsoft.com/download)
- Un compte MyDyson avec le robot déjà enregistré dans l'application mobile

## Utilisation

```bash
dotnet build
dotnet run --project src/MyDyson.Cli -- login --country CH --culture fr-CH
dotnet run --project src/MyDyson.Cli -- devices
dotnet run --project src/MyDyson.Cli -- iot    --serial XXX-XX-XXXXXXXX
dotnet run --project src/MyDyson.Cli -- watch  --serial XXX-XX-XXXXXXXX --poll 0 --log capture.jsonl
dotnet run --project src/MyDyson.Cli -- probe  --serial XXX-XX-XXXXXXXX
```

`watch --poll 0` écoute sans rien publier, ce qui évite la fermeture de connexion. Lancez un cycle
depuis l'application mobile pendant l'écoute pour constituer la capture de référence.

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

Le détail des messages, le modèle d'état et la correspondance entre les deux dialectes sont dans
[docs/protocole.md](docs/protocole.md), établi à partir de captures réelles.

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
