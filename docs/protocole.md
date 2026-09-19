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

États observés sur un cycle complet, dans l'ordre : `INACTIVE_CHARGING`, `FULL_CLEAN_DISCOVERING`,
`FULL_CLEAN_RUNNING`, `INACTIVE_DISCHARGING`, `FULL_CLEAN_FINISHED`. L'APK en contient bien
davantage, notamment `FULL_CLEAN_PAUSED`, `FULL_CLEAN_ABORTED`, `FULL_CLEAN_NEEDS_CHARGE`,
`MAPPING_RUNNING` et la famille `DRYING_MOP`.

Le code de faute `2105` accompagné de `nextActionRequired: LOG_ONLY` est un état normal, pas une
panne. La famille `21xx` sert d'indicateur de statut.

### Autres messages classiques observés

`MAP-UPLOAD-STATUS` signale la fin d'un envoi de carte : `{ "status": "COMPLETE", "persistentMapId": "1000000003" }`.

Les autres types présents dans l'APK mais non encore observés : `STATE-CHANGE`, `CURRENT-FAULTS`,
`FAULTS-CHANGE`, `START-MAPPING`, `START-DOCK-ACTION`, `ABORT-DOCK-ACTION`, `START-DOCK-SELF-CHECK`,
`SKIP-CURRENT-ZONE`.

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

Indice 0 l'identifiant de zone, indice 1 le nom. Ce nom est soit une chaîne simple, soit un objet
JSON encodé contenant `type` et `name` pour les pièces auxquelles l'utilisateur a attribué un type.
Tout code lisant ce champ doit gérer les deux formes. Les indices suivants ne sont pas encore tous
identifiés ; l'indice 10 semble porter l'ordre de passage, l'indice 2 un réglage propre à la pièce.

### Méthodes et événements jdm observés

`service.get_map_list`, `service.get_preference`, `service.set_preference`, `service.set_cur_map`,
`service.set_room_clean`, `service.get_order`, `prop.get`, `prop.post`, `event.startClean.post`,
`event.clean_record.post`, `event.locate_fail.post`,
`event.shortcut_instruction_task_change.post`.

## Capture

Nos droits d'abonnement couvrent `+/{serial}/#`, ce qui inclut les topics de commande. Une écoute
lancée pendant que l'application officielle pilote le robot enregistre donc aussi les requêtes
qu'elle publie, et pas seulement les réponses du robot. Les deux premières captures n'ont pas
bénéficié de cela : elles ont été faites avec des filtres restreints aux topics d'état.

```bash
dotnet run --project src/MyDyson.Cli -- watch --serial XXX-XX-XXXXXXXX --poll 0 --log capture.jsonl
```

`--poll 0` évite toute publication, donc la fermeture de connexion décrite dans le README.
