# Dyson Spot+Scrub AI pour Windows

> Ce README, comme l'essentiel du code de ce dépôt, a été rédigé par Claude (Claude Code, Anthropic),
> sur la direction d'Armand Delessert, qui a fourni les captures et vérifié chaque étape sur son robot.

Application Windows non officielle pour contrôler le robot aspirateur **Dyson Spot+Scrub AI** ([en](https://www.dyson.com/vacuum-cleaners/robot/spot-scrub-ai), [fr-CH](https://www.dyson.ch/fr_ch/aspirateurs/robot/spot-scrub-ai)) (nom interne RB05).

Le robot n'expose aucun service sur le réseau local : il n'est joignable que via le cloud Dyson
(MQTT sur WebSocket vers AWS IoT). Ce dépôt reproduit donc le protocole de [l'application Android MyDyson](https://play.google.com/store/apps/details?id=com.dyson.mobile.android).

## État du projet

Étapes 1 à 3 : **ligne de commande, bibliothèque et application Windows fonctionnent.**

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
| Tests unitaires | 44 tests, exécutés en CI |
| Application Windows (WPF) : tableau de bord, carte, historique, réglages | fonctionne |

Vérifié du 19 au 22 septembre 2026 sur un RB05 en ligne, firmware `RB05PR.01.000.0436`, y compris
sur des nettoyages réels de plusieurs heures.

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

## Application Windows

`src/MyDyson.App` est une application WPF. Au premier lancement elle demande le compte MyDyson et
le code reçu par e-mail, puis mémorise la session chiffrée. Ensuite :

- **Thème** : suit le mode clair ou sombre de Windows, y compris en cours d'exécution.
- **État** : état du robot en clair, batterie, fautes réelles en rouge, retour à la station. Le
  bouton de pause devient « Reprendre » une fois le nettoyage effectivement en pause. Pendant un
  nettoyage, signale le retour à la station pour laver le rouleau (`back_to_wash`, que l'application
  officielle n'affiche pas) ; pendant le séchage, le temps restant (`work_time`).
- **Nettoyage** : choix de la carte, avec un bouton pour en faire la carte active du compte (comme
  le sélecteur de carte de l'application mobile), pièces triées par nom (l'ordre renvoyé par le
  cloud n'est ni alphabétique ni par identifiant), cochées ici ou cliquées sur la carte, ordre de
  passage affiché.
  Une pièce cochée déplie ses réglages : type de nettoyage, mode de l'aspirateur (masqué si le type
  est « Laver » seul), et si le type inclut la serpillière, niveau d'hydratation et nombre de
  passages. Une pièce non cochée reste repliée avec un résumé d'une ligne, et peut être dépliée à la
  main pour consultation sans être sélectionnée. Le tout est enregistré côté cloud pour que
  l'application mobile le voie aussi.
- **Station** : « Vider le collecteur » et « Laver et sécher », qui devient l'arrêt de l'action en cours.
- **Consommables** : durée de vie restante, à remplacer à zéro, comme dans l'application.
- **Notifications** : une notification Windows à la fin d'un nettoyage, ou si une pièce sélectionnée
  n'a pas pu être atteinte. Cliquer dessus ramène la fenêtre au premier plan sur l'historique.
- **Compte** : bouton « Se déconnecter » dans l'en-tête, avec confirmation ; ramène à l'écran de
  connexion sans redémarrer l'application.
- **Carte** : grille d'occupation colorée par pièce, meubles, station, position du robot, tracé en
  temps réel du nettoyage en cours, construit au fil de l'eau depuis le flux `cur_path` du robot, en
  gris là où il ne fait que se déplacer et dans la couleur du type de nettoyage de la pièce là où il
  travaille réellement. Icônes pour les obstacles et les taches détectés. Clic ou tape pour
  sélectionner une pièce, ou pour effacer la sélection en dehors d'une pièce ; double-clic ou
  double-tape en dehors d'une pièce pour réinitialiser le zoom. Molette ou pincement à deux doigts
  pour zoomer, glisser (souris ou un doigt) pour déplacer la vue.
  Export en PNG. Les pièces d'un type reconnu (cuisine, chambre, salon…) portent le même nom que
  dans l'application mobile, même quand le nom enregistré sur le compte diffère.
- **Historique** : date, durée, fin, carte, pièces réellement nettoyées, surface, batterie et
  fautes pour chaque nettoyage. Les pièces se remplissent en arrière-plan une par une après le
  chargement de la liste, chacune nécessitant le détail complet de son nettoyage (l'API ne dit pas,
  autrement, quelles pièces une tâche a couvertes). Chaque nettoyage a sa propre carte, celle du
  nettoyage choisi, avec le trajet parcouru (même code couleur que la carte en direct) et les
  obstacles détectés. Le résultat pièce par pièce (terminée, injoignable…) s'affiche sous la carte
  pour le nettoyage sélectionné ; il n'existe qu'au niveau du détail d'un nettoyage précis, pas dans
  la liste. Le réglage utilisé par pièce (aspirer, laver…) au lancement d'un nettoyage passé n'est en
  revanche pas récupérable après coup : l'API ne renvoie que la préférence *actuelle* de la carte,
  pas celle du moment.
- **Réglages** : les mêmes libellés que l'application Android, en trois groupes : lavage, station,
  vocaux. Chaque réglage part au robot dans les deux dialectes.
- **Journal** : les événements notables du robot et le résultat des commandes envoyées depuis la
  fenêtre, pas chaque message MQTT. Un bouton lance une capture complète dans un fichier JSON Lines,
  pour repérer des messages non encore identifiés.

L'en-tête rappelle le numéro de série, le firmware et le compte connecté.

La connexion se rétablit seule après une coupure, avec des credentials renouvelés. L'état du robot
est réinterrogé toutes les 30 secondes, en plus de ce que le robot pousse spontanément (d'où
l'heure « mis à jour » de l'en-tête). Les données REST — cartes, pièces, historique — ne sont
chargées qu'au démarrage et sur le bouton « Actualiser » ; après une longue veille de la machine,
c'est ce bouton qui les remet à jour.

Deux options de ligne de commande servent à la vérification sans écran et à la documentation :

```bash
MyDyson.App.exe --export-map carte.png
MyDyson.App.exe --screenshot ecran.png --after 15 --tab 0 --theme light --zones 11,13
```

WPF a été préféré à WinUI 3 parce qu'il se compile et se lance sans outillage supplémentaire sur
une machine ARM64 ; la bibliothèque ne dépend d'aucune interface, une migration reste possible.

## Architecture

- `MyDyson.Core`
  - `DysonCloudClient` : API REST `appapi.cp.dyson.com` : compte, appareils, credentials, cartes, historique.
  - `RobotMqttClient` : MQTT direct sur TLS comme l'application, commandes des deux dialectes,
    corrélation requête-réponse (`RequestStateAsync`, `RequestJdmAsync`). Implémente `IRobotCommands`,
    la surface minimale (requête jdm, publication jdm ou classique) sur laquelle `CleaningSequence`
    rejoue la séquence de démarrage de l'application, testable sans robot.
  - `RobotSession` : connexion longue durée, reconnexion avec credentials renouvelés, expose un `RobotStateTracker`.
  - `RobotState`, `RobotStateTracker`, `JdmProperties`, `RoomPreference` : modèle d'état typé des deux dialectes.
  - `MapModels`, `MapGrid` : cartes, zones, position en direct, historique, grille d'occupation décodée.
  - `AwsSigV4`, `RawMqttProbe` : diagnostics des autres transports.
  - `SessionStore` : bearer token chiffré avec DPAPI dans `%APPDATA%\MyDyson\session.bin`.
- `MyDyson.App` : application WPF. `MapRenderer` dessine la scène pour l'écran et l'export PNG,
  `RobotContext` porte la session. `MainViewModel` connecte le robot et distribue ce qu'il pousse aux
  modèles de vue d'onglet (`StatusViewModel`, `CleaningViewModel`, `HistoryViewModel`,
  `SettingsViewModel`, `JournalViewModel`), qui partagent un `RobotHub` (session, journal,
  envoi de commandes, annulation à la fermeture) et un `MapCatalog` (cartes, géométrie, grille).
- `MyDyson.Cli` : `login`, `devices`, `iot`, `status`, `watch`, `maps`, `map`, `live`, `history`,
  `clean`, `send`, `api`, `probe`, `wstest`.
- `tests/MyDyson.Core.Tests` : casse des requêtes, signature SigV4, nom d'utilisateur MQTT, modèle
  d'état des deux dialectes (dont le tracé `cur_path` et les propriétés jdm de la station),
  préférences de pièces, grille d'occupation, messages exacts de la séquence de démarrage.
- `tests/MyDyson.App.Tests` : géométrie de la scène (bornes, dock sentinelle, pièce sous un point,
  découpage du trajet par action).

L'effort de test porte sur ce qui a été retrouvé par rétro-ingénierie et qu'aucune documentation ne
permettrait de retrouver : formes exactes des messages, correspondances entre dialectes, décodage
de la grille. Le cycle de vie de la session et le routage MQTT sont, eux, couverts par l'usage
plutôt que par des tests, faute de coutures pour les isoler.

Le protocole retrouvé par décompilation et par captures est documenté dans [docs/protocole.md](docs/protocole.md).

### Outillage

- `global.json` épingle le SDK ; `Directory.Build.props` active les analyseurs .NET (niveau
  `latest-recommended`) et traite tout avertissement comme une erreur ; `Directory.Packages.props`
  centralise les versions de paquets.
- `.github/workflows/ci.yml` compile les cinq projets et lance les tests à chaque push.
- Les règles qui exigeraient de rendre une `Window` WPF `IDisposable` (CA1001) sont supprimées
  ponctuellement, avec justification, là où la durée de vie est déjà celle de l'événement `Closed`.

## Prérequis

- Windows 10/11, [.NET SDK 10](https://dotnet.microsoft.com/download)
- Un compte MyDyson avec le robot déjà enregistré dans l'application mobile

## Utilisation

```bash
dotnet build
dotnet run --project src/MyDyson.App
```

Ligne de commande :

```bash
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

## Limites connues

- Pas de nettoyage de toute la maison depuis l'application : seulement par pièces. La commande
  existe dans la bibliothèque (`StartGlobalCleanAsync`) et dans la ligne de commande.
- Pas de cartographie ni de modification de carte depuis l'application : renommer, fusionner ou
  diviser des pièces, poser un mur virtuel, ajuster un meuble. Les messages correspondants sont
  documentés dans [docs/protocole.md](docs/protocole.md) et seuls `service.arrange_room` et
  `START-MAPPING` sont implémentés, sans interface.
- Le débordement de la carte à travers les fenêtres vient du lidar du robot, pas du rendu ; seule
  l'application mobile sait le retoucher.
- Le réglage utilisé par pièce lors d'un nettoyage passé n'est pas récupérable (voir Historique).
- Les types de taches autres que `liquid` n'ont jamais été observés : toutes sont dessinées pareil.

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
