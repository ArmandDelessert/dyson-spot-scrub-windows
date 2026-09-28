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

`MAP-UPLOAD-STATUS` signale la fin d'un envoi de carte : `{ "status": "COMPLETE", "persistentMapId": "1000000003" }`
après une modification de carte, ou `{ "status": "COMPLETE", "cleanId": "..." }` après un nettoyage.

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
| `station_act` | `dockState` | 0 `IDLE`, 1 `WASHING_MOP`, 2 `DRYING_MOP`, 5 vidage du collecteur (avec `dust_action: 1`) |
| `charge_state` | `state` | 1 pendant `INACTIVE_CHARGING`, 0 sinon |
| `back_to_wash` | aucun | 1 dès que le robot quitte le nettoyage pour aller laver son rouleau, 0 une fois à quai (`station_act: 1`, `status: 9`) ; le dialecte classique ne signale que le lavage lui-même |
| `work_time` | aucun | `{type, total, surplus}` en secondes : compte à rebours de l'action de la station, poussé toutes les minutes ; vu seulement pour le séchage (`type: 3`, `total: 10800` pour le réglage 3 h), `surplus` reste à 0 une fois terminé |
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

`work_time` et `back_to_wash` ne figurent pas dans la liste de propriétés que l'application demande
par `prop.get` : le robot les pousse de lui-même. Il y répond pourtant si on les demande (vérifié le
2026-09-22), ce que fait `RobotSession.RefreshPropertiesAsync` au démarrage pour connaître l'état
du séchage sans attendre la prochaine poussée.

### cur_path : le tracé en temps réel

Poussé par lots de un à une quinzaine de points pendant tout le nettoyage. Chaque lot est un
tableau à plat, pas un tableau d'objets :

```json
{ "cur_path": [103, -0.771649,3.225564,-1.557448,1, -0.762950,3.119714,-1.595241,1, 1789915756] }
```

Premier élément un identifiant de lot, puis des groupes de quatre valeurs `x, y, angle, update`
(un par point du lot), et un timestamp Unix en dernière position. `update` vaut `0` quand le robot
se contente de se déplacer et `1` quand il travaille réellement à cet endroit ; seules ces deux
valeurs ont été observées, sur un nettoyage aspirateur seul — un nettoyage lavage ou mixte pourrait
en révéler d'autres. C'est un flux, pas un état : `RobotStateTracker` l'accumule lot par lot dans
`CleanPath`, en le vidant quand l'identifiant de lot repart à une valeur plus basse (nouvelle tâche)
ou sur `event.startClean.post`. Le classique `RobotPosition` (position en direct) porte le même
champ `update` sur un point unique, d'où sa réutilisation ici plutôt qu'un nouveau type.

Côté REST, `GET /v1/app/{serial}/live-maps/cleaning` et `GET /v2/{serial}/clean-maps-data/{cleanId}`
renvoient le même tracé complet sous forme d'objets `{x, y, update}`, avec les mêmes deux valeurs
observées pour `update`. Les deux endpoints renvoient aussi, à côté du tracé : `obstacles` (liste de
points `{x, y}`, confirmée non vide — un câble ou un objet détecté) et `dirt`, confirmée non vide sur
un nettoyage de 128 minutes couvrant plusieurs pièces :

```json
{ "x": -0.53, "y": -5.25, "type": "liquid", "isUvScanOn": false }
```

Une seule valeur de `type` observée pour l'instant (`liquid`) ; l'application Android affichant
plusieurs icônes de tache différentes, d'autres valeurs existent vraisemblablement. `hazardZones`,
`groutLines` et `swingDoors` restent vides dans toutes les captures observées jusqu'ici, leur forme
reste donc inconnue.

### cleanStatus des pièces

`persistent-maps`, `live-maps` et `clean-maps-data` portent chacun un `cleanStatus` par pièce :
`CLEAN_NOT_REQUESTED` (non sélectionnée pour cette tâche), `CLEAN_COMPLETE`, `CANT_CLEAN`
(le robot a renoncé à l'atteindre, voir `event.Unable_all_area_recharge.post` plus haut), et
`CLEAN_PENDING` (sélectionnée mais son tour n'est jamais arrivé : observé sur un nettoyage de
0 minute dont le tracé ne contient qu'un seul point, la tâche s'étant arrêtée avant même de
commencer à nettoyer cette pièce). C'est la
seule donnée de résultat par pièce disponible : la liste `GET /v2/{serial}/clean-maps` (utilisée
pour l'historique) n'a ni ce champ ni aucun équivalent global de succès/échec pour toute la tâche —
seul `clean-maps-data/{cleanId}` (le détail d'un nettoyage précis) l'expose, d'où le choix de
l'afficher uniquement pièce par pièce dans le détail d'un nettoyage sélectionné, plutôt qu'en
colonne de la liste (qui obligerait à télécharger le détail, plusieurs centaines de Ko avec le
tracé complet, de chaque entrée juste pour remplir une colonne).

**`isSelected` et `settings` ne sont pas un instantané historique**, contrairement à `cleanStatus` :
présents sur les zones de `clean-maps` (liste) comme de `clean-maps-data` (détail), ils reflètent la
préférence *actuelle* de la carte, pas celle utilisée par le nettoyage consulté. Confirmé par
recoupement entre une capture et l'API pour le même nettoyage : une pièce explicitement exclue au
lancement (`room_preference` avec l'indice 8 à 0) est ensuite revenue avec `isSelected: true` dans
`clean-maps` et `clean-maps-data` sitôt que sa sélection avait changé, y compris pour ce nettoyage
déjà terminé. Son `cleanStatus`, lui, restait correctement `CLEAN_NOT_REQUESTED`. Autre écart observé
sur le même nettoyage : `settings.cleanType` valait `vacuum` pour toutes les pièces dans l'API,
alors que la capture montre plusieurs pièces lancées avec la serpillière (indice 3 du
`room_preference` à 1 ou 3). Il n'existe donc pas de source REST fiable pour retrouver a posteriori
quel type de nettoyage a été choisi pour une pièce lors d'un nettoyage précis ; seule une capture
prise au moment même le permet. L'affichage des pièces d'un nettoyage dans l'historique se base donc
sur `cleanStatus` (pièces dont le statut n'est pas `CLEAN_NOT_REQUESTED`), jamais sur `isSelected`.

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

Indices établis en croisant les `set_preference` et les `add_order` capturés avec ce qu'affichait
l'application (captures des 19 et 25 septembre, dont deux horaires réglés exprès pour couvrir
toutes les valeurs) :

| Indice | Contenu | Valeurs |
|---|---|---|
| 0 | identifiant de zone | |
| 1 | nom | chaîne simple ou objet JSON encodé, voir ci-dessus |
| 2 | renvoyé par le robot, remis à 0 par l'application | 2 pour une salle de bain, 3 pour une cuisine |
| 3 | type de nettoyage | 0 aspirer, 1 aspirer et laver, 2 laver, 3 aspirer puis laver |
| 4 | puissance d'aspiration | 0 Auto, 1 Boost, 2 Silencieux, 3 Rapide |
| 5 | niveau d'eau | 0 Faible, 1 Moyen, 2 Élevé |
| 6 | passages de lavage | 0 un passage, 1 deux passages |
| 8 | pièce retenue | 0 ou 1 |
| 10 | ordre de passage | 1, 2, 3… au démarrage ; 0, 1, 2… dans un horaire |

Les indices 7, 9 et 11 sont toujours à 0. Une pièce lavée seulement garde sa puissance à 0, une
pièce aspirée seulement son eau et ses passages à 0. `uv_switch`, qui accompagne la liste avec une
paire `[zone, valeur]` par pièce, vaut 0 pour toute pièce où la serpillière passe dans tous les
horaires capturés, et 1 pour les autres. Ce n'est pourtant pas une simple conséquence du type : au
démarrage d'un nettoyage le 19 septembre, une pièce aspirée seulement (« Cave », en Silencieux)
est partie avec 0. C'est vraisemblablement le réglage REST `isUvScanOn` de la pièce, que le
téléphone coupe quand il lave.

Côté REST, `settings.cleanType` prend `vacuum`, `mop`, `vacuumAndMop`, `vacuumThenMop` ; la
stratégie `auto`, `quick`, `quiet`, `boost` ; le niveau d'eau `low`, `medium`, `high` (l'application
connaît aussi `veryLow`, jamais proposé) ; le nombre de passages 1 ou 2.
`PUT /v2/app/{serial}/persistent-map-metadata/{mapId}` avec la liste des zones enregistre ces
réglages côté cloud ; le téléphone les recopie aussi dans les indices 3 à 6 du `set_preference`
qu'il envoie au démarrage d'un nettoyage.

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

`service.set_cur_map` fait partie de cette séquence même quand la carte visée est déjà active (donc
sans effet) : rejouer la séquence sur une carte différente de l'active revient exactement à
combiner « changer de carte active » et « démarrer un nettoyage » en une seule action, ce que
l'application mobile ne propose jamais en un clic (elle oblige à changer de carte au préalable dans
son propre sélecteur). Le robot a été entendu annoncer deux fois « Lancer un nettoyage
personnalisé » lors d'un tel démarrage sur une carte différente ; une capture prise le 20 septembre
2026 pendant ce scénario montre plusieurs `service.set_cur_map` et `service.set_room_clean` dans
une fenêtre de quelques dizaines de millisecondes, mais elle ne permet pas de distinguer les
messages du client Windows de ceux du téléphone (les deux ont pu être utilisés pendant la capture) :
la cause exacte de la double annonce reste donc non confirmée, à isoler avec une capture n'impliquant
que l'application Windows.

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
| `service.set_cur_map` | `{map_id}` | `{result: 0, result: 0}` |
| `service.rename_map` | `{map_id, map_name}` | `{result: 0}` |
| `service.del_map` | `{map_id}` | `{result: 0, result: 0}` |
| `service.rename_room` | `{map_id, room_id, room_name}` | `{map_id, map_type: 3, timestamp}` |
| `service.split_room` | `{map_id, room_id, split_points: [x1, y1, x2, y2], lang}` | `{map_id, map_type: 3, timestamp}`, ou `{result: 1}` si refusé |
| `service.arrange_room` | `{map_id, room_ids: [16, 15], lang}` | `{map_id, map_type: 3, timestamp}` |
| `service.set_virtual_wall` | `{virwall: [n, [map_id, type, 8 coordonnées], …]}` | `{map_id, map_type, timestamp}`, clés en double |
| `service.adjust_furniture` | `{timestamp, package: [1, 1], furniture_list: "…"}` | `{map_id: 0, map_type: 0, timestamp, package}` |

Capturées les 19 et 23 septembre 2026 pendant que l'application officielle modifiait de vraies
cartes. **Deux formes de réponse coexistent**, et laquelle une méthode emploie ne se devine pas :
un code `{result: 0}` (0 réussite, 1 refus) ou la carte réenregistrée `{map_id, map_type,
timestamp}`. Une division ou une fusion refusée répond, elle, `{result: 1}`. Plusieurs réponses
**répètent une clé** dans `data` (`{"result":0,"result":0}`, `map_id` deux fois) : un `JsonObject`
.NET lève une exception en y accédant, il faut les relire membre par membre.

Chaque modification est suivie d'un `event.map_change.post` dont `result` donne l'issue, puis d'un
`MAP-UPLOAD-STATUS` une fois la copie cloud à jour — c'est lui qu'il faut attendre avant de relire
la carte en REST, sous peine de relire l'ancienne. Valeurs de `result` observées :

| `result` | Signification |
|---|---|
| 3 | modification appliquée |
| 4 | division refusée (pièce trop petite, provoqué le 23 septembre) |
| 6 | fusion refusée (pièces non adjacentes, provoqué le 23 septembre) |

**Seule la carte active a jamais été modifiée.** Sur les 36 modifications capturées — depuis le
téléphone comme depuis cette application — la carte visée était toujours la carte active au moment
de l'envoi. Une capture du 23 septembre (23 h 24) montre ce qui se passe sinon : deux divisions
envoyées sur une carte non active ont chaque fois fait de cette carte la carte active, et lui ont
donné le nom de la carte qui l'était jusque-là (`get_map_list` avant et après). L'application n'autorise donc
que la carte active à être modifiée.

**Supprimer une carte** (`del_map`, capturé le 23 septembre) fonctionne sur la carte active : le
robot en active alors une autre de lui-même, et le `MAP-UPLOAD-STATUS` suivant porte sur cette
nouvelle carte active. Aucune commande jdm de suppression de *pièce* n'a été observée ; le téléphone
en a une en REST (`remove-zone`, voir « API REST de l'application mobile »).

**Diviser une pièce** envoie la pièce visée (`room_id`) et les deux extrémités du trait de coupe
(`split_points`, en mètres dans le repère de la carte), pas une forme. Elle efface le nom et le
type des deux moitiés : sur trois divisions réussies le 23 septembre, dont deux sur des pièces tout
juste renommées « Balcon », toutes les pièces issues sont revenues sans type, nommées « Pièce1 »,
« Pièce2 », « Pièce3 » — et deux s'appelaient « Pièce1 » en même temps. Il faut donc renommer les
deux moitiés après coup.

### Zones de restriction (`set_virtual_wall`)

Chaque appel envoie **la liste complète** des zones de la carte active, `[nombre, zone, zone, …]`,
et `[0]` les efface toutes : ajouter une zone suppose de renvoyer les existantes. Une zone vaut
`[map_id, type, x1, y1, x2, y2, x3, y3, x4, y4]`, un rectangle en mètres. `persistent-maps` les
restitue sous `restrictions`, `{id, points, behavior}`. Correspondance établie le 23 septembre en
recoupant les coordonnées des mêmes rectangles des deux côtés, indépendamment de l'ordre de
création :

| jdm `type` | REST `behavior` | Libellé de l'application | Effet annoncé |
|---|---|---|---|
| 2 | `keepOut` | Éviter la zone | le robot ne nettoie pas cette zone |
| 13 | `climbObstacle` | Franchir le seuil | le robot tente de franchir de petits obstacles |
| 12 | `brushBarOff` | Lavage uniquement | nettoyage sans la brosse |
| 6 | `noMop` | Aspirateur uniquement | nettoyage sans laver |

La réponse reprend dans son second `map_type` le type de la zone ajoutée en dernier. Le téléphone
donne les coins d'un rectangle droit dans l'ordre haut-gauche, bas-gauche, bas-droite, haut-droite
(y vers le haut), et renvoie les zones existantes telles que `persistent-maps` les restitue : elles
glissent d'un centimètre ou deux d'un envoi à l'autre, vraisemblablement recalées par le robot.

### Meubles (`adjust_furniture`)

`furniture_list` est une **chaîne** contenant du JSON (double encodage) :
`[[index, code, 1, x1, y1, x2, y2, x3, y3, x4, y4], …]`, liste complète à chaque appel, `"[]"` pour
tout effacer. Pas de `map_id` : l'appel porte sur la carte active. L'appel porte aussi
`timestamp` et `package: [1, 1]`, vraisemblablement le découpage d'une longue liste en plusieurs
messages (partie, nombre de parties) ; une liste de 22 meubles tient encore dans un seul.

`persistent-maps` restitue les meubles sous `furniture`, `{id, type, userDefined, points}`. L'`id`
REST est l'`index` jdm, et les points sont les mêmes, dans le même ordre. Correspondances établies
le 25 septembre en déplaçant un meuble sur une carte qui les porte tous (le téléphone renvoie alors
la liste complète), complétées par la capture du 23 septembre :

| jdm `code` | REST `type` | Meuble | Dimensions observées (m) |
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

Les dimensions sont les côtés (point 1 → 2, puis 2 → 3) des meubles tels qu'ils étaient posés :
celles par défaut du téléphone, sauf si le meuble avait été redimensionné. Les codes forment deux
séries, 15xx et 16xx, avec des trous (1517, 1521 à 1523, 1609 à 1612) qu'aucun meuble proposé par
l'application n'occupe. L'application Android contient les noms de trois autres meubles
(`lShapeCabinetLeft`, `lShapeCabinetRight`, `uShapeCabinet`) sans les proposer ; leurs codes sont
inconnus, un client ne doit jamais envoyer un code qu'il n'a pas vu.

La rotation de la carte ne passe pas par MQTT : voir « Orientation de la carte » plus bas.

`service.set_cur_map` change la carte active du compte, exactement l'action du sélecteur de carte
de l'application mobile ; c'est aussi la première étape de tout nettoyage par pièce (voir plus
haut, `CleaningSequence.StartAsync`), puisqu'on ne peut lancer un nettoyage que sur la carte active.
`service.arrange_room` fusionne les pièces listées. `lang` vaut 5 pour le français.

`room_name` suit la double forme décrite plus haut, chaîne simple ou objet JSON encodé avec `type`
et `name`. `furniture_list` est une chaîne contenant un tableau JSON, pas un tableau. Après chaque
modification le robot émet `event.map_change.post` puis le cloud confirme sur `status/jdm/map`.

## Horaires

Les horaires vivent dans le robot, pas dans le cloud, et **chacun appartient à une carte**.

```json
{ "method": "service.add_order", "params": {
  "id": 286166297, "enable": 1, "day": 1, "hour": 10, "minute": 0, "repeat": 1,
  "map_id": 1000000002, "room_count": 6, "time_zone": 3600, "prefer_type": 1, "is_global": 0,
  "areas": [], "room_preference": [ …tableaux positionnels… ], "uv_switch": [[11, 0], [10, 1]]
}}
```

- `id` est choisi par l'application (un entier aléatoire). Renvoyer `add_order` avec le même `id`
  **remplace** l'horaire : c'est ainsi que l'application le modifie, et qu'elle l'active ou le
  désactive (`enable` 1 ou 0, tout le reste renvoyé tel quel). Réponse `{result: 0}`.
- `service.del_order` `{id}` supprime.
- `day` est un masque de bits, lundi en premier : lundi 1, mardi 2, mercredi 4, jeudi 8,
  vendredi 16, samedi 32, dimanche 64. Établi le 26 septembre avec un horaire lundi + jeudi +
  dimanche (73) ; les autres horaires capturés s'y lisent de même (6 mardi + mercredi, 20
  mercredi + vendredi, 127 tous les jours).
- `repeat` vaut toujours 1 : l'application Android exige au moins un jour et ne propose pas
  d'horaire ponctuel. Une valeur 0 n'a jamais été essayée.
- `room_count` est le nombre de pièces de la carte, pas celui des pièces retenues ; toutes figurent
  dans `room_preference` (douze éléments chacune, voir `service.get_preference`), les pièces
  retenues d'abord dans leur ordre de passage compté à partir de 0, les autres ensuite. Le nom est
  envoyé en chaîne simple, l'indice 2 à 0. `uv_switch` accompagne chaque pièce : 0 si elle est
  lavée, 1 sinon, dans tous les horaires capturés.
- `is_global` reste à 0 même quand toutes les pièces sont cochées une à une, sur une carte de six
  pièces comme sur une carte de deux (captures des 25 et 26 septembre) ; l'application Android n'a
  pas de bouton « toute la maison » pour les horaires. `prefer_type` vaut toujours 1 et `areas`
  est toujours vide.
- `time_zone` est un décalage en secondes, calculé par l'application à partir du fuseau du robot :
  3600 correspond à Londres en heure d'été, le fuseau enregistré côté cloud (voir plus bas).

Après chaque changement le robot publie `prop.post` `order_total {total, enable}` : le nombre
d'horaires **de la carte active** et le nombre de ceux qui sont activés. Changer de carte active
publie aussi `order_total` pour la nouvelle carte ; revenir sur la première retrouve ses horaires
intacts.

**Le robot ne rend pas le détail des horaires, le cloud si.** `service.get_order` `{}` ne renvoie
que `order_data_lite {total, enable, timestamp, md5}`, même quand la carte active porte des
horaires. Le détail est chez le cloud Dyson, que le téléphone interroge en REST (lu dans l'APK et
vérifié le 28 septembre) :

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
- `groupId` est l'identifiant de l'horaire, celui de `add_order` et `del_order` : le téléphone a
  désactivé le 27 septembre, sous le même identifiant, un horaire créé par cette application.
- `days` compte **à partir du dimanche = 0** (lundi 1 … samedi 6), à l'inverse du masque du robot.
- `settings.zones` a la forme des pièces de `persistent-map-metadata` : les pièces à nettoyer ont
  `isSelected`, dans l'ordre de `order`, avec les réglages par pièce sous leurs noms REST.
- La liste est celle de la **carte active** : vérifié en rendant « Test 2 » active (liste vide)
  puis « Appartement » à nouveau (ses deux horaires revenus).
- Le cloud tient cette liste à jour quelle que soit l'origine de l'horaire : ceux envoyés au robot
  par cette application en `add_order` apparaissent aussi sur le téléphone.

Le téléphone écrit par `PUT` sur la même adresse, avec `{serial, enabled, events, updatedEvents}`
(la liste entière, et les `groupId` modifiés) ; le cloud relaie ensuite `add_order` ou `del_order`
au robot. Cette application écrit directement en jdm, ce qui a été vérifié. Le `md5` du résumé
change avec le contenu (celui d'une liste vide est `6a8ad4dbe08d69c27dbb2b53c97da3f8`) mais ne
correspond à aucune sérialisation évidente. `…/app/schedule.bin` sert à d'autres produits.

Quand deux horaires d'une même carte sont proches, l'application Android avertit : « Ce programme
ne commencera pas si le programme précédent est toujours en cours ». C'est donc le robot qui
arbitre, et l'application qui estime la durée d'un nettoyage. Elle dispose pour cela de
`/v2/app/{serial}/persistent-maps/{mapId}/clean-estimation`, qui renvoie `durationMinutes` mais
refuse un `GET` (405) : il attend un corps décrivant les pièces, dont la forme n'est pas connue.

Reste à vérifier sur le robot lui-même à quelle heure locale part un horaire : l'application
envoie le décalage de Londres (3600 en été), le fuseau du cloud, alors que le logement des captures
vit à l'heure d'Europe centrale (7200 en été).

## Fuseau horaire

Deux chemins existent et aboutissent au même endroit.

- jdm : `service.set_robot_time_zone` `{"time_zone": "Europe/Amsterdam"}`.
- REST : `PUT /v1/machine/{serial}/timezone` `{"timezone": "Europe/Amsterdam"}`, que le cloud relaie
  au robot. `GET` sur la même URL lit la valeur côté cloud.

Sur le firmware `RB05PR.01.000.0436` le robot refuse les deux, avec `result: 1` en jdm et, en REST,
HTTP 424 « Failed to update JDM machine timezone … Response code: 1 ». Le refus est le même robot
au repos ou en phase de séchage. C'est un défaut du firmware, à signaler à Dyson.

## API REST de l'application mobile pour le RB05

Le code du téléphone pour ce robot ne contient **aucun** nom de méthode jdm : il appelle l'API
REST, et c'est le cloud qui relaie au robot les `service.*` que les captures montrent (leurs
`msgId` sont ceux du cloud). Cette application parle jdm directement, ce qui marche aussi ; ces
adresses sont celles qu'emploierait un client qui voudrait faire exactement comme le téléphone.

Relevé le 28 septembre 2026 dans l'interface Retrofit de l'APK 6.4.26360 : les annotations y sont
renommées (`c82.f` GET, `c82.p` PUT, `c82.b` DELETE, `c82.o` POST, `c82.s` paramètre de chemin,
`c82.t` paramètre de requête, `c82.a` corps), les champs JSON gardent leurs noms Gson.

| Verbe | Adresse | Corps | Rôle |
|---|---|---|---|
| GET | `/v2/app/{serial}/persistent-map-metadata` | | cartes, pièces et réglages |
| PUT | `/v2/app/{serial}/persistent-map-metadata/{mapId}` | liste des pièces | réglages, sélection, ordre |
| GET | `/v2/app/{serial}/persistent-maps/{mapId}?isPreview=` | | géométrie |
| PUT | `/v2/app/{serial}/persistent-maps/{mapId}` | `{name, zone, orientation, isCurrentMap, furniture}` | renommer, activer, tourner (vérifié), meubles |
| DELETE | `/v2/app/{serial}/persistent-maps/{mapId}` | | supprimer la carte |
| PUT | `/v2/app/{serial}/zones-definitions/{mapId}/divide-zone` | `{threshold: {zoneId, start, end}, language}` | diviser une pièce |
| PUT | `/v2/app/{serial}/zones-definitions/{mapId}/merge-zones` | `{zoneIds, language}` | fusionner des pièces |
| PUT | `/v2/app/{serial}/zones-definitions/{mapId}/remove-zone` | `{isPreview, zoneId}` | **supprimer une pièce** |
| PUT | `/v2/app/{serial}/restrictions-definitions/{mapId}` | liste de restrictions | zones de restriction |
| POST | `/v2/app/{serial}/persistent-maps/{mapId}/clean-estimation` | `{spotZones, zones}` | durée estimée |
| GET/PUT | `/v1/unifiedscheduler/{serial}/events?productType=804` | `{serial, enabled, events, updatedEvents}` | horaires (voir Horaires) |

Dans `zones-definitions`, une « zone » est une **pièce** (le vocabulaire REST des cartes, où les
pièces sont `zones`) ; les zones de restriction sont les `restrictions`. `remove-zone` supprime donc
une pièce, ce que le téléphone propose (il a un écran d'erreur « suppression de la pièce »), avec
`isPreview` qui demande vraisemblablement d'abord un aperçu du résultat. Jamais essayé depuis cette
application. `divide-zone` et `merge-zones` portent la langue en toutes lettres (`language`) là où
le jdm porte un code numérique (`lang`, 5 pour le français) : c'est le cloud qui traduit.

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

### Orientation de la carte

La rotation d'une carte ne passe pas par le robot : c'est un réglage d'affichage tenu par le cloud.
`persistent-maps/{mapId}` la rend sous `orientation`, en degrés **dans le sens des aiguilles d'une
montre** (le téléphone n'a qu'un bouton, un quart de tour horaire à chaque appui). Elle s'écrit
comme le fait le téléphone (lu dans l'APK, vérifié le 28 septembre sur « Test 2 », active ou non) :

```
PUT /v2/app/{serial}/persistent-maps/{mapId}
{ "orientation": 180 }
```

Seules 0, 90, 180 et 270 sont admises (l'application le vérifie elle-même : « orientationDegrees
must be 0, 90, 180, or 270 »). Le corps accepte aussi `name`, `zone {id, name, type}`,
`isCurrentMap` et `furniture`, tous facultatifs.

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
