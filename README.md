# Dyson Spot+Scrub AI pour Windows

> Ce README, comme l'essentiel du code de ce dépôt, a été rédigé par Claude (Claude Code, Anthropic),
> sur la direction d'Armand Delessert, qui a fourni les captures et vérifié chaque étape sur son robot.

Application Windows non officielle pour contrôler le robot aspirateur **Dyson Spot+Scrub AI** ([en](https://www.dyson.com/vacuum-cleaners/robot/spot-scrub-ai), [fr-CH](https://www.dyson.ch/fr_ch/aspirateurs/robot/spot-scrub-ai)) (nom interne RB05).

> **Projet indépendant, non affilié à Dyson, ni approuvé ou soutenu par Dyson.** Dyson,
> Spot+Scrub et MyDyson sont des marques du groupe Dyson, citées ici uniquement pour désigner le
> robot et l'application avec lesquels ce logiciel communique.

Le robot n'expose aucun service sur le réseau local : il n'est joignable que via le cloud Dyson
(MQTT direct sur TLS vers AWS IoT, voir « Le transport compte autant que les credentials »). Ce
dépôt reproduit donc le protocole de [l'application Android MyDyson](https://play.google.com/store/apps/details?id=com.dyson.mobile.android).

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
| Horaires, zones de restriction, meubles, pièces | fonctionne, vérifié dans l'application mobile |
| Tests unitaires | 281 tests, exécutés en CI |
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
  passage affiché. Le bouton de lancement reste inactif tant qu'aucune pièce n'est cochée ou que le
  robot n'est pas disponible, et dit pourquoi au survol.
  Une pièce cochée déplie ses réglages : type de nettoyage, mode de l'aspirateur (masqué si le type
  est « Laver » seul), et si le type inclut la serpillière, niveau d'hydratation et nombre de
  passages. Une pièce non cochée reste repliée avec un résumé d'une ligne, et peut être dépliée à la
  main pour consultation sans être sélectionnée. Le tout part au robot au lancement, et est
  enregistré côté cloud pour que l'application mobile le voie aussi.
- **Nettoyer une zone** : comme sur le téléphone, un rectangle tracé sur la carte en cliquant deux
  coins opposés, qu'on peut ensuite déplacer ou redimensionner par ses coins, nettoyé avec le type,
  la puissance, l'eau et les passages choisis. Un clic dans le vide de la carte ou Échap l'efface ; tant
  qu'elle est tracée, les pièces ne peuvent pas être cochées.
- **Station** : « Vider le collecteur » et « Laver et sécher », qui devient l'arrêt de l'action en cours.
- **Consommables** : durée de vie restante, à remplacer à zéro, comme dans l'application.
- **Notifications** : une notification Windows à la fin d'un nettoyage, ou si une pièce sélectionnée
  n'a pas pu être atteinte. Cliquer dessus ramène la fenêtre au premier plan sur l'historique.
- **Compte** : bouton « Se déconnecter » dans l'en-tête, avec confirmation ; ramène à l'écran de
  connexion sans redémarrer l'application.
- **Carte** : grille d'occupation colorée par pièce, meubles, station, position du robot, tracé en
  temps réel du nettoyage en cours, construit au fil de l'eau depuis le flux `cur_path` du robot, en
  gris là où il ne fait que se déplacer et dans la couleur du type de nettoyage de la pièce là où il
  travaille réellement. Le robot et la station sont dessinés vus de dessus, à l'échelle, et animés
  selon ce qu'ils font : lumière verte devant lui et brosses qui tournent sous lui pendant l'aspiration, rouleau qui défile pendant le
  lavage, robot à quai sur le socle, charge, vidage du collecteur, remplissage d'eau propre, lavage
  et séchage du rouleau (animations coupées si Windows les désactive). Icônes pour les obstacles et
  les taches détectés, dont la taille suit le zoom. Clic ou tape pour
  sélectionner une pièce, ou pour effacer la sélection en dehors d'une pièce ; double-clic ou
  double-tape en dehors d'une pièce pour réinitialiser le zoom. Molette ou pincement à deux doigts
  pour zoomer, glisser (souris ou un doigt) pour déplacer la vue.
  Export en PNG. Une pièce porte partout le nom qu'elle a sur le compte — contrairement à
  l'application mobile, qui affiche le libellé du type (« Salon ») et masque le nom (« Salon12 »).
  Un bouton « Affichage » regroupe ce qui ne concerne que cette fenêtre, et vaut aussi pour la carte
  de l'historique : masquer les meubles, masquer les déplacements sans nettoyage (ne reste alors que
  ce qui a réellement été nettoyé), masquer le bouton d'export, et lisser les déplacements du
  robot, qui glisse alors d'une position à la suivante au lieu de sauter. Ces choix sont retenus d'un
  lancement à l'autre, dans `%APPDATA%\MyDyson\display.json`, et ne sont jamais envoyés au robot.
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
- **Gérer les cartes** : une fenêtre à part, ouverte depuis la carte du panneau Nettoyage. Définir
  la carte active (bouton ou double clic dans la liste), renommer ou supprimer une carte, lancer
  une cartographie, et choisir son orientation (quart de tour par quart de tour, comme le bouton de
  rotation du téléphone) : la carte s'affiche alors tournée partout, ici comme sur le téléphone.
  Pour les pièces :
  renommer (un des trente types du robot, ou un nom libre ; le type s'affiche en petit à côté du
  nom quand il en diffère), diviser en cliquant les deux extrémités du trait de coupe sur la carte,
  et fusionner — un premier clic sur « Fusionner » permet de choisir plusieurs pièces, sur la carte
  ou dans la liste, un second les fusionne. Un clic dans le vide de la carte efface le choix.
  Deux autres onglets font de même pour les **zones de restriction** (« Zone à éviter »,
  « Seuil à franchir », « Lavage uniquement », « Aspirateur uniquement », chacune dans sa couleur
  sur la carte) : en ajouter une en cliquant deux coins opposés, la faire glisser pour la déplacer,
  tirer un de ses coins pour la redimensionner, changer son type, la supprimer ; et pour les
  **meubles** (les vingt-quatre de l'application mobile) : en poser un d'un clic à la taille que
  lui donne le téléphone, le faire glisser, choisir l'un des quatre sens, le retirer. Un clic sur
  la carte y choisit une zone ou un meuble plutôt qu'une pièce, et les listes sont triées par nom.
  Glisser marche à la souris comme au doigt. Le trait de coupe se cale toujours sur la grille de
  5 cm du robot ; les zones et les meubles aussi quand la case « Caler sur la grille de 5 cm » est
  cochée (retenue d'un lancement à l'autre), y compris un élément glissé qui n'y était pas. La
  grille s'affiche en zoomant, pendant un tracé ou dès qu'une zone ou un meuble est choisi. Voir
  « Limites connues » pour ce que le robot ne permet pas.
- **Horaires** : les nettoyages planifiés de la carte active, dans leur ordre dans la journée, avec
  les jours, les pièces, une durée estimée d'après l'historique et un avertissement quand un horaire
  risque de tomber pendant le précédent (le robot saute alors le second, comme le signale
  l'application mobile). Créer, modifier, activer ou désactiver d'une case, supprimer. L'éditeur
  reprend les réglages par pièce du panneau Nettoyage : ordre de passage, type de nettoyage,
  puissance, eau et passages pour chaque pièce, plus l'heure et les jours. La liste est celle que
  le cloud Dyson garde pour la carte active, la même que l'application mobile : ceux créés ici comme
  ceux du téléphone, relue dès que le robot signale un changement.
- **Réglages** : les mêmes libellés que l'application Android, en trois groupes : lavage, station,
  vocaux. Chaque réglage part au robot dans les deux dialectes.
- **Journal** : les événements notables du robot et le résultat des commandes envoyées depuis la
  fenêtre, pas chaque message MQTT. Une case, retenue d'un lancement à l'autre, enregistre tous
  les messages dans `%APPDATA%\MyDyson\messages`, un fichier JSON Lines par jour gardé 30 jours,
  pour repérer des messages non encore identifiés.

L'en-tête rappelle le numéro de série, le firmware et le compte connecté.

La connexion se rétablit seule après une coupure, avec des credentials renouvelés. L'état du robot
est réinterrogé toutes les 30 secondes, en plus de ce que le robot pousse spontanément (d'où
l'heure « mis à jour » de l'en-tête). Les données REST — cartes, pièces, historique — ne sont pas
poussées : elles sont chargées au démarrage, sur le bouton « Actualiser », et à chaque fois que la
connexion revient après une coupure, ce qui couvre le retour de veille de la machine. La carte
consultée et les pièces cochées survivent à un rechargement.

Lancée sans réseau, l'application attend : une fenêtre réessaie toutes les 10 secondes, et tout de
suite quand Windows signale le retour du réseau, ou propose de quitter. La première connexion au
robot est elle aussi réessayée tant qu'elle échoue. Les erreurs inattendues sont consignées, avec
leur détail, dans `%APPDATA%\MyDyson\erreurs.log`.

Des options de ligne de commande servent à la vérification sans écran et à la documentation
(`--manage-maps` photographie la fenêtre « Gérer les cartes », sur l'onglet `--layer 0` à `2` et la
carte `--map-id` voulus ; `--edit-schedule` l'éditeur d'horaire) :

```bash
MyDyson.App.exe --export-map carte.png
MyDyson.App.exe --screenshot ecran.png --after 15 --tab 0 --theme light --zones 11,13
MyDyson.App.exe --screenshot editeur.png --after 15 --edit-schedule
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
  `SchedulesViewModel`, `SettingsViewModel`, `JournalViewModel`), qui partagent un `RobotHub`
  (session, journal, envoi de commandes, annulation à la fermeture) et un `MapCatalog` (cartes,
  géométrie, grille).
- `MyDyson.Cli` : `login`, `devices`, `iot`, `status`, `watch`, `maps`, `map`, `live`, `history`,
  `clean`, `send`, `api`, `probe`, `wstest`.
- `tests/MyDyson.Core.Tests` : casse des requêtes, signature SigV4, nom d'utilisateur MQTT, modèle
  d'état des deux dialectes (dont le tracé `cur_path` et les propriétés jdm de la station), formes
  JSON des réponses REST, préférences de pièces, libellés de pièces et de résultats, grille
  d'occupation, messages exacts de la séquence de démarrage, attente entre deux reconnexions,
  horaires, zones et meubles relus ou écrits à l'identique des messages capturés, géométrie des
  rectangles.
- `tests/MyDyson.App.Tests` : géométrie de la scène (bornes, dock sentinelle, pièce sous un point,
  découpage du trajet par action) et les modèles de vue, branchés sur des réponses HTTP simulées :
  texte d'état et compte à rebours de séchage, numérotation des pièces choisies et sa conservation
  au rechargement, pièces d'un nettoyage passé, partage du téléchargement de détail, réglages qui
  ne renvoient pas au robot ce qu'il vient d'annoncer, éditeur d'horaires et liste retenue, onglets
  Zones et Meubles de la gestion des cartes.

L'effort de test porte d'abord sur ce qui a été retrouvé par rétro-ingénierie et qu'aucune
documentation ne permettrait de retrouver : formes exactes des messages, correspondances entre
dialectes, décodage de la grille. Le cycle de vie de la session MQTT et le routage des messages
entrants restent couverts par l'usage plutôt que par des tests, faute de coutures pour les isoler
du broker.

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
- **Supprimer une pièce** : le protocole existe (`service.delete_room`, relayé par le cloud depuis
  `PUT …/zones-definitions/{mapId}/remove-zone`, voir [docs/protocole.md](docs/protocole.md)), mais
  le robot le refuse sur le firmware `RB05PR.01.000.0436`, pour toutes les pièces essayées.
- **Horaires** : comme sur le téléphone, seuls ceux de la carte active existent ; le cloud ne rend
  que ceux-là et remplace la liste quand la carte active change. Un horaire ponctuel (sans
  répétition) n'est pas proposé : le téléphone ne le fait pas, et le robot n'a jamais été essayé
  ainsi.
- **Dessiner la forme d'une pièce** : impossible. Le robot n'offre que la division par un trait
  droit (`service.split_room`, deux points) et la fusion (`service.arrange_room`), et il recale la
  coupe sur sa propre grille d'occupation : la forme se guide par coupes et fusions successives,
  elle ne se dicte pas. Une division efface en outre le nom des deux moitiés.
- **Zones et meubles** : le robot ne reçoit jamais qu'une liste entière (`service.set_virtual_wall`,
  `service.adjust_furniture`), si bien que chaque changement renvoie toutes les autres zones ou
  tous les autres meubles. Une zone ou un meuble que l'application ne sait pas décrire (un type
  jamais observé) serait donc effacé au passage : sa liste entière reste alors en lecture seule.
  Les zones sont des rectangles droits, comme dans l'application mobile ; un meuble se pose à la
  taille par défaut du téléphone, sans redimensionnement.
- **Modifier une carte non active** : volontairement bloqué. Le robot ne modifie que la carte
  active : une carte non active modifiée devient active et prend le nom de la carte qui l'était
  (confirmé par capture le 23 septembre). La fenêtre propose de la définir comme active d'abord.
- Le débordement de la carte à travers les fenêtres vient du lidar du robot, pas du rendu. Une zone
  « Zone à éviter » posée dessus, ici ou depuis l'application mobile, empêche le robot d'y aller.
- Le réglage utilisé par pièce lors d'un nettoyage passé n'est pas récupérable (voir Historique).
- Les types de taches autres que `liquid` n'ont jamais été observés : toutes sont dessinées pareil.

## Avertissements

- Projet indépendant, non affilié à Dyson. Dyson, Spot+Scrub et MyDyson sont des marques du
  groupe Dyson.
- API non officielle, susceptible de changer sans préavis. Usage personnel.
- Les credentials AWS IoT sont de courte durée (environ 20 minutes) et apparaissent en clair dans les
  réponses de l'API. Évitez de les journaliser.

## Références

- [thoukydides/matterbridge-dyson-robot](https://github.com/thoukydides/matterbridge-dyson-robot) ([issue #46](https://github.com/thoukydides/matterbridge-dyson-robot/issues/46) : capture complète RB05)
- [UNsync3D/ha-dyson-spot-scrub](https://github.com/UNsync3D/ha-dyson-spot-scrub)
- [libdyson-wg/appapi](https://github.com/libdyson-wg/appapi)
- [Diagnostic des connexions AWS IoT](https://docs.aws.amazon.com/iot/latest/developerguide/diagnosing-connectivity-issues.html)
