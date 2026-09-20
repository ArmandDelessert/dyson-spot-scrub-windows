# Protocole observé du Dyson Spot+Scrub AI (RB05)

Établi à partir de captures MQTT réelles du 19 septembre 2026, robot anonymisé en `SERIAL` dans les exemples,
firmware `RB05PR.01.000.0436`, région `eu-west-1`.

Deux dialectes circulent sur les mêmes topics.

- **Dyson classique** sur `RB05/{serial}/status` et `RB05/{serial}/command`, messages identifiés par `msg`.
- **jdm**, une couche JSON-RPC, sur `RB05/{serial}/status/jdm` et `RB05/{serial}/command/jdm`,
  messages identifiés par `method`. C'est la couche de la plateforme robot sous-jacente, en snake_case.

Les deux décrivent le même état. L'application lit les deux et les fusionne.

## Messages classiques

### CURRENT-STATE

Émis en réponse à `REQUEST-CURRENT-STATE`, et spontanément pendant un nettoyage. Vingt-neuf champs
observés. Exemple au repos sur la station :

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

Pendant un déplacement, le robot ajoute `globalPosition`, un tableau de points en mètres relatifs à
la station.

États observés : `INACTIVE_CHARGING`, `INACTIVE_DISCHARGING`, `FULL_CLEAN_DISCOVERING`,
`FULL_CLEAN_RUNNING`, `FULL_CLEAN_FINISHED`. Voir la section sur le cycle de vie pour l'enchaînement.
L'APK en contient davantage, notamment `FULL_CLEAN_PAUSED`, `FULL_CLEAN_ABORTED`,
`FULL_CLEAN_NEEDS_CHARGE` et `MAPPING_RUNNING`.

États observés depuis : `FULL_CLEAN_PAUSED`, `ABORTED` (sans préfixe, juste après un abandon),
`MAPPING_RUNNING`, `MAPPING_FINISHED`.

Valeurs de `dockState` observées : `IDLE`, `WASHING_MOP`, `COLLECTING_DUST`, `DRYING_MOP`.
Valeurs de `fullCleanAction` : `NONE`, `VACUUMING`, `VACUUMING_AND_MOPPING`.

`currentCleaningMode` vaut `global` au repos et `zoneConfigured` pendant une tâche.

Le code de faute `2105` accompagné de `nextActionRequired: LOG_ONLY` est un état normal, pas une
panne. La famille `21xx` sert d'indicateur de statut.

### Autres messages classiques observés

`MAP-UPLOAD-STATUS` signale la fin d'un envoi de carte : `{ "status": "COMPLETE", "persistentMapId": "1000000003" }`.

`START` et `ABORT-DOCK-ACTION` sont détaillés plus bas, dans la section des commandes.

Observés aussi : `STATE-SET`, `START-DOCK-ACTION`, `SET-VOICE-LANGUAGE`,
`REQUEST-VOICE-DOWNLOAD-STATUS` et `VOICE-DOWNLOAD-STATUS`, décrits dans la section des réglages.

`PAUSE`, `ABORT` et `START-MAPPING` sont décrits dans la section sur la pause, l'abandon et la
cartographie.

Les autres types présents dans l'APK mais non encore observés : `STATE-CHANGE`, `CURRENT-FAULTS`,
`FAULTS-CHANGE`, `START-DOCK-SELF-CHECK`, `SKIP-CURRENT-ZONE`, `RESUME` et `STOP`. L'application
n'utilise jamais `STOP` : elle interrompt un nettoyage par `PAUSE` puis `ABORT`.

## Couche jdm

### prop.get et prop.post

`prop.get` renvoie l'état complet en snake_case, `prop.post` pousse les champs modifiés. Pendant un
nettoyage, `prop.post` domine le trafic, essentiellement pour `cur_path` (le tracé parcouru) et
`cleaning_area`.

Correspondance établie entre les deux dialectes :

| jdm (snake_case) | classique (camelCase) | remarque |
|---|---|---|
| `quantity` | `batteryChargeLevel` | pourcentage |
| `status` | `state` | 4 correspond à `INACTIVE_CHARGING` |
| `fault` | `activeFaults[].faultCode` | valeur numérique |
| `current_map_id` | `persistentMapId` | entier contre chaîne |
| `cleaning_time` | `cleanDuration` | minutes contre secondes, 8 contre 480 |
| `sweep_type` | mode de nettoyage | 0 aspiration seule, 7 aspiration puis serpillière |
| `work_mode` | `currentCleaningMode` | |
| `station_act` | `dockState` | 0 correspond à `IDLE` |
| `oob_state` | `outOfBoxState` | 1 correspond à `OUT_OF_BOX_COMPLETE` |
| `quiet_begin_time`, `quiet_end_time`, `quiet_is_open` | `doNotDisturbMode` | minutes depuis minuit, 1320 pour 22:00 |
| `voice_type` | `voiceLanguage` | 5 correspond à `fr-FR` |
| `main_brush`, `mop_life`, `side_brush`, `hypa`, `dock_hypa`, `ioniser` | `consumables[].usage` | |
| `back_wash_type`, `back_wash_time`, `wash_back_frequency` | `backWashType`, `backWashTime`, `backWashFrequency` | |
| `airdry_frequency`, `alarm`, `child_lock`, `detergent`, `volume` | mêmes champs | booléens en 0 ou 1 |
| `hot_water_mop`, `hot_water_switch` | mêmes champs | |
| `dust_auto_state` | `collectDustOnSelfClean` | |

Champs jdm présents mais toujours nuls au repos : `auto_water_complete_flag`,
`auto_water_self_check`, `clean_wash_attachment`, `empty_bin_time`, `empty_bin_type`,
`global_clean_status`, `mop_pad_life`, `robot_auto_updown_type`, `store_demo_mode`, `taskBeginTs`.

### service.get_map_list

```json
{ "method": "service.get_map_list", "code": 0, "data": { "map_list": [
  { "name": "Carte A", "id": 1000000001, "cur": false },
  { "name": "Carte B", "id": 1000000002, "cur": true  },
  { "name": "Carte C", "id": 1000000003, "cur": false }
]}}
```

### service.get_preference

Les pièces sont des tableaux positionnels, pas des objets. Format observé, douze éléments :

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

Les réponses comptent douze éléments par pièce, alors que le `service.set_preference` publié par
l'application n'en envoie que onze. Un lecteur doit donc tolérer les deux longueurs.

Indice 0 l'identifiant de zone, indice 1 le nom. Ce nom est soit une chaîne simple, soit un objet
JSON encodé contenant `type` et `name` pour les pièces auxquelles l'utilisateur a attribué un type.
Tout code lisant ce champ doit gérer les deux formes.

Indices établis en croisant les `set_preference` capturés avec les réglages REST des zones :

| Indice | Contenu | Valeurs |
|---|---|---|
| 3 | type de nettoyage | 0 aspiration, 1 aspiration et lavage (capturés) ; 2 lavage seul, 3 aspiration puis lavage (d'après ha-dyson-spot-scrub) |
| 8 | pièce retenue pour ce nettoyage | 0 ou 1 |
| 10 | ordre de passage | 1, 2, 3… |

L'indice 2 est renvoyé par le robot (2 pour une salle de bain, 3 pour une cuisine) et remis à 0 par
l'application. Les autres indices portent vraisemblablement le niveau d'eau, le nombre de passages
et la stratégie, non identifiés. Un client doit réécrire les seuls indices connus et renvoyer le
reste tel quel.

Côté REST, `settings.cleanType` prend `vacuum`, `mop`, `vacuumAndMop`, `vacuumThenMop` ; l'application
connaît aussi une stratégie (`auto`, `quick`, `quiet`, `boost`), un niveau d'eau (`veryLow`, `low`,
`medium`, `high`) et un nombre de passages (1 ou 2). `PUT /v2/app/{serial}/persistent-map-metadata/{mapId}`
avec la liste des zones enregistre ces réglages côté cloud. La stratégie et le niveau d'eau n'ont
pas d'équivalent connu dans les tableaux `room_preference` du jdm ; seul le type de nettoyage
(indice 3) y figure. Ce sont donc des réglages purement REST, invisibles pour un client qui n'écoute
que le MQTT.

### Le nom stocké n'est pas toujours le nom affiché

Décompiler l'énumération des types de pièce (classe `d11.a`, trente valeurs de `BALCONY` à
`UTILITY_ROOM` plus `CUSTOM`, déclarées par ordre alphabétique avec une clé de traduction chacune)
révèle que l'application affiche, pour une pièce d'un type reconnu, le libellé propre à ce type et
non le champ `name` stocké. Une pièce de type `toilet` dont le nom stocké vaut `Salle de bain`
s'affiche « W.-C. » ; une pièce `livingRoom` nommée `Salon2` s'affiche « Salon ». Seul le type
`custom`, qui n'a pas de libellé par défaut, affiche le nom stocké tel quel, par exemple `Pièce1`.
Le champ `name` continue probablement de servir ailleurs, par exemple aux assistants vocaux, mais
plus à l'affichage de la liste des pièces.

Les trente correspondances ont été confirmées en une fois : une carte de test a été découpée en
une pièce par type, et le libellé affiché par l'application pour chacune recoupé avec le champ
`type` REST de la pièce correspondante. Plusieurs noms stockés portent d'ailleurs un suffixe
numérique que l'application n'affiche jamais (`Chambre1`, `Salon12` à `Salon15`, `Salle de bain1`) :
Dyson pré-remplit le nom stocké d'une nouvelle pièce avec le libellé par défaut de son type, et
n'ajoute un numéro que pour garder ce champ unique en base quand plusieurs pièces partagent un
type, puisque l'affichage ne s'en sert de toute façon jamais pour une pièce typée.

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
| `custom` | (pas de libellé par défaut : nom stocké affiché tel quel) |

### Ordre d'affichage des pièces

`GET /v2/app/{serial}/persistent-map-metadata` renvoie les zones dans un ordre qui n'est ni
alphabétique ni croissant par identifiant, probablement celui de leur détection pendant la
cartographie. L'application Android semble grouper par type de pièce, mais cet ordre ne correspond
pas à celui, alphabétique par nom, de l'énumération des types elle-même : sur un compte de test,
l'ordre affiché a été cuisine, couloir, chambre, salon, quand l'ordre alphabétique des types
correspondants aurait été chambre, couloir, cuisine, salon. L'algorithme exact de tri du côté
Android n'a pas été identifié.

### Méthodes et événements jdm observés

`service.get_map_list`, `service.get_preference`, `service.set_preference`, `service.set_cur_map`,
`service.set_room_clean`, `service.get_order`, `service.add_order`, `service.del_order`,
`service.start_station_act`, `service.set_robot_time_zone`, `service.rename_map`,
`service.rename_room`, `service.split_room`, `service.set_virtual_wall`,
`service.adjust_furniture`, `service.arrange_room`, `service.start_explore`,
`service.start_recharge`, `service.download_voice_type`, `service.get_voice_download`,
`prop.get`, `prop.set`, `prop.post`, `event.startClean.post`, `event.clean_finish.post`,
`event.clean_record.post`, `event.locate_fail.post`, `event.map_change.post`,
`event.startBuildMap.post`, `event.BuildMapFinish.post`,
`event.shortcut_instruction_task_change.post`. Les réglages, cartes, horaires et voix sont
détaillés dans leurs sections.

## Capture

Nos droits d'abonnement couvrent `+/{serial}/#`, ce qui inclut les topics de commande. Une écoute
lancée pendant que l'application officielle pilote le robot enregistre donc aussi les requêtes
qu'elle publie, et pas seulement les réponses du robot. Les deux premières captures n'ont pas
bénéficié de cela : elles ont été faites avec des filtres restreints aux topics d'état.

```bash
dotnet run --project src/MyDyson.Cli -- watch --serial XXX-XX-XXXXXXXX --poll 0 --log capture.jsonl
```

`--poll 0` évite toute publication, donc la fermeture de connexion décrite dans le README.

## Commandes publiées par l'application

Relevées dans une capture où les filtres couvraient aussi les topics de commande.

### Séquence de démarrage d'un nettoyage de zones

C'est la séquence exacte du bouton de démarrage, en quatre messages sur deux topics.

```
-> command/jdm   service.set_preference   { map_id, prefer_type: 1, room_preference, uv_switch }
<- status/jdm    code 0, data.result 0
-> command       START
-> command/jdm   service.set_cur_map      { map_id }
-> command/jdm   service.set_room_clean   { ctrl_value: 1, clean_type: 0, room_ids: [11] }
```

La charge utile du START :

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

Les champs `fullCleanType` et `zonesDefinitionLastUpdatedDate` ne figurent dans aucune
implémentation communautaire connue. L'application les envoie systématiquement.

### Arrêt d'une action du dock

Le séchage de la serpillière dure plusieurs heures après chaque nettoyage. L'application l'interrompt
avec deux messages complémentaires :

```json
{ "msg": "ABORT-DOCK-ACTION", "action": "DRY_MOP", "mode-reason": "RAPP" }
{ "method": "service.start_station_act", "params": { "ctrl_value": 0, "station_act": 2 } }
```

### Interrogation périodique

L'application publie `REQUEST-CURRENT-STATE` toutes les trente secondes, accompagné d'un `prop.get`
qui énumère explicitement les quarante-deux propriétés voulues, dans `params.property`.

Curiosité : certains messages de l'application transportent des clés parasites à une lettre, par
exemple `{"f": "RB05", "g": "SERIAL", "h": "RB05/SERIAL/command"}`. Ce sont les champs internes de
routage de l'application, dont les noms ont été raccourcis par l'obfuscateur R8 puis sérialisés par
erreur. Le robot les ignore.

### Fuseau horaire

```json
{ "method": "service.set_robot_time_zone", "params": { "time_zone": "Europe/Amsterdam" } }
```

Le robot répond `data.result: 1`, soit un refus. Voir la section sur les codes de retour.

## Codes de retour jdm

Pour les méthodes `service.set_*`, c'est `data.result` qui porte le verdict : 0 pour un succès,
1 pour un refus. Le champ `code` de l'enveloppe ne suit pas cette convention et vaut 1 sur les
réponses à `prop.get` alors que les données sont valides.

## Cycle de vie d'un nettoyage

Enchaînement observé sur un cycle complet, aspiration et serpillière.

| `state` | `dockState` | `fullCleanAction` |
|---|---|---|
| `INACTIVE_CHARGING` | `IDLE` | `NONE` |
| `FULL_CLEAN_RUNNING` | `IDLE` | `VACUUMING_AND_MOPPING` |
| `FULL_CLEAN_RUNNING` | `WASHING_MOP` | `NONE` |
| `FULL_CLEAN_DISCOVERING` | `IDLE` | `VACUUMING_AND_MOPPING` |
| `FULL_CLEAN_FINISHED` | `COLLECTING_DUST` puis `WASHING_MOP` | `NONE` |
| `INACTIVE_CHARGING` | `DRYING_MOP` | `NONE` |

Le robot revient laver sa serpillière au dock en cours de nettoyage, `dockState` passant à
`WASHING_MOP` sans que le nettoyage s'arrête. En fin de cycle il vide son bac, lave la serpillière,
puis entame le séchage qui dure des heures.

`event.clean_finish.post` marque la fin, suivi de `event.clean_record.post` qui résume la session :

```json
{ "record_start_time": 1789819321, "record_use_time": 8, "record_clean_area": 756,
  "clean_count": 2, "record_task_status": 1, "clean_current_map": 1000000002 }
```

`record_task_status` vaut 1 pour un nettoyage mené à terme et 4 pour un nettoyage abandonné.
Plusieurs champs de ce message transportent des entiers négatifs encodés en non signé, par exemple
`4294967276` pour -20.

## Codes de faute

Le champ `nextActionRequired` distingue l'indicateur de statut de la vraie panne.

| Code | `nextActionRequired` | Signification observée |
|---|---|---|
| `0` | absent | plus aucune faute |
| `501` | `WAIT_TO_CLEAR` | transitoire, au retour sur la base après un abandon |
| `589` | `WAIT_TO_CLEAR` | **échec de localisation**, précédé de `event.locate_fail.post` |
| `2102` | `LOG_ONLY` | nettoyage terminé |
| `2103` | `LOG_ONLY` | en charge |
| `2105` | `LOG_ONLY` | au repos sur la base |
| `2108` | `LOG_ONLY` | localisation en cours, accompagne `FULL_CLEAN_DISCOVERING` |
| `2109` | `LOG_ONLY` | pendant le nettoyage |

La famille `21xx` sert donc d'indicateur de statut, pas d'alerte. Un code à trois chiffres avec
`WAIT_TO_CLEAR` est une vraie erreur qui attend une intervention.

Déroulé d'un échec de localisation, capturé en conditions réelles : le robot part, passe en
`FULL_CLEAN_DISCOVERING` avec la faute `2108`, échoue, émet `event.locate_fail.post` puis la faute
`589`, abandonne avec `record_task_status: 4`, et revient à la base.

Autre déroulé, distinct : nettoyage lancé sur une pièce que le robot ne peut pas atteindre depuis sa
position (`service.set_room_clean` répond d'ailleurs `code: 1` au lieu de `0`, mais le nettoyage
démarre quand même, `event.startClean.post` suivant immédiatement). Après plusieurs minutes à
chercher un chemin, le robot émet `event.Unable_all_area_recharge.post` (params vide), puis
`event.clean_finish.post` et `event.clean_record.post` avec `record_task_status: 4`, sans jamais
avoir posté de faute `589`. Ce message signale donc « pièce sélectionnée injoignable », un cas
différent de l'échec de localisation.

## Topic de carte

`RB05/{serial}/status/jdm/map` porte l'état du traitement des cartes côté cloud :

```json
{ "message": "Processing success", "status": "SUCCESS", "mapId": 1000000002 }
```

## Connexion au broker, tel que le fait l'application

Retrouvé par décompilation de MyDyson 6.4.26360 (classe `y50.e`, construite sur
`AwsIotMqttConnectionBuilder` du SDK AWS IoT pour Java).

- Transport : **MQTT directement sur TLS**, port 443, protocole ALPN `mqtt`. Pas de WebSocket.
- Endpoint et identifiant client : ceux de la réponse `POST /v2/authorize/iot-credentials`. Si
  l'identifiant est vide, un UUID aléatoire.
- Nom d'utilisateur MQTT, sans mot de passe :
  `?x-amz-customauthorizer-name=NOM&x-amz-customauthorizer-signature=SIGNATURE&token=VALEUR`,
  la signature étant encodée en URL si elle ne l'est pas déjà.
- Keep-alive 300 s, session propre, délai de connexion 30 s, reconnexion entre 12 et 24 heures.
- La branche SigV4 du SDK n'est jamais prise : elle ne s'active qu'en WebSocket, que l'application
  n'utilise pas. Les credentials IAM de `iot-role-credentials` ne servent donc pas au MQTT.

Le même jeton présenté dans la chaîne de requête d'une URL WebSocket n'obtient qu'une politique
de lecture. C'est la raison pour laquelle la publication échouait dans les premiers essais.

Les endpoints `/v1/device/register`, `/v1/device/client-metadata` et
`/v1/device/registerDeviceCapabilities`, un temps soupçonnés de conditionner le droit de publier,
appartiennent aux SDK PayPal et Salesforce embarqués dans l'application. Ils n'ont aucun rapport
avec le robot.

## Réglages

Chaque changement de réglage est écrit **deux fois** par l'application, dans les deux dialectes, avec
le même horodatage : un `STATE-SET` classique et un `prop.set` jdm. Le robot acquitte le `prop.set`
par `{"property": [{"prop": "…", "code": 0}]}`.

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

« Uniquement si nécessaire » n'est pas une valeur à part : dans l'enum de l'application c'est
l'entrée `ONLY_WHEN_NEEDED` avec 60 minutes, envoyée comme un intervalle de 60. `airDryFrequency`
est une durée en heures. L'enum de l'application connaît aussi 8, 20 et 25 minutes, que l'écran
n'offre pas. `washMopBeforeClean` figure dans le modèle des réglages classiques de l'application
mais ni dans `CURRENT-STATE` ni dans les propriétés jdm observées.

Les champs du modèle classique `STATE-SET` de l'application : `airDryFrequency`, `alarm`,
`backWashTime`, `backWashType`, `childLock`, `collectDustOnSelfClean`, `detergent`,
`doNotDisturbMode`, `emptyBinTime`, `emptyBinType`, `hotWaterMop`, `hotWaterSwitch`,
`resetConsumable`, `washMopBeforeClean`.

### Voix

`SET-VOICE-LANGUAGE` avec `{"language": "fr-CA"}` déclenche un téléchargement sur le robot. Le
robot le rapporte par une suite de `VOICE-DOWNLOAD-STATUS` (`downloading` avec `progress`,
`installing`, puis `idle`). Côté jdm, l'application envoie `service.download_voice_type` avec l'URL
du paquet sur `device-package.cp.dyson.com`, son MD5 et un `type` numérique, et interroge
`service.get_voice_download`. `REQUEST-VOICE-DOWNLOAD-STATUS` demande l'état courant.

### Actions du dock

Chaque action est doublée côté jdm par `service.start_station_act` avec `ctrl_value` 1 pour lancer
et 0 pour arrêter, et un numéro d'action. Les actions de l'application, retrouvées dans son enum :
`COLLECT_DUST`, `WASH_MOP`, `DRY_MOP`.

| Action | Classique | jdm `station_act` | Source |
|---|---|---|---|
| Vider le collecteur | `START-DOCK-ACTION` `COLLECT_DUST` | 3 | capturé |
| Arrêter le séchage | `ABORT-DOCK-ACTION` `DRY_MOP` | 2 | capturé |
| Laver et sécher | `START-DOCK-ACTION` `WASH_MOP` | 1 | action dans l'APK, numéro déduit |

Le bouton « Laver et sécher » de l'application correspond à `WASH_MOP`, le séchage suivant le
lavage.

## Cartes et pièces, côté jdm

| Méthode | Paramètres | Réponse |
|---|---|---|
| `service.set_cur_map` | `{map_id}` | `{result: 0}` |
| `service.rename_map` | `{map_id, map_name}` | `{result: 0}` |
| `service.rename_room` | `{map_id, room_id, room_name}` | `{map_id, map_type: 3, timestamp}` |
| `service.split_room` | `{map_id, room_id, split_points: [x1, y1, x2, y2], lang}` | `{map_id, map_type: 3, timestamp}` |
| `service.set_virtual_wall` | `{virwall: [n, [map_id, type, x1, y1, x2, y2, x3, y3, x4, y4]]}` | `{map_id, map_type: 2, timestamp}` |
| `service.adjust_furniture` | `{timestamp, package: [1, 1], furniture_list: "[[id, type, …, 8 coordonnées]]"}` | `{map_id, map_type, timestamp, package}` |
| `service.arrange_room` | `{map_id, room_ids: [16, 15], lang}` | `{map_id, map_type: 3, timestamp}` |

`service.set_cur_map` change la carte active du compte, exactement l'action du sélecteur de carte
de l'application mobile ; c'est aussi la première étape de tout nettoyage par pièce (voir plus
haut, `CleaningSequence.StartAsync`), puisqu'on ne peut lancer un nettoyage que sur la carte active.
`service.arrange_room` fusionne les pièces listées. `lang` vaut 5 pour le français.

`room_name` suit la double forme décrite plus haut, chaîne simple ou objet JSON encodé avec `type`
et `name`. `furniture_list` est une chaîne contenant un tableau JSON, pas un tableau. Après chaque
modification le robot émet `event.map_change.post` puis le cloud confirme sur `status/jdm/map`.

## Horaires

Les horaires vivent dans le robot, pas dans le cloud.

```json
{ "method": "service.add_order", "params": {
  "id": 286166297, "enable": 1, "day": 1, "hour": 10, "minute": 0, "repeat": 1,
  "map_id": 1000000002, "room_count": 6, "time_zone": 3600, "prefer_type": 1, "is_global": 0,
  "areas": [], "room_preference": [ …tableaux positionnels… ], "uv_switch": [[11, 0], [10, 1]]
}}
```

`service.del_order` avec `{id}` supprime. `service.get_order` renvoie un résumé
`order_data_lite` avec `total`, `enable`, `timestamp` et `md5`. `time_zone` est un décalage en
secondes, calculé par l'application, 3600 correspondant à Londres en heure d'été.

## Fuseau horaire

Deux chemins existent et aboutissent au même endroit.

- jdm : `service.set_robot_time_zone` `{"time_zone": "Europe/Amsterdam"}`.
- REST : `PUT /v1/machine/{serial}/timezone` `{"timezone": "Europe/Amsterdam"}`, que le cloud relaie
  au robot. `GET` sur la même URL lit la valeur côté cloud.

Sur le firmware `RB05PR.01.000.0436` le robot refuse les deux, avec `result: 1` en jdm et, en REST,
HTTP 424 « Failed to update JDM machine timezone … Response code: 1 ». Le refus est le même robot
au repos ou en phase de séchage. C'est un défaut du firmware, à signaler à Dyson.

## Endpoints REST en lecture

Tous vérifiés le 19 septembre 2026.

| Endpoint | Contenu |
|---|---|
| `GET /v2/app/{serial}/persistent-map-metadata` | cartes, zones, réglages par zone, ordre et sélection |
| `GET /v2/app/{serial}/persistent-maps/{mapId}` | géométrie : dimensions de la grille, zones avec points visités et segments planifiés, station, meubles, restrictions |
| `GET /v1/app/{serial}/live-maps/cleaning` | même contenu plus `robotLocation` et `cleanPath` de la tâche en cours, utilisable à tout moment |
| `GET /v1/app/{serial}/live-maps/mapping` | grille d'occupation `mapData` de `width × height` cellules, environ 300 Ko |
| `GET /v2/{serial}/clean-maps` | historique : durée, surface, batterie au départ et à l'arrivée, fautes, lien S3 présigné de 15 minutes vers un blob zlib |
| `GET /v2/{serial}/clean-maps-data/{cleanId}` | détail d'un nettoyage : tracé, zones de saleté détectées, obstacles, dimensions, bornes |
| `GET /v1/assets/devices/{serial}/ota` | état de mise à jour du firmware |
| `GET /v1/machine/{serial}/timezone` | fuseau horaire côté cloud |

Les batteries de l'historique arrivent en nombres à virgule (`91.0`), pas en entiers. La grille est
de 320 × 420 cellules de 5 cm pour un logement de 16 m sur 21 m.

### Grille d'occupation

`mapData` compte `width × height` entiers, en ordre ligne par ligne : la cellule `(cx, cy)` est
`mapData[cy × width + cx]`, avec `cx = (x − offsetX) / resolution` et `cy = (y − offsetY) / resolution`,
sans inversion d'axe. Vérifié en s'assurant que les points visités de chaque pièce tombent sur des
cellules portant son identifiant.

| Valeur | Signification |
|---|---|
| `0` | inconnu ou hors du logement |
| `255` | obstacle, mur |
| autre | identifiant de la zone à laquelle la cellule appartient, `10`, `11`, `12`… |

La grille encode donc directement la découpe en pièces. Les longues pointes qui dépassent des
murs sont des artefacts du lidar à travers les vitres.

`GET /v1/telemetry/device/{serial}/sessions` exige `start` et `end` et répond 400 quelle que soit
leur forme : il sert vraisemblablement aux purificateurs, pas au robot.

### Cartes non actives : pas de grille, et parfois une station factice

`live-maps/mapping` ne renvoie la grille d'occupation que pour la carte actuellement active : les
autres cartes du compte n'ont que la géométrie de `persistent-maps/{mapId}` (points visités et
segments par pièce, pas de grille). L'application les affiche donc différemment : la carte active
avec des surfaces de pièce pleines (grille), les autres avec seulement le tracé emprunté par le
robot lors de leur cartographie (points visités reliés en segments).

Certaines cartes non actives renvoient malgré tout un `dockLocation` factice, observé à
`(1100.0, 1100.0)` sur deux cartes qui possédaient par ailleurs des points visités bien réels
(centaines de points, dans les coordonnées attendues du logement). Rien d'autre ne distingue ces
cartes de `Appartement Rez v2`, dont la station est correcte (`-0.67, -0.02`) ; la cause exacte côté
Dyson n'est pas connue. Inclure cette valeur telle quelle dans le calcul du cadrage de la vue fait
gonfler la boîte englobante à plus de 1000 m de côté et réduit toute la géométrie réelle à moins
d'un pixel, ce qui donnait une carte visuellement vide (juste l'icône de station coincée dans un
coin). `MapScene.WorldBounds()` ignore désormais la station pour le cadrage quand elle tombe très
loin du reste des données.

## Pause, abandon et cartographie

Observés le soir du 19 septembre, y compris sur une carte sans accès à la station.

### Pause

```
-> command       {"msg": "PAUSE", "cleaningMode": "zoneConfigured", "mode-reason": "RAPP"}
-> command/jdm   service.set_room_clean  {"ctrl_value": 2, "clean_type": 0, "room_ids": []}
<- status        state FULL_CLEAN_PAUSED, fullCleanAction NONE
```

`ctrl_value` vaut 1 pour démarrer et 2 pour mettre en pause. La reprise n'a pas été observée.

### Abandon

```
-> command       {"msg": "ABORT", "cleaningMode": "zoneConfigured", "state": "FULL_CLEAN_PAUSED", "mode-reason": "RAPP"}
-> command/jdm   service.start_recharge  {}
<- status        state ABORTED avec la faute 2104, puis INACTIVE_DISCHARGING, puis INACTIVE_CHARGING une fois à quai
<- status/jdm    event.clean_record.post avec record_task_status 2
```

Le message `ABORT` transporte l'état courant du robot dans son champ `state`, `FULL_CLEAN_RUNNING`
ou `FULL_CLEAN_PAUSED` selon le moment. Un client doit donc connaître l'état avant d'abandonner.

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

La langue de la carte sert au nommage automatique des pièces. Pendant la cartographie
`persistentMapId` vaut `"0"`.

### Codes de faute et de compte rendu supplémentaires

| Code | Signification observée |
|---|---|
| `2104` | abandonné, indicateur de statut |
| `2110`, `2112` | cartographie en cours, indicateurs de statut |

`record_task_status` du compte rendu : 1 terminé, 2 abandonné par l'utilisateur, 4 abandonné après
un échec de localisation. `record_clean_mode` vaut 4 pour une cartographie.
