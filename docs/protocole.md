# Protocole observé du Dyson Spot+Scrub AI (RB05)

Ce document décrit comment l'application Android MyDyson dialogue avec le robot Dyson Spot+Scrub AI (nom interne RB05), tel que DySS Cockpit le reproduit. Il est établi à partir de captures MQTT réelles prises du 19 septembre au 1er octobre 2026 et de la décompilation de l'APK MyDyson 6.4.26360, sur un robot au firmware `RB05PR.01.000.0436`, région `eu-west-1`. Dans les exemples, le numéro de série est remplacé par `SERIAL` et les identifiants de carte par `1000000001`, `1000000002`…

## Sommaire

- [Vue d'ensemble](#vue-densemble)
- [Connexion](#connexion)
- [Dialecte classique](#dialecte-classique)
- [Couche jdm](#couche-jdm)
- [Nettoyage](#nettoyage)
- [Station](#station)
- [Réglages](#réglages)
- [Cartes et pièces](#cartes-et-pièces)
- [Horaires](#horaires)
- [Fuseau horaire](#fuseau-horaire)
- [API REST de l'application mobile](#api-rest-de-lapplication-mobile)

## Vue d'ensemble

Le robot n'expose aucun service sur le réseau local : tout passe par le cloud Dyson.

- **L'API REST** `appapi.cp.dyson.com` sert au compte, aux appareils, aux cartes, à l'historique et aux horaires.
- **Le broker MQTT** AWS IoT relaie l'état du robot en direct et les commandes, sur quatre topics : `RB05/{serial}/status`, `RB05/{serial}/status/jdm`, `RB05/{serial}/command` et `RB05/{serial}/command/jdm`. Le préfixe `RB05` vaut les quatre premiers caractères de la version du firmware.

Deux dialectes circulent sur ces topics et décrivent le même état :

- **le dialecte Dyson classique**, sur `status` et `command`, dont les messages sont identifiés par `msg` : `{"msg": "START", "mode-reason": "RAPP", …}` ;
- **jdm**, une couche JSON-RPC en snake_case, sur `status/jdm` et `command/jdm`, dont les messages sont identifiés par `method` : `{"method": "service.set_room_clean", "params": {…}}`. C'est la couche de la plateforme robot sous-jacente.

L'application officielle lit les deux, et écrit souvent la même commande dans les deux dialectes.

Le code du téléphone pour ce robot ne contient d'ailleurs **aucun** nom de méthode jdm : pour les cartes, les pièces et les horaires, il appelle l'API REST, et c'est le cloud qui relaie au robot les `service.*` que montrent les captures (leurs `msgId` sont ceux du cloud). DySS Cockpit parle jdm directement, ce qui marche aussi. Les adresses REST correspondantes sont listées dans la dernière section.

## Connexion

### Compte et appareils

| Étape | Requête |
|---|---|
| Provisioning (obligatoire avant tout) | `GET /v1/provisioningservice/application/Android/version` |
| Statut du compte | `POST /v3/userregistration/email/userstatus?country=CH` |
| Début de connexion, envoi du code par e-mail | `POST /v3/userregistration/email/auth?country=CH&culture=fr-CH` |
| Fin de connexion, bearer token | `POST /v3/userregistration/email/verify?country=CH&culture=fr-CH` |
| Appareils du compte, firmware, type de produit | `GET /v3/manifest` |
| Robot en ligne ou non | `GET /v1/messageprocessor/devices/{serial}/connectionstatus` |
| Jeton du custom authorizer, endpoint et identifiant client MQTT | `POST /v2/authorize/iot-credentials` `{"Serial": "..."}` |
| Credentials IAM temporaires | `POST /v1/authorize/iot-role-credentials` `{"Serial": "..."}` |

L'API est sensible à la casse des propriétés des corps de requête : `{"Serial": ...}` est accepté, `{"serial": ...}` renvoie HTTP 400. Le type de produit du RB05 dans le manifeste est 804.

### Connexion au broker

Retrouvé par décompilation (classe `y50.e`, construite sur `AwsIotMqttConnectionBuilder` du SDK AWS IoT pour Java) :

- Transport : **MQTT directement sur TLS**, port 443, protocole ALPN `mqtt`. Pas de WebSocket.
- Endpoint et identifiant client : ceux de la réponse de `POST /v2/authorize/iot-credentials`. Si l'identifiant est vide, un UUID aléatoire.
- Nom d'utilisateur MQTT, sans mot de passe : `?x-amz-customauthorizer-name=NOM&x-amz-customauthorizer-signature=SIGNATURE&token=VALEUR`, la signature étant encodée en URL si elle ne l'est pas déjà.
- Keep-alive de 300 s, session propre, délai de connexion de 30 s, reconnexion entre 12 et 24 heures.
- La branche SigV4 du SDK n'est jamais prise : elle ne s'active qu'en WebSocket, que l'application n'utilise pas. Les credentials IAM de `iot-role-credentials` ne servent donc pas au MQTT.

Le transport compte autant que le jeton. Présenté dans la chaîne de requête d'une URL WebSocket, le même jeton n'obtient qu'une politique de lecture (connexion et abonnement, publication refusée) ; un WebSocket présigné SigV4 n'obtient que la connexion. Seul le nom d'utilisateur MQTT sur TLS direct donne tous les droits. La vérification de révocation du certificat AWS doit être désactivée, son serveur n'étant pas joignable depuis certains réseaux ; le certificat lui-même reste validé.

Les endpoints `/v1/device/register`, `/v1/device/client-metadata` et `/v1/device/registerDeviceCapabilities`, un temps soupçonnés de conditionner le droit de publier, appartiennent aux SDK PayPal et Salesforce embarqués dans l'application. Ils n'ont aucun rapport avec le robot.

### Interrogation périodique

L'application publie `REQUEST-CURRENT-STATE` toutes les trente secondes, accompagné d'un `prop.get` qui énumère les quarante-deux propriétés voulues dans `params.property`.

Curiosité : certains messages de l'application transportent des clés parasites à une lettre, par exemple `{"f": "RB05", "g": "SERIAL", "h": "RB05/SERIAL/command"}`. Ce sont des champs internes de routage dont les noms ont été raccourcis par l'obfuscateur R8, puis sérialisés par erreur. Le robot les ignore.

### Capturer les messages

Les droits d'abonnement couvrent `+/{serial}/#`, topics de commande compris. Une écoute lancée pendant que l'application officielle pilote le robot enregistre donc aussi les requêtes qu'elle publie, et pas seulement les réponses du robot :

```bash
dotnet run --project src/DyssCockpit.Cli -- watch --serial XXX-XX-XXXXXXXX --poll 0 --log capture.jsonl
```

`--poll 0` désactive l'interrogation périodique : l'écoute ne publie alors rien.

## Dialecte classique

### CURRENT-STATE

Émis en réponse à `REQUEST-CURRENT-STATE`, et spontanément pendant un nettoyage. Vingt-neuf champs observés. Exemple au repos sur la station :

```json
{
  "msg": "CURRENT-STATE",
  "time": "2026-09-19T11:53:11.9534615Z",
  "batteryChargeLevel": 100,
  "state": "INACTIVE_CHARGING",
  "currentCleaningStrategy": "auto",
  "defaultCleaningStrategy": "auto",
  "currentCleaningMode": "global",
  "defaultCleaningMode": "zoneConfigured",
  "cleanDuration": 480,
  "persistentMapId": "1000000002",
  "activeFaults": [{ "faultCode": "2105", "nextActionRequired": "LOG_ONLY" }],
  "outOfBoxState": "OUT_OF_BOX_COMPLETE",
  "hotWaterMop": true,
  "backWashType": "TIME",
  "hotWaterSwitch": true,
  "detergent": true,
  "volume": 30,
  "fullCleanAction": "NONE",
  "dockState": "IDLE",
  "airDryFrequency": 3,
  "backWashTime": 15,
  "backWashFrequency": 20,
  "alarm": true,
  "voiceLanguage": "fr-FR",
  "consumables": [
    { "type": "brushBar", "usage": 18 },
    { "type": "mopRoller", "usage": 18 },
    { "type": "sideBrushes", "usage": 18 },
    { "type": "robotFilter", "usage": 18 },
    { "type": "dockFilter", "usage": 18 },
    { "type": "ioniserCartridge", "usage": 18 },
    { "type": "cleaningSolution", "needsRefill": false }
  ],
  "collectDustOnSelfClean": true,
  "childLock": false,
  "doNotDisturbMode": { "isOn": false, "startTime": "22:00", "endTime": "8:00" }
}
```

Pendant un déplacement, le robot envoie aussi des messages de position seule, avec `globalPosition`, un tableau de points `{id, x, y, angle, update}` en mètres, relatifs à la station.

| Champ | Valeurs observées |
|---|---|
| `state` | `INACTIVE_CHARGING`, `INACTIVE_DISCHARGING`, `FULL_CLEAN_DISCOVERING`, `FULL_CLEAN_RUNNING`, `FULL_CLEAN_PAUSED`, `FULL_CLEAN_FINISHED`, `ABORTED` (sans préfixe, juste après un abandon), `MAPPING_RUNNING`, `MAPPING_FINISHED` |
| `dockState` | `IDLE`, `WASHING_MOP`, `COLLECTING_DUST`, `DRYING_MOP` |
| `fullCleanAction` | `NONE`, `VACUUMING`, `MOPPING`, `VACUUMING_AND_MOPPING` |
| `currentCleaningMode` | `global` au repos, `zoneConfigured` pendant un nettoyage de pièces, `spotZoneConfigured` pendant un nettoyage de zone |

L'APK connaît d'autres états, jamais observés : `FULL_CLEAN_ABORTED`, `FULL_CLEAN_NEEDS_CHARGE`, etc. Les codes de faute de `activeFaults` sont décrits dans la section [Nettoyage](#codes-de-faute).

### Autres messages

| Message | Rôle | Voir |
|---|---|---|
| `START`, `PAUSE`, `ABORT` | démarrer, mettre en pause, abandonner un nettoyage | [Nettoyage](#nettoyage) |
| `START-DOCK-ACTION`, `ABORT-DOCK-ACTION` | lancer ou arrêter une action de la station | [Station](#station) |
| `STATE-SET` | modifier un réglage | [Réglages](#réglages) |
| `SET-VOICE-LANGUAGE`, `REQUEST-VOICE-DOWNLOAD-STATUS`, `VOICE-DOWNLOAD-STATUS` | changer la voix | [Voix](#voix) |
| `START-MAPPING` | lancer une cartographie | [Création d'une carte](#création-dune-carte) |
| `MAP-UPLOAD-STATUS` | fin d'un envoi de carte au cloud : `{"status": "COMPLETE", "persistentMapId": "1000000003"}` après une modification de carte, `{"status": "COMPLETE", "cleanId": "…"}` après un nettoyage | [Cartes et pièces](#commandes-jdm) |
| `CLEAN-MAP-IMAGE-STATUS` | image d'un nettoyage envoyée : `{"status": "UPLOADED", "cleanId": "…"}` | |

Présents dans l'APK mais jamais observés : `STATE-CHANGE`, `CURRENT-FAULTS`, `FAULTS-CHANGE`, `START-DOCK-SELF-CHECK`, `SKIP-CURRENT-ZONE`, `RESUME` et `STOP`. L'application n'utilise jamais `STOP` : elle interrompt un nettoyage par `PAUSE` puis `ABORT`.

## Couche jdm

### prop.get et prop.post

`prop.get` renvoie l'état complet en snake_case, `prop.post` pousse les champs modifiés. Pendant un nettoyage, `prop.post` domine le trafic, essentiellement pour `cur_path` (le tracé parcouru) et `cleaning_area`.

| jdm (snake_case) | Classique (camelCase) | Remarque |
|---|---|---|
| `quantity` | `batteryChargeLevel` | pourcentage |
| `status` | `state` | voir ci-dessous |
| `fault` | `activeFaults[].faultCode` | valeur numérique |
| `current_map_id` | `persistentMapId` | entier contre chaîne |
| `cleaning_time` | `cleanDuration` | minutes contre secondes, 8 contre 480 |
| `cleaning_area` | aucun | surface nettoyée de la tâche en cours, en dm² |
| `sweep_type` | aucun | 0 aspiration seule ; 1 et 7 vus pendant un nettoyage avec lavage ; 3 et 6 vus aussi, non interprétés |
| `work_mode` | `currentCleaningMode` | 0 au repos, 36 pendant un nettoyage, 38 au retour ; d'autres valeurs vues (5, 30, 32, 37, 45, 47), non interprétées |
| `station_act` | `dockState` | 0 `IDLE`, 1 `WASHING_MOP`, 2 `DRYING_MOP`, 5 vidage du collecteur (avec `dust_action: 1`) |
| `charge_state` | aucun | 1 quand le robot est sur ses contacts de charge, y compris pendant un lavage en cours de nettoyage |
| `back_to_wash` | aucun | 1 dès que le robot quitte le nettoyage pour aller laver son rouleau, 0 une fois à quai ; le dialecte classique ne signale que le lavage lui-même |
| `work_time` | aucun | `{type, total, surplus}` en secondes : compte à rebours de l'action de la station, poussé toutes les minutes ; vu seulement pour le séchage (`type: 3`, `total: 10800` pour le réglage de 3 h), `surplus` restant à 0 une fois terminé |
| `clean_finish`, `clean_id` | aucun | horodatage de fin et identifiant du dernier nettoyage |
| `order_total` | aucun | `{total, enable}` : horaires de la carte active, voir [Horaires](#horaires) |
| `oob_state` | `outOfBoxState` | 1 correspond à `OUT_OF_BOX_COMPLETE` |
| `quiet_begin_time`, `quiet_end_time`, `quiet_is_open` | `doNotDisturbMode` | minutes depuis minuit, 1320 pour 22:00 |
| `voice_type` | `voiceLanguage` | 5 correspond à `fr-FR` |
| `main_brush`, `mop_life`, `side_brush`, `hypa`, `dock_hypa`, `ioniser` | `consumables[].usage` | |
| `back_wash_type`, `back_wash_time`, `wash_back_frequency` | `backWashType`, `backWashTime`, `backWashFrequency` | |
| `airdry_frequency`, `alarm`, `child_lock`, `detergent`, `volume` | mêmes champs | booléens en 0 ou 1 |
| `hot_water_mop`, `hot_water_switch` | mêmes champs | |
| `dust_auto_state` | `collectDustOnSelfClean` | |

`status` suit l'activité plus finement que `state` : 4 en charge sur la station, 9 pendant un lavage à quai (avec `station_act: 1`). Au cours d'un nettoyage avec lavage, la suite observée est 5 puis 6 au départ, 9 à chaque lavage, 7 après le lavage de fin, 3 au retour et 4 une fois en charge. Les valeurs 1, 2, 10 et 12 ont aussi été vues.

Champs présents mais toujours nuls au repos : `auto_water_complete_flag`, `auto_water_self_check`, `clean_wash_attachment`, `empty_bin_time`, `empty_bin_type`, `global_clean_status`, `mop_pad_life`, `robot_auto_updown_type`, `store_demo_mode`, `taskBeginTs`.

`work_time` et `back_to_wash` ne figurent pas dans la liste que l'application demande par `prop.get` : le robot les pousse de lui-même. Il y répond pourtant si on les demande (vérifié le 22 septembre), ce que fait DySS Cockpit au démarrage pour connaître l'état du séchage sans attendre la poussée suivante.

### Codes de retour

Pour les méthodes `service.*`, c'est en général `data.result` qui porte le verdict : 0 pour un succès, 1 pour un refus. Certaines méthodes de carte répondent autrement, voir [Commandes jdm](#commandes-jdm). Le champ `code` de l'enveloppe ne suit pas cette convention : il vaut 1 sur les réponses à `prop.get`, alors que les données sont valides.

### cur_path : le tracé en temps réel

Poussé par lots d'un à une quinzaine de points pendant tout le nettoyage. Chaque lot est un tableau à plat :

```json
{ "cur_path": [103, -0.771649,3.225564,-1.557448,1, -0.762950,3.119714,-1.595241,1, 1789915756] }
```

Le premier élément est un identifiant de lot, suivi de groupes de quatre valeurs `x, y, angle, update` (un par point) et d'un horodatage Unix en dernière position. `update` vaut 0 quand le robot se contente de se déplacer et 1 quand il travaille réellement à cet endroit. C'est un flux, pas un état : il faut l'accumuler lot par lot, et repartir de zéro quand l'identifiant de lot redescend (nouvelle tâche) ou sur `event.startClean.post`.

Côté REST, `GET /v1/app/{serial}/live-maps/cleaning` et `GET /v2/{serial}/clean-maps-data/{cleanId}` renvoient le même tracé complet sous forme d'objets `{x, y, update}`. Ils renvoient aussi `obstacles` (points `{x, y}` : un câble, un objet détecté) et `dirt`, les taches détectées :

```json
{ "x": -0.53, "y": -5.25, "type": "liquid", "isUvScanOn": false }
```

Une seule valeur de `type` observée (`liquid`) ; l'application Android affichant plusieurs icônes de tache, d'autres valeurs existent vraisemblablement. `hazardZones`, `groutLines` et `swingDoors` sont restés vides dans toutes les captures, leur forme reste inconnue.

### Méthodes et événements observés

| Domaine | Méthodes |
|---|---|
| État | `prop.get`, `prop.set`, `prop.post` |
| Nettoyage | `service.set_preference`, `service.get_preference`, `service.set_room_clean`, `service.set_areas_start`, `service.start_recharge` |
| Station | `service.start_station_act` |
| Cartes et pièces | `service.get_map_list`, `service.set_cur_map`, `service.rename_map`, `service.del_map`, `service.rename_room`, `service.split_room`, `service.arrange_room`, `service.delete_room` (relayée par le cloud, refusée), `service.set_virtual_wall`, `service.adjust_furniture`, `service.start_explore` |
| Horaires | `service.get_order`, `service.add_order`, `service.del_order` |
| Voix et fuseau | `service.download_voice_type`, `service.get_voice_download`, `service.set_robot_time_zone` |

Événements poussés par le robot : `event.startClean.post`, `event.clean_finish.post`, `event.clean_record.post`, `event.locate_fail.post`, `event.Unable_all_area_recharge.post`, `event.map_change.post`, `event.startBuildMap.post`, `event.BuildMapFinish.post`, `event.shortcut_instruction_task_change.post`.

## Nettoyage

### Démarrer un nettoyage de pièces

La séquence exacte du bouton de démarrage, en quatre messages sur deux topics :

```
-> command/jdm   service.set_preference   { map_id, prefer_type: 1, room_preference, uv_switch }
<- status/jdm    code 0, data.result 0
-> command       START
-> command/jdm   service.set_cur_map      { map_id }
-> command/jdm   service.set_room_clean   { ctrl_value: 1, clean_type: 0, room_ids: [11] }
```

La charge utile du `START` :

```json
{
  "msg": "START",
  "mode-reason": "RAPP",
  "cleaningMode": "zoneConfigured",
  "fullCleanType": "immediate",
  "cleaningProgramme": {
    "persistentMapId": "1000000002",
    "zonesDefinitionLastUpdatedDate": "",
    "unorderedZones": ["11"]
  },
  "time": "2026-09-19T12:29:35Z"
}
```

Les champs `fullCleanType` et `zonesDefinitionLastUpdatedDate` ne figurent dans aucune implémentation communautaire connue ; l'application les envoie systématiquement.

`service.set_cur_map` fait partie de la séquence même quand la carte visée est déjà active. Rejouer la séquence sur une autre carte revient donc à changer de carte active et à démarrer en une seule action, ce que l'application mobile ne propose pas. Le robot a été entendu annoncer deux fois « Lancer un nettoyage personnalisé » lors d'un tel démarrage, sans que la capture permette d'en isoler la cause.

### Réglages par pièce (`room_preference`)

`service.get_preference` renvoie les pièces sous forme de tableaux positionnels de douze éléments ; le `service.set_preference` de l'application n'en envoie que onze. Un lecteur doit tolérer les deux longueurs.

```json
{ "method": "service.get_preference", "code": 0, "data": {
  "room": [
    [11, "Salle de bain", 2, 0, 0, 0, 0, 0, 1, 0, 1, 0],
    [10, "{\"type\":\"kitchen\",\"name\":\"Cuisine\"}", 3, 0, 0, 0, 0, 0, 0, 0, 2, 0]
  ],
  "material": [],
  "prefer_on": 1
}}
```

Indices établis en croisant les `set_preference` et les `add_order` capturés avec ce qu'affichait l'application (dont deux horaires réglés exprès pour couvrir toutes les valeurs) :

| Indice | Contenu | Valeurs |
|---|---|---|
| 0 | identifiant de la pièce | |
| 1 | nom | chaîne simple, ou objet JSON encodé `{type, name}` pour une pièce typée : un lecteur doit gérer les deux formes |
| 2 | renvoyé par le robot, remis à 0 par l'application | 2 pour une salle de bain, 3 pour une cuisine |
| 3 | type de nettoyage | 0 aspirer, 1 aspirer et laver, 2 laver, 3 aspirer puis laver |
| 4 | puissance d'aspiration | 0 Auto, 1 Boost, 2 Silencieux, 3 Rapide |
| 5 | niveau d'eau | 0 Faible, 1 Moyen, 2 Élevé |
| 6 | passages de lavage | 0 un passage, 1 deux passages |
| 8 | pièce retenue | 0 ou 1 |
| 10 | ordre de passage | 1, 2, 3… au démarrage ; 0, 1, 2… dans un horaire |

Les indices 7, 9 et 11 sont toujours à 0. Une pièce lavée seulement garde sa puissance à 0, une pièce aspirée seulement son eau et ses passages à 0.

`uv_switch` accompagne la liste avec une paire `[pièce, valeur]` par pièce. Il vaut 0 pour toute pièce lavée dans les horaires capturés, 1 sinon, mais pas toujours au démarrage d'un nettoyage (une pièce aspirée seulement est partie avec 0). C'est vraisemblablement le réglage REST `isUvScanOn` de la pièce, que le téléphone coupe quand il lave.

Côté REST, les mêmes réglages s'appellent `settings.cleanType` (`vacuum`, `mop`, `vacuumAndMop`, `vacuumThenMop`), la stratégie (`auto`, `quick`, `quiet`, `boost`), `waterLevel` (`low`, `medium`, `high` ; l'application connaît aussi `veryLow`, jamais proposé) et `mopPasses` (1 ou 2). `PUT /v2/app/{serial}/persistent-map-metadata/{mapId}` avec la liste des pièces les enregistre côté cloud ; le téléphone les recopie aussi dans les indices 3 à 6 du `set_preference` qu'il envoie au démarrage.

### Nettoyer une zone dessinée

L'application permet aussi de nettoyer un rectangle tracé sur la carte plutôt que des pièces entières. Capturé le 30 septembre (aspiration seule, mode silencieux) :

```
-> command       START { cleaningMode: "spotZoneConfigured", fullCleanType: "immediate", mode-reason: "RAPP",
                   cleaningProgramme: { persistentMapId, spotZones: [{ id: <uuid>, points: [4 coins] }],
                     defaultSpotZoneSettings: { cleaningStrategy: "quiet", cleanType: "vacuum",
                                                waterLevel: "low", mopPasses: 1, dryPasses: 1 } } }
-> command/jdm   service.set_cur_map      { map_id }
-> command/jdm   service.set_areas_start  { ctrl_value: 1, zone_points: [[x1, y1 … x4, y4]], mode: 0, wind: 2,
                                            water: 0, clean_count: 0, dry_clean_count: 0, action: 0, uv_switch: 0 }
<- status/jdm    event.startClean.post, puis state FULL_CLEAN_RUNNING
<- status/jdm    service.set_areas_start  { result: 1 }
```

Les coins vont dans l'ordre haut-droite, haut-gauche, bas-gauche, bas-droite (y vers le haut), les mêmes dans les deux messages. `mode`, `wind`, `water` et `clean_count` reprennent les codes des indices 3 à 6 de `room_preference` : type de nettoyage, puissance, eau, passages. Le robot démarre sur le `START` ; `set_areas_start` répond `result: 1` deux secondes plus tard, le nettoyage étant déjà lancé.

### Pause et abandon

```
-> command       {"msg": "PAUSE", "cleaningMode": "zoneConfigured", "mode-reason": "RAPP"}
-> command/jdm   service.set_room_clean  {"ctrl_value": 2, "clean_type": 0, "room_ids": []}
<- status        state FULL_CLEAN_PAUSED, fullCleanAction NONE
```

`ctrl_value` vaut 1 pour démarrer et 2 pour mettre en pause. La reprise n'a pas été observée.

```
-> command       {"msg": "ABORT", "cleaningMode": "zoneConfigured", "state": "FULL_CLEAN_PAUSED", "mode-reason": "RAPP"}
-> command/jdm   service.start_recharge  {}
<- status        state ABORTED avec la faute 2104, puis INACTIVE_DISCHARGING, puis INACTIVE_CHARGING une fois à quai
<- status/jdm    event.clean_record.post avec record_task_status 2
```

Le message `ABORT` transporte l'état courant du robot dans son champ `state` (`FULL_CLEAN_RUNNING` ou `FULL_CLEAN_PAUSED` selon le moment) : un client doit connaître l'état avant d'abandonner.

### Cycle de vie d'un nettoyage

Enchaînement observé sur un cycle complet, aspiration et lavage :

| Étape | `state` | `dockState` | `fullCleanAction` |
|---|---|---|---|
| au repos | `INACTIVE_CHARGING` | `IDLE` | `NONE` |
| lavage avant le départ | `FULL_CLEAN_RUNNING` | `WASHING_MOP` | `NONE` |
| localisation | `FULL_CLEAN_DISCOVERING` | `IDLE` | `VACUUMING_AND_MOPPING` |
| nettoyage | `FULL_CLEAN_RUNNING` | `IDLE` | `VACUUMING_AND_MOPPING` |
| lavage en cours de nettoyage | `FULL_CLEAN_RUNNING` | `WASHING_MOP` | `NONE` |
| fin | `FULL_CLEAN_FINISHED` | `COLLECTING_DUST` puis `WASHING_MOP` | `NONE` |
| séchage | `INACTIVE_CHARGING` | `DRYING_MOP` | `NONE` |

Avant de partir, le robot lave son rouleau à la station (environ deux minutes), vraisemblablement pour le charger en eau propre. En cours de nettoyage, il revient le laver (`back_to_wash` à 1 pendant le trajet), puis repart. En fin de cycle il vide son collecteur, lave le rouleau, puis entame un séchage de plusieurs heures. Aucun signal ne distingue, pendant un lavage, la vidange de l'eau sale du remplissage d'eau propre.

Une fois le dernier lavage terminé, quelques minutes après la fin du nettoyage proprement dit, le robot émet `event.clean_finish.post` puis `event.clean_record.post`, le compte rendu de la tâche :

```json
{ "record_start_time": 1789819321, "record_use_time": 8, "record_clean_area": 756,
  "clean_count": 2, "record_task_status": 1, "clean_current_map": 1000000002 }
```

`record_task_status` vaut 1 pour une tâche menée à terme, 2 pour une tâche abandonnée par l'utilisateur, 4 pour une tâche abandonnée par le robot (échec de localisation, pièce injoignable). `record_clean_mode` vaut 0 pour un nettoyage de pièces et 4 pour une cartographie ou un nettoyage de zone. Plusieurs champs transportent des entiers négatifs encodés en non signé, par exemple `4294967276` pour -20. Le compte rendu arrive aussi pour une tâche interrompue ; le cloud range la tâche dans l'historique peu après, ce qui en fait le bon signal pour recharger celui-ci. Le téléphone efface alors le tracé de sa carte.

Début et fin d'une tâche, tels que les captures les montrent (pièces, zone, cartographie, arrêts et abandons) :

| Cas | Événements | État |
|---|---|---|
| nettoyage mené à bien | `startClean`, puis, minutes après `FULL_CLEAN_FINISHED`, `clean_finish` et `clean_record` (statut 1) dans la même seconde | `INACTIVE_*` → `FULL_CLEAN_RUNNING` … `FULL_CLEAN_FINISHED` → `INACTIVE_CHARGING` |
| arrêt par l'utilisateur | `clean_record` (statut 2), jamais de `clean_finish` | `FULL_CLEAN_PAUSED` ou `RUNNING` → `ABORTED` |
| échec de localisation | `locate_fail`, puis `clean_record` (statut 4), jamais de `clean_finish` | `FULL_CLEAN_DISCOVERING` → `FULL_CLEAN_RUNNING` → `INACTIVE_DISCHARGING` |
| pièce injoignable | `Unable_all_area_recharge`, `clean_finish`, `clean_record` (statut 4, 0 minute, 0 dm²) | `FULL_CLEAN_FINISHED` → `INACTIVE_CHARGING` |
| cartographie | `startBuildMap`, `clean_record` (statut 1, mode 4, sans `clean_finish`), `BuildMapFinish` | `MAPPING_RUNNING` → `MAPPING_FINISHED` |
| nettoyage de zone | comme un nettoyage de pièces, avec un `clean_record` de mode 4 | `currentCleaningMode` à `spotZoneConfigured` |

Un `startClean` n'est donc pas un départ : l'un d'eux est suivi aussitôt d'un `clean_record` de statut 2, avant un second `startClean` qui, lui, mène à `FULL_CLEAN_RUNNING`. C'est le passage à un état de nettoyage depuis un état qui n'en est pas un qui marque le début. Les reprises après une pause, une relocalisation (`FULL_CLEAN_DISCOVERING`), un lavage du rouleau ou une recharge au milieu du nettoyage (`FULL_CLEAN_CHARGING`) partent d'un état de nettoyage et n'en sont pas. Un `clean_finish` ne dit pas non plus que la tâche a réussi, la pièce injoignable en est la preuve : c'est `clean_record` qui tranche. DySS Cockpit en tire `CleaningTaskDetector` : début au passage décrit ci-dessus (jamais au premier état reçu, qui n'est qu'un point de départ, ni pour une cartographie), fin pour un statut 1 précédé d'un `clean_finish` (ou de mode 0 si celui-ci s'est perdu), « interrompu » pour un statut 4, rien pour un statut 2, et un même compte rendu, reconnu à son `record_start_time`, n'est annoncé qu'une fois.

### Codes de faute

Le champ `nextActionRequired` distingue l'indicateur de statut de la vraie panne. La famille `21xx`, avec `LOG_ONLY`, sert d'indicateur ; un code à trois chiffres avec `WAIT_TO_CLEAR` est une vraie erreur.

| Code | `nextActionRequired` | Signification observée |
|---|---|---|
| `0` | absent | plus aucune faute |
| `501` | `WAIT_TO_CLEAR` | transitoire, au retour sur la base après un abandon |
| `589` | `WAIT_TO_CLEAR` | **échec de localisation**, précédé de `event.locate_fail.post` |
| `2102` | `LOG_ONLY` | nettoyage ou cartographie terminé |
| `2103` | `LOG_ONLY` | en charge |
| `2104` | `LOG_ONLY` | abandonné |
| `2105` | `LOG_ONLY` | au repos sur la base |
| `2108` | `LOG_ONLY` | localisation en cours, avec `FULL_CLEAN_DISCOVERING` |
| `2109` | `LOG_ONLY` | pendant le nettoyage |
| `2110`, `2112` | `LOG_ONLY` | cartographie en cours |

Deux échecs se distinguent :

- **Échec de localisation** : le robot part, passe en `FULL_CLEAN_DISCOVERING` avec la faute `2108`, échoue, émet `event.locate_fail.post` puis la faute `589`, abandonne avec `record_task_status: 4` et revient à la base.
- **Pièce injoignable** : `service.set_room_clean` répond `code: 1` au lieu de 0, mais le nettoyage démarre quand même. Après plusieurs minutes à chercher un chemin, le robot émet `event.Unable_all_area_recharge.post` (sans paramètre), puis `event.clean_finish.post` et `event.clean_record.post` avec `record_task_status: 4`, sans faute `589`.

### Résultat par pièce (`cleanStatus`)

`persistent-maps`, `live-maps` et `clean-maps-data` portent un `cleanStatus` par pièce :

| `cleanStatus` | Signification |
|---|---|
| `CLEAN_NOT_REQUESTED` | pièce non sélectionnée pour cette tâche |
| `CLEAN_COMPLETE` | nettoyée |
| `CANT_CLEAN` | le robot a renoncé à l'atteindre (voir « Pièce injoignable » ci-dessus) |
| `CLEAN_PENDING` | sélectionnée, mais son tour n'est jamais venu (tâche arrêtée avant) |

C'est le seul résultat disponible : la liste de l'historique (`GET /v2/{serial}/clean-maps`) n'a ni ce champ ni aucun équivalent global pour la tâche. Il faut le détail d'un nettoyage (`clean-maps-data/{cleanId}`, plusieurs centaines de Ko avec le tracé) pour le connaître.

**`isSelected` et `settings` ne sont pas un instantané historique.** Présents sur les pièces de `clean-maps` comme de `clean-maps-data`, ils reflètent la préférence *actuelle* de la carte, pas celle du nettoyage consulté : une pièce exclue au lancement revient avec `isSelected: true` dès que sa sélection change, et `settings.cleanType` peut indiquer `vacuum` pour des pièces lancées avec la serpillière. Les pièces nettoyées par une tâche se déduisent donc de `cleanStatus` (tout sauf `CLEAN_NOT_REQUESTED`), et le type de nettoyage utilisé pour chaque pièce n'est pas récupérable a posteriori.

## Station

Chaque action de la station est écrite dans les deux dialectes : `START-DOCK-ACTION` ou `ABORT-DOCK-ACTION` avec une action classique, et `service.start_station_act` avec `ctrl_value` 1 pour lancer, 0 pour arrêter, et un numéro d'action. Les actions de l'application, retrouvées dans son enum : `COLLECT_DUST`, `WASH_MOP`, `DRY_MOP`.

| Action | Classique | jdm `station_act` | Source |
|---|---|---|---|
| Vider le collecteur | `START-DOCK-ACTION` `COLLECT_DUST` | 3 | capturé |
| Arrêter le séchage | `ABORT-DOCK-ACTION` `DRY_MOP` | 2 | capturé |
| Laver et sécher | `START-DOCK-ACTION` `WASH_MOP` | 1 | action dans l'APK, numéro déduit |

```json
{ "msg": "ABORT-DOCK-ACTION", "action": "DRY_MOP", "mode-reason": "RAPP" }
{ "method": "service.start_station_act", "params": { "ctrl_value": 0, "station_act": 2 } }
```

Le numéro envoyé n'est pas toujours celui que le robot rapporte : le vidage, demandé avec `station_act: 3`, est signalé par `prop.post` avec `station_act: 5` et `dust_action: 1`. Le bouton « Laver et sécher » correspond à `WASH_MOP`, le séchage suivant le lavage.

## Réglages

Chaque changement de réglage est écrit **deux fois** par l'application, avec le même horodatage : un `STATE-SET` classique et un `prop.set` jdm. Le robot acquitte le `prop.set` par `{"property": [{"prop": "…", "code": 0}]}`.

| Réglage | `STATE-SET` (classique) | `prop.set` (jdm) |
|---|---|---|
| Eau chaude pour la serpillière | `{"hotWaterMop": true}` | `{"hot_water_mop": 1}` |
| Chauffe-eau du dock | `{"hotWaterSwitch": true}` | `{"hot_water_switch": 1}` |
| Détergent | `{"detergent": true}` | `{"detergent": 1}` |
| Intervalle d'auto-nettoyage : après chaque pièce | `{"backWashType": "ROOM"}` | `{"back_wash_type": 1}` |
| Intervalle : toutes les 15 ou 30 min | `{"backWashType": "TIME", "backWashTime": 15}` | `{"back_wash_type": 0, "back_wash_time": 15}` |
| Intervalle : uniquement si nécessaire | `{"backWashType": "TIME", "backWashTime": 60}` | `{"back_wash_type": 0, "back_wash_time": 60}` |
| Durée de séchage du rouleau (3, 4 ou 5 heures) | `{"airDryFrequency": 4}` | `{"airdry_frequency": 4}` |
| Prolonger les préparatifs de lavage | `{"washMopBeforeClean": true}` | inconnu |
| Sons | `{"alarm": true}` | `{"alarm": 1}` |
| Volume (0 à 100) | `{"volume": 40}` | `{"volume": 40}` |
| Mise à jour automatique | aucun | `{"privacy": {"auto_upgrade": true}}` |

« Uniquement si nécessaire » n'est pas une valeur à part : dans l'enum de l'application, c'est l'entrée `ONLY_WHEN_NEEDED` avec 60 minutes, envoyée comme un intervalle de 60. L'enum connaît aussi 8, 20 et 25 minutes, que l'écran ne propose pas. `airDryFrequency` est une durée en heures. `washMopBeforeClean` figure dans le modèle des réglages classiques de l'application, mais ni dans `CURRENT-STATE` ni dans les propriétés jdm observées.

Champs du modèle `STATE-SET` de l'application : `airDryFrequency`, `alarm`, `backWashTime`, `backWashType`, `childLock`, `collectDustOnSelfClean`, `detergent`, `doNotDisturbMode`, `emptyBinTime`, `emptyBinType`, `hotWaterMop`, `hotWaterSwitch`, `resetConsumable`, `washMopBeforeClean`.

### Voix

`SET-VOICE-LANGUAGE` avec `{"language": "fr-CA"}` déclenche un téléchargement sur le robot, qui le rapporte par une suite de `VOICE-DOWNLOAD-STATUS` (`downloading` avec `progress`, `installing`, puis `idle`). `REQUEST-VOICE-DOWNLOAD-STATUS` demande l'état courant. Côté jdm, l'application envoie `service.download_voice_type` avec l'URL du paquet sur `device-package.cp.dyson.com`, son MD5 et un `type` numérique, et interroge `service.get_voice_download`. La langue de la voix est sans rapport avec celle des noms de pièces.

## Cartes et pièces

### Commandes jdm

| Méthode | Paramètres | Réponse |
|---|---|---|
| `service.get_map_list` | `{}` | `{map_list: [{name, id, cur}, …]}` |
| `service.set_cur_map` | `{map_id}` | `{result: 0, result: 0}` |
| `service.rename_map` | `{map_id, map_name}` | `{result: 0}` |
| `service.del_map` | `{map_id}` | `{result: 0, result: 0}` |
| `service.rename_room` | `{map_id, room_id, room_name}` | `{map_id, map_type: 3, timestamp}` |
| `service.split_room` | `{map_id, room_id, split_points: [x1, y1, x2, y2], lang}` | `{map_id, map_type: 3, timestamp}`, ou `{result: 1}` si refusé |
| `service.arrange_room` | `{map_id, room_ids: [16, 15], lang}` | `{map_id, map_type: 3, timestamp}`, ou `{result: 1}` si refusé |
| `service.delete_room` | `{map_id, room_id, is_preview}` | `{result: 1}` : toujours refusé, voir plus bas |
| `service.set_virtual_wall` | `{virwall: [n, [map_id, type, 8 coordonnées], …]}` | `{map_id, map_type, timestamp}`, clés en double |
| `service.adjust_furniture` | `{timestamp, package: [1, 1], furniture_list: "…"}` | `{map_id: 0, map_type: 0, timestamp, package}` |

**Deux formes de réponse coexistent**, et laquelle une méthode emploie ne se devine pas : un code `{result: 0}` (0 réussite, 1 refus) ou la carte réenregistrée `{map_id, map_type, timestamp}`. Plusieurs réponses **répètent une clé** dans `data` (`{"result":0,"result":0}`, `map_id` deux fois) : un `JsonObject` .NET lève une exception en y accédant, il faut les relire membre par membre.

`service.set_cur_map` change la carte active du compte, exactement comme le sélecteur de carte de l'application mobile ; on ne peut nettoyer que la carte active. `service.arrange_room` fusionne les pièces listées. `room_name` suit la double forme de `room_preference`, chaîne simple ou objet JSON encodé avec `type` et `name`.

Chaque modification est suivie d'un `event.map_change.post` dont `result` donne l'issue, d'un message sur le topic `RB05/{serial}/status/jdm/map` (`{"message": "Processing success", "status": "SUCCESS", "mapId": 1000000002}`), puis d'un `MAP-UPLOAD-STATUS` une fois la copie du cloud à jour. C'est ce dernier qu'il faut attendre avant de relire la carte en REST, sous peine de relire l'ancienne.

| `result` de `map_change` | Signification |
|---|---|
| 3 | modification appliquée |
| 4 | division refusée (pièce trop petite) |
| 6 | fusion refusée (pièces non adjacentes) |

**Seule la carte active doit être modifiée.** Sur 36 modifications capturées, depuis le téléphone comme depuis DySS Cockpit, la carte visée était toujours la carte active. Deux divisions envoyées sur une carte non active ont chaque fois fait de cette carte la carte active, et lui ont donné le nom de la carte qui l'était jusque-là.

**Supprimer une carte** (`del_map`) fonctionne sur la carte active : le robot en active alors une autre de lui-même, et le `MAP-UPLOAD-STATUS` suivant porte sur cette nouvelle carte active.

**Diviser une pièce** envoie la pièce visée et les deux extrémités du trait de coupe, en mètres dans le repère de la carte ; le robot recale la coupe sur sa propre grille. La division efface le nom et le type des deux moitiés, qui reviennent sans type, nommées « Pièce1 », « Pièce2 »… (deux pièces ont même porté le même nom). Il faut les renommer après coup.

**Supprimer une pièce** n'est pas possible sur ce firmware. Le téléphone passe par `PUT …/zones-definitions/{mapId}/remove-zone`, que le cloud relaie au robot sous la forme `service.delete_room` ; le robot la refuse (`{result: 1}`), en aperçu (`is_preview` 1) comme en suppression réelle (0), et le cloud traduit ce refus en HTTP 500. Le même aperçu envoyé directement en jdm sur d'autres pièces est refusé aussi. Pour faire disparaître une pièce, il faut la fusionner avec une voisine.

### Noms des pièces

L'application affiche, pour une pièce d'un type reconnu, le libellé propre à ce type et non le nom stocké. Une pièce de type `toilet` dont le nom stocké vaut « Salle de bain » s'affiche « W.-C. » ; une pièce `livingRoom` nommée « Salon2 » s'affiche « Salon ». Seul le type `custom`, qui n'a pas de libellé, affiche le nom stocké tel quel. Dyson pré-remplit le nom stocké d'une nouvelle pièce avec le libellé de son type, et n'ajoute un numéro (« Salon12 ») que pour garder ce champ unique. DySS Cockpit affiche, lui, toujours le nom stocké.

Les trente types (énumération `d11.a` de l'APK) ont été confirmés en découpant une carte de test en une pièce par type :

| Type REST | Libellé affiché |
|---|---|
| `balcony` | Balcon |
| `bathroom` | Salle de bain |
| `bedroom` | Chambre |
| `boxroom` | Cagibi |
| `cloakroom` | Toilettes |
| `closet` | Dressing |
| `conservatory` | Véranda |
| `dining` | Salle à manger |
| `ensuite` | Salle de bain attenante |
| `entrance` | Hall d'entrée |
| `familyRoom` | Pièce familiale |
| `guestBathroom` | Salle de bain invités |
| `guestBedroom` | Chambre d'amis |
| `guestRoom` | Chambre invités |
| `hallway` | Couloir |
| `kidsBedroom` | Chambre d'enfant |
| `kitchen` | Cuisine |
| `laundryRoom` | Buanderie |
| `livingRoom` | Salon |
| `nursery` | Chambre de bébé |
| `office` | Bureau |
| `pantry` | Cellier |
| `playRoom` | Salle de jeux |
| `primaryBathroom` | Salle de bain parentale |
| `primaryBedroom` | Chambre parentale |
| `recreationRoom` | Salle de loisirs |
| `storageRoom` | Débarras |
| `study` | Bibliothèque |
| `toilet` | W.-C. |
| `utilityRoom` | Cave |
| `custom` | (pas de libellé : nom stocké affiché tel quel) |

`GET /v2/app/{serial}/persistent-map-metadata` renvoie les pièces dans un ordre qui n'est ni alphabétique ni croissant par identifiant, probablement celui de leur détection. L'application Android semble les grouper par type, selon un tri qui n'a pas été identifié.

### Langue des noms créés (`lang`)

`split_room` et `arrange_room` portent un `lang` : la langue dans laquelle le robot nomme les pièces qu'il crée, en devinant au passage un type (« Pièce3 », « Chambre6 »). Établi le 29 septembre en divisant la même pièce avec chaque valeur de 0 à 20 :

| `lang` | Langue | Noms obtenus |
|---|---|---|
| 0, 1 | chinois simplifié | 房间3, 卧室6 |
| 2 | anglais | Room3, Bedroom6 |
| 3 | espagnol | Habitación3, Dormitorio6 |
| 4 | allemand | Raum3, Schlafzimmer6 |
| 5 | français | Pièce3, Chambre6 |
| 6 | polonais | Pomieszczenie3, Bedroom6 |
| 7 | italien | Stanza3, Camera da letto6 |
| 8 | russe | Комната3, Bedroom6 |
| 9, 14 | chinois traditionnel | 房間3, Bedroom6 |
| 10 | thaï | ห้อง3, Bedroom6 |
| 11 | coréen | 방3, Bedroom6 |
| 13 | portugais | Room3, Quarto6 |
| 12, 15 à 20 | (anglais par défaut) | Room3, Bedroom6 |

Plusieurs langues ne sont que partiellement traduites. Une fusion nomme la pièce restante de la même façon. Pas de japonais, de néerlandais ni de langues nordiques parmi les valeurs essayées. Côté REST, `divide-zone` et `merge-zones` portent la langue en toutes lettres (`language`) : c'est le cloud qui la traduit en code.

### Zones de restriction (`set_virtual_wall`)

Chaque appel envoie **la liste complète** des zones de la carte active, `[nombre, zone, zone, …]`, et `[0]` les efface toutes : ajouter une zone suppose de renvoyer les existantes. Une zone vaut `[map_id, type, x1, y1, x2, y2, x3, y3, x4, y4]`, un rectangle en mètres. `persistent-maps` les restitue sous `restrictions`, `{id, points, behavior}`. Correspondance établie en recoupant les coordonnées des mêmes rectangles des deux côtés :

| jdm `type` | REST `behavior` | Libellé de l'application | Effet annoncé |
|---|---|---|---|
| 2 | `keepOut` | Éviter la zone | le robot ne nettoie pas cette zone |
| 13 | `climbObstacle` | Franchir le seuil | le robot tente de franchir de petits obstacles |
| 12 | `brushBarOff` | Lavage uniquement | nettoyage sans la brosse |
| 6 | `noMop` | Aspirateur uniquement | nettoyage sans laver |

La réponse reprend dans son second `map_type` le type de la zone ajoutée en dernier. Le téléphone donne les coins d'un rectangle droit dans l'ordre haut-gauche, bas-gauche, bas-droite, haut-droite (y vers le haut), et renvoie les zones existantes telles que `persistent-maps` les restitue : elles glissent d'un centimètre ou deux d'un envoi à l'autre, vraisemblablement recalées par le robot.

### Meubles (`adjust_furniture`)

`furniture_list` est une **chaîne** contenant du JSON (double encodage) : `[[index, code, 1, x1, y1, x2, y2, x3, y3, x4, y4], …]`, liste complète à chaque appel, `"[]"` pour tout effacer. Pas de `map_id` : l'appel porte sur la carte active. `package: [1, 1]` est vraisemblablement le découpage d'une longue liste en plusieurs messages (partie, nombre de parties) ; une liste de 22 meubles tient encore dans un seul.

`persistent-maps` restitue les meubles sous `furniture`, `{id, type, userDefined, points}`. L'`id` REST est l'`index` jdm, et les points sont les mêmes, dans le même ordre.

| jdm `code` | REST `type` | Meuble | Dimensions par défaut (m) |
|---|---|---|---|
| 1511 | `diningTableAndChairs` | table et chaises | 1,4 × 2,0 |
| 1512 | `twoSeaterSofa` | canapé deux places | 1,1 × 1,8 |
| 1513 | `doubleBed` | lit double | 2,1 × 1,8 |
| 1514 | `toilet` | toilettes | 0,7 × 0,5 |
| 1515 | `cabinet` | meuble | 0,9 × 2,1 |
| 1516 | `refrigerator` | réfrigérateur | 0,8 × 0,8 |
| 1518 | `squareCoffeeTable` | table basse carrée | 0,5 × 1,1 |
| 1519 | `bedsideTable` | table de chevet | 0,6 × 0,8 |
| 1520 | `tvStand` | meuble TV | 0,6 × 2,3 |
| 1524 | `washingMachine` | lave-linge | 1,0 × 0,9 |
| 1525 | `threeSeaterSofa` | canapé trois places | 1,1 × 2,5 |
| 1526 | `lShapedSofaLeft` | canapé d'angle, à gauche | 1,7 × 2,4 |
| 1527 | `lShapedSofaRight` | canapé d'angle, à droite | 1,8 × 2,4 |
| 1528 | `singleSeaterSofa` | fauteuil | 1,0 × 0,9 |
| 1601 | `singleBed` | lit simple | 2,1 × 1,2 |
| 1602 | `roundCoffeeTable` | table basse ronde | 1,1 × 1,1 |
| 1603 | `desk` | bureau | 1,4 × 1,6 |
| 1604 | `storageCabinet` | meuble de rangement | 0,4 × 1,4 |
| 1605 | `shoeCabinet` | meuble à chaussures | 0,4 × 1,4 |
| 1606 | `wardrobe` | armoire | 1,0 × 1,8 |
| 1607 | `bookshelf` | bibliothèque | 0,4 × 1,4 |
| 1608 | `cabinetWithStove` | meuble avec cuisinière | 0,6 × 0,6 |
| 1613 | `indoorPlant` | plante | 0,5 × 0,5 |
| 1614 | `standingMirror` | miroir sur pied | 0,5 × 0,7 |

Les dimensions sont les côtés (point 1 → 2, puis 2 → 3) des meubles posés par le téléphone. Les codes forment deux séries, 15xx et 16xx, avec des trous (1517, 1521 à 1523, 1609 à 1612) qu'aucun meuble proposé n'occupe. L'APK contient les noms de trois autres meubles (`lShapeCabinetLeft`, `lShapeCabinetRight`, `uShapeCabinet`) sans les proposer ; leurs codes sont inconnus, et un client ne doit jamais envoyer un code qu'il n'a pas vu.

### Création d'une carte

```
-> command       {"msg": "STATE-SET", "mapLanguage": "fr-CH", "mode-reason": "RAPP"}
-> command       {"msg": "START-MAPPING", "mode-reason": "RAPP"}
-> command/jdm   service.start_explore  {"mode": 0}
<- status/jdm    event.startBuildMap.post
<- status        state MAPPING_RUNNING, persistentMapId "0", fautes 2110 puis 2112
<- status        state MAPPING_FINISHED, faute 2102
<- status/jdm    event.clean_record.post avec record_clean_mode 4
<- status/jdm    event.BuildMapFinish.post
<- status        persistentMapId prend l'identifiant de la nouvelle carte
```

`mapLanguage` sert au nommage automatique des pièces. Pendant la cartographie, `persistentMapId` vaut `"0"`.

### Orientation de la carte

La rotation d'une carte ne passe pas par le robot : c'est un réglage d'affichage tenu par le cloud. `persistent-maps/{mapId}` la rend sous `orientation`, en degrés **dans le sens des aiguilles d'une montre** (le téléphone n'a qu'un bouton, un quart de tour horaire à chaque appui). Elle s'écrit comme le fait le téléphone, sur une carte active ou non :

```
PUT /v2/app/{serial}/persistent-maps/{mapId}
{ "orientation": 180 }
```

Seules 0, 90, 180 et 270 sont admises. Le corps accepte aussi `name`, `zone {id, name, type}`, `isCurrentMap` et `furniture`, tous facultatifs.

### Grille d'occupation

`GET /v1/app/{serial}/live-maps/mapping` renvoie `mapData`, `width × height` entiers en ordre ligne par ligne : la cellule `(cx, cy)` est `mapData[cy × width + cx]`, avec `cx = (x − offsetX) / resolution` et `cy = (y − offsetY) / resolution`, sans inversion d'axe. Vérifié en s'assurant que les points visités de chaque pièce tombent sur des cellules portant son identifiant.

| Valeur | Signification |
|---|---|
| `0` | inconnu ou hors du logement |
| `255` | obstacle, mur |
| autre | identifiant de la pièce, `10`, `11`, `12`… |

La grille encode donc directement la découpe en pièces : 320 × 420 cellules de 5 cm pour un logement de 16 m sur 21 m, environ 300 Ko. Les longues pointes qui dépassent des murs sont des artefacts du lidar à travers les vitres.

### Cartes non actives

`live-maps/mapping` ne renvoie la grille que pour la carte active. Les autres n'ont que la géométrie de `persistent-maps/{mapId}` : points visités et segments par pièce, sans grille.

Certaines cartes non actives renvoient en outre une station factice, observée à `(1100.0, 1100.0)` sur deux cartes par ailleurs bien réelles. Prise telle quelle pour cadrer la vue, elle gonfle la boîte englobante à plus de 1000 m de côté ; il faut l'ignorer quand elle tombe loin du reste des données.

## Horaires

Les horaires sont exécutés par le robot, et le cloud en garde le détail. **Chacun appartient à une carte.**

```json
{ "method": "service.add_order", "params": {
  "id": 286166297, "enable": 1, "day": 1, "hour": 10, "minute": 0, "repeat": 1,
  "map_id": 1000000002, "room_count": 6, "time_zone": 3600, "prefer_type": 1, "is_global": 0,
  "areas": [], "room_preference": [ …tableaux positionnels… ], "uv_switch": [[11, 0], [10, 1]]
}}
```

- `id` est choisi par l'application (un entier aléatoire). Renvoyer `add_order` avec le même `id` **remplace** l'horaire : c'est ainsi que l'application le modifie, et qu'elle l'active ou le désactive (`enable` 1 ou 0, tout le reste renvoyé tel quel). Réponse `{result: 0}`.
- `service.del_order` `{id}` supprime.
- `day` est un masque de bits, lundi en premier : lundi 1, mardi 2, mercredi 4, jeudi 8, vendredi 16, samedi 32, dimanche 64 (73 pour lundi, jeudi et dimanche ; 127 tous les jours).
- `repeat` vaut toujours 1 : l'application exige au moins un jour et ne propose pas d'horaire ponctuel. La valeur 0 n'a jamais été essayée.
- `room_count` est le nombre de pièces de la carte, pas celui des pièces retenues. Toutes figurent dans `room_preference` (voir [Réglages par pièce](#réglages-par-pièce-room_preference)), les pièces retenues d'abord dans leur ordre de passage compté à partir de 0, les autres ensuite. Le nom est envoyé en chaîne simple, l'indice 2 à 0.
- `is_global` reste à 0 même quand toutes les pièces sont cochées ; l'application n'a pas de bouton « toute la maison » pour les horaires. `prefer_type` vaut toujours 1 et `areas` est toujours vide.
- `time_zone` est un décalage en secondes, calculé par l'application à partir du fuseau enregistré côté cloud (3600 pour Londres en heure d'été). **Le robot l'ignore** : voir [Fuseau horaire](#fuseau-horaire).

Après chaque changement, le robot publie `prop.post` `order_total {total, enable}` : le nombre d'horaires **de la carte active** et le nombre de ceux qui sont activés. Changer de carte active publie aussi `order_total` pour la nouvelle carte ; revenir sur la première retrouve ses horaires intacts.

**Le robot ne rend pas le détail des horaires, le cloud si.** `service.get_order` `{}` ne renvoie que `order_data_lite {total, enable, timestamp, md5}`. Le détail se lit en REST, comme le fait le téléphone :

```
GET /v1/unifiedscheduler/{serial}/events?productType=804
{ "enabled": true, "serial": "…", "events": [
  { "groupId": 544623979, "days": [0], "startTime": "11:00", "weeklyRepeat": true, "enabled": true,
    "settings": { "persistentMapId": "1000000002", "zones": [
      { "id": "11", "name": "Salle de bain", "type": "", "isSelected": true, "order": 0,
        "settings": { "cleaningStrategy": "auto", "cleanType": "vacuum", "waterLevel": "low",
                      "mopPasses": 1, "dryPasses": 1, "vacuumPowerMode": 0, "isUvScanOn": false } }, … ] } } ] }
```

- `productType` est le type d'appareil du manifeste (804 pour le RB05) ; sans lui, 404.
- `groupId` est l'identifiant de l'horaire, celui de `add_order` et `del_order`.
- `days` compte **à partir du dimanche = 0** (lundi 1 … samedi 6), à l'inverse du masque du robot.
- `settings.zones` a la forme des pièces de `persistent-map-metadata` : les pièces à nettoyer ont `isSelected`, dans l'ordre de `order`, avec leurs réglages sous leurs noms REST.
- La liste est celle de la **carte active**.
- Le cloud la tient à jour quelle que soit l'origine de l'horaire : ceux envoyés au robot en `add_order` apparaissent aussi sur le téléphone.

Le téléphone écrit par `PUT` sur la même adresse, avec `{serial, enabled, events, updatedEvents}` (la liste entière et les `groupId` modifiés) ; le cloud relaie ensuite `add_order` ou `del_order` au robot. Le `md5` du résumé change avec le contenu (celui d'une liste vide est `6a8ad4dbe08d69c27dbb2b53c97da3f8`) mais ne correspond à aucune sérialisation évidente.

Quand deux horaires d'une même carte sont proches, l'application avertit : « Ce programme ne commencera pas si le programme précédent est toujours en cours ». C'est donc le robot qui arbitre, et l'application qui estime la durée d'un nettoyage, par `POST /v2/app/{serial}/persistent-maps/{mapId}/clean-estimation` avec `{spotZones, zones}`, qui renvoie `durationMinutes` ; la forme exacte du corps n'est pas connue.

## Fuseau horaire

**Le robot déclenche les horaires à l'heure de Pékin (UTC+8)**, sans doute son fuseau d'usine. Observé le 1er octobre 2026 : un horaire du jeudi à 10:00 a démarré à 04:00 heure de Suisse (UTC+2), soit 02:00 UTC, c'est-à-dire 10:00 à UTC+8. Quatre horaires du mardi à 18, 19, 20 et 21 h, attendus en vain le soir du 29 septembre, tombaient de même entre 12 et 15 h, heure locale.

Deux chemins existent pour changer ce fuseau, et aboutissent au même endroit :

- jdm : `service.set_robot_time_zone` `{"time_zone": "Europe/Amsterdam"}` ;
- REST : `PUT /v1/machine/{serial}/timezone` `{"timezone": "Europe/Amsterdam"}`, que le cloud relaie au robot. C'est ce qu'utilise l'écran « Fuseau horaire » du téléphone. `GET` sur la même adresse lit la valeur côté cloud.

Sur le firmware `RB05PR.01.000.0436`, le robot refuse les deux, avec `result: 1` en jdm et, en REST, HTTP 424 « Failed to update JDM machine timezone … Response code: 1 », au repos comme en phase de séchage. C'est un défaut du firmware.

## API REST de l'application mobile

Relevé le 28 septembre 2026 dans l'interface Retrofit de l'APK 6.4.26360. Les annotations y sont renommées (`c82.f` GET, `c82.p` PUT, `c82.b` DELETE, `c82.o` POST, `c82.s` paramètre de chemin, `c82.t` paramètre de requête, `c82.a` corps) ; les champs JSON gardent leurs noms Gson. Dans le vocabulaire REST des cartes, une « zone » est une **pièce** ; les zones de restriction sont les `restrictions`.

### Lecture

Tous vérifiés le 19 septembre 2026.

| Endpoint | Contenu |
|---|---|
| `GET /v2/app/{serial}/persistent-map-metadata` | cartes, pièces, réglages par pièce, ordre et sélection |
| `GET /v2/app/{serial}/persistent-maps/{mapId}?isPreview=` | géométrie : dimensions de la grille, pièces avec points visités et segments, station, meubles, restrictions, orientation |
| `GET /v1/app/{serial}/live-maps/cleaning` | la même géométrie, plus `robotLocation` et `cleanPath` de la tâche en cours, utilisable à tout moment |
| `GET /v1/app/{serial}/live-maps/mapping` | grille d'occupation de la carte active |
| `GET /v2/{serial}/clean-maps` | historique : durée, surface, batterie au départ et à l'arrivée, fautes, lien S3 présigné de 15 minutes vers un blob zlib |
| `GET /v2/{serial}/clean-maps-data/{cleanId}` | détail d'un nettoyage : tracé, taches, obstacles, résultat par pièce |
| `GET /v1/unifiedscheduler/{serial}/events?productType=804` | horaires de la carte active |
| `GET /v1/assets/devices/{serial}/ota` | état de mise à jour du firmware |
| `GET /v1/machine/{serial}/timezone` | fuseau horaire côté cloud |

Les batteries de l'historique arrivent en nombres à virgule (`91.0`), pas en entiers. `GET /v1/telemetry/device/{serial}/sessions` exige `start` et `end` et répond 400 quelle que soit leur forme : il sert vraisemblablement aux purificateurs.

### Écriture

| Verbe | Adresse | Corps | Rôle |
|---|---|---|---|
| PUT | `/v2/app/{serial}/persistent-map-metadata/{mapId}` | liste des pièces | réglages, sélection, ordre |
| PUT | `/v2/app/{serial}/persistent-maps/{mapId}` | `{name, zone, orientation, isCurrentMap, furniture}` | renommer, activer, tourner (vérifié), meubles |
| DELETE | `/v2/app/{serial}/persistent-maps/{mapId}` | | supprimer la carte |
| PUT | `/v2/app/{serial}/zones-definitions/{mapId}/divide-zone` | `{threshold: {zoneId, start, end}, language}` | diviser une pièce |
| PUT | `/v2/app/{serial}/zones-definitions/{mapId}/merge-zones` | `{zoneIds, language}` | fusionner des pièces |
| PUT | `/v2/app/{serial}/zones-definitions/{mapId}/remove-zone` | `{isPreview, zoneId}` | supprimer une pièce (refusé par le robot) |
| PUT | `/v2/app/{serial}/restrictions-definitions/{mapId}` | liste de restrictions | zones de restriction |
| POST | `/v2/app/{serial}/persistent-maps/{mapId}/clean-estimation` | `{spotZones, zones}` | durée estimée d'un nettoyage |
| PUT | `/v1/unifiedscheduler/{serial}/events?productType=804` | `{serial, enabled, events, updatedEvents}` | horaires |
| PUT | `/v1/machine/{serial}/timezone` | `{timezone}` | fuseau horaire (refusé par le robot) |
