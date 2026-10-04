<p align="center">
  <img src="assets/logo.svg" alt="Logo de DySS Cockpit : le robot vu de dessus, sa lumière verte allumée" width="160">
</p>

# DySS Cockpit

**DySS Cockpit** est une application Windows non officielle pour piloter le robot aspirateur et laveur **Dyson Spot+Scrub AI** (nom interne RB05) : nettoyer des pièces ou une zone, suivre le robot en direct sur la carte, gérer cartes, pièces, zones et horaires, consulter l'historique. Son nom vient de **Dy**son **S**pot+**S**crub.

Le robot sur le site de Dyson : [présentation](https://www.dyson.ch/fr_ch/aspirateurs/robot/spot-scrub-ai) et [fiche technique](https://www.dyson.ch/fr_ch/aspirateurs/robot/spot-scrub-ai/noir) en français ; [présentation](https://www.dyson.com/vacuum-cleaners/robot/spot-scrub-ai) et [fiche technique](https://www.dyson.com/vacuum-cleaners/robot/spot-scrub-ai/black) en anglais.

> **Projet indépendant, non affilié à Dyson, ni approuvé ou soutenu par Dyson.** Dyson, Spot+Scrub et MyDyson sont des marques du groupe Dyson, citées ici uniquement pour désigner le robot et l'application avec lesquels ce logiciel communique.

> Ce README, comme l'essentiel du code de ce dépôt, a été rédigé par Claude (Claude Code, Anthropic), sous la direction d'Armand Delessert, qui a fourni les captures et vérifié chaque étape sur son robot.

## Sommaire

- [Fonctionnalités](#fonctionnalités)
- [Installation](#installation)
- [Limites connues](#limites-connues)
- [Comment ça marche](#comment-ça-marche)
- [Développement](#développement)
- [Avertissements](#avertissements)
- [Références](#références)

## Fonctionnalités

L'application parle au robot comme l'application mobile MyDyson, avec le même compte. Tout ce qui est modifié ici se retrouve sur le téléphone, et inversement. Elle a l'apparence de Windows 11 (Mica, contrôles Fluent), suit son thème clair ou sombre, et range ses pages dans un volet de navigation : tableau de bord, historique, horaires, réglages, journal et gestion des cartes.

### Tableau de bord

- **État du robot** : état en clair, batterie, fautes, et ce que fait la station (vidage, lavage, séchage avec le temps restant). Le retour à la station pour laver le rouleau en cours de nettoyage est signalé, ce que l'application officielle ne fait pas. Boutons « Pause » / « Reprendre » et « Retour à la station ».
- **Nettoyer des pièces** : choix de la carte (et de la carte active du compte), pièces cochées dans la liste ou cliquées sur la carte, dans l'ordre de passage voulu. Chaque pièce a ses réglages : type de nettoyage (aspirer, laver, ou les deux), mode d'aspiration, niveau d'eau et nombre de passages. Ils sont enregistrés sur le compte, comme sur le téléphone.
- **Nettoyer une zone** : un rectangle tracé sur la carte en cliquant deux coins opposés, puis déplacé ou redimensionné à la souris, nettoyé avec son propre type, sa puissance, son eau et ses passages. Un clic dans le vide de la carte ou la touche Échap l'efface.
- **Station** : « Vider le collecteur » et « Laver et sécher », qui devient l'arrêt de l'action en cours.
- **Consommables** : durée de vie restante de chaque pièce d'usure.
- **Notifications Windows** à la fin d'un nettoyage, ou quand une pièce n'a pas pu être atteinte.

### Carte

- Grille d'occupation colorée par pièce, meubles, obstacles et taches détectés, dans l'orientation choisie pour la carte.
- Le robot et la station dessinés vus de dessus, à l'échelle, et animés selon ce qu'ils font : lumière verte et brosses qui tournent pendant l'aspiration, rouleau qui défile pendant le lavage ; à la station, charge, vidage du collecteur, remplissage d'eau propre, lavage et séchage du rouleau.
- Le tracé du nettoyage en cours, en temps réel : en gris là où le robot ne fait que se déplacer, dans la couleur du type de nettoyage là où il travaille. Comme sur le téléphone, il quitte la carte une fois le robot revenu et la station a fini son travail ; le nettoyage passe alors dans l'historique.
- Zoom à la molette ou au pincement, déplacement en glissant, à la souris comme au doigt ; export en PNG.
- Un menu « Affichage », propre à cet ordinateur : masquer les meubles, masquer les déplacements sans nettoyage, masquer le bouton d'export, lisser les déplacements du robot.

Une pièce porte partout son nom sur le compte, là où l'application mobile n'affiche que son type (« Salon » au lieu de « Salon12 »).

### Historique

Chaque nettoyage avec sa date, sa durée, sa carte, les pièces réellement nettoyées, la surface, la batterie et les fautes. Le nettoyage choisi s'affiche sur sa propre carte, avec son trajet et ses obstacles, et le résultat pièce par pièce (terminée, injoignable…). La liste se met à jour d'elle-même dès que le robot signale la fin d'un nettoyage.

### Horaires

Les nettoyages planifiés de la carte active, triés par jour de la semaine puis par heure, avec leurs jours, leurs pièces, une durée estimée d'après l'historique, et un avertissement quand un horaire risque de commencer avant la fin du précédent. Création, modification avec les mêmes réglages par pièce que le tableau de bord, activation, suppression. Les horaires créés sur le téléphone apparaissent ici, et inversement.

### Gérer les cartes

Une page dédiée, d'où le bouton retour de la barre de titre ramène à la précédente, pour :

- **les cartes** : définir la carte active, renommer, supprimer, lancer une cartographie, tourner la carte par quarts de tour ;
- **les pièces** : renommer (l'un des trente types du robot ou un nom libre), diviser d'un trait tracé sur la carte, fusionner plusieurs pièces ;
- **les zones de restriction** (« Zone à éviter », « Seuil à franchir », « Lavage uniquement », « Aspirateur uniquement ») : ajouter, déplacer, redimensionner, changer de type, supprimer ;
- **les meubles** (les vingt-quatre de l'application mobile) : poser, déplacer, orienter, retirer.

Les tracés peuvent se caler sur la grille de 5 cm du robot.

### Réglages et journal

- **Réglages** du robot, avec les libellés de l'application Android : lavage, station, voix.
- **Journal** des événements du robot et des commandes envoyées. Une option enregistre aussi tous les messages échangés avec le robot, un fichier par jour, pour l'analyse du protocole.

La connexion se rétablit seule après une coupure de réseau ou une mise en veille. Lancée hors ligne, l'application attend le retour du réseau.

## Installation

1. Télécharger le zip de la [dernière version](https://github.com/ArmandDelessert/dyson-spot-scrub-windows/releases/latest), pour processeur x64 ou ARM64.
2. Le décompresser et lancer `DyssCockpit.exe`, dans le dossier. Rien d'autre à installer : .NET et le Windows App SDK sont embarqués.
3. Se connecter avec son compte MyDyson (e-mail, mot de passe, puis code reçu par e-mail). Le robot doit déjà être enregistré dans l'application mobile.

L'exécutable n'est pas signé : au premier lancement, Windows SmartScreen demande une confirmation (« Informations complémentaires », puis « Exécuter quand même »).

La session (chiffrée pour le compte Windows), les préférences et les journaux sont enregistrés dans `%APPDATA%\DySS Cockpit`. Aucune donnée n'est envoyée ailleurs qu'au cloud Dyson.

Configuration requise : Windows 10 ou 11, et un robot Dyson Spot+Scrub AI connecté à Internet.

## Limites connues

- **Horaires décalés** : le robot déclenche les horaires à l'heure de Pékin (UTC+8), quel que soit le fuseau du compte : un horaire à 10:00 part à 04:00 en Suisse en été. Le firmware `RB05PR.01.000.0436` refuse de changer de fuseau, que la demande lui parvienne directement ou par l'API qu'utilise le téléphone.
- **Horaires d'une seule carte** : comme sur le téléphone, seuls ceux de la carte active existent.
- **Supprimer une pièce** : le robot refuse la commande sur le firmware actuel. Pour faire disparaître une pièce, il faut la fusionner avec une voisine.
- **Forme des pièces** : elle ne se dessine pas, elle se guide par divisions et fusions successives, que le robot recale sur sa propre grille. Une division efface le nom des deux moitiés.
- **Carte non active** : seule la carte active peut être modifiée ; une carte non active modifiée deviendrait active et prendrait le nom de l'autre. L'application propose donc de la rendre active d'abord.
- **Zones et meubles** : des rectangles droits, comme dans l'application mobile ; un meuble garde la taille que lui donne le téléphone. Si une carte contient une zone ou un meuble d'un type inconnu, sa liste reste en lecture seule, pour ne pas l'effacer en renvoyant les autres.
- **Nettoyage complet** : pas de bouton « toute la maison », seulement par pièces ou par zone. La commande existe dans la ligne de commande.
- **Historique** : les réglages utilisés pièce par pièce lors d'un nettoyage passé ne sont pas récupérables ; le cloud ne garde que les réglages actuels.
- Le débordement de la carte à travers les fenêtres vient du lidar du robot. Une « Zone à éviter » posée dessus empêche le robot d'y aller.

## Comment ça marche

Le robot n'expose aucun service sur le réseau local : il n'est joignable que par le cloud Dyson. Le projet reproduit le protocole de [l'application Android MyDyson](https://play.google.com/store/apps/details?id=com.dyson.mobile.android), retrouvé par décompilation et par captures de messages, et documenté en détail dans [docs/protocole.md](docs/protocole.md).

- **API REST** (`appapi.cp.dyson.com`) : compte, appareils, cartes, historique, horaires.
- **MQTT** vers AWS IoT : état du robot en direct et commandes, sur les topics `RB05/{série}/status`, `…/status/jdm`, `…/command` et `…/command/jdm`. Deux dialectes y coexistent : le format Dyson classique (`{"msg":"START", …}`) et une couche JSON-RPC nommée jdm (`{"method":"service.set_room_clean", …}`).

| Étape | Requête |
|---|---|
| Provisioning (obligatoire avant tout) | `GET /v1/provisioningservice/application/Android/version` |
| Statut du compte | `POST /v3/userregistration/email/userstatus?country=CH` |
| Début de connexion, envoi du code | `POST /v3/userregistration/email/auth?country=CH&culture=fr-CH` |
| Fin de connexion, bearer token | `POST /v3/userregistration/email/verify?country=CH&culture=fr-CH` |
| Appareils du compte | `GET /v3/manifest` |
| Robot en ligne ou non | `GET /v1/messageprocessor/devices/{serial}/connectionstatus` |
| Jeton du custom authorizer | `POST /v2/authorize/iot-credentials` `{"Serial": "..."}` |
| Credentials IAM temporaires | `POST /v1/authorize/iot-role-credentials` `{"Serial": "..."}` |

**Attention à la casse** : l'API accepte `{"Serial": ...}` mais répond HTTP 400 à `{"serial": ...}`. Le sérialiseur JSON ne doit appliquer aucune politique de renommage.

### Le transport compte autant que les credentials

Ce point a coûté une journée et n'est documenté nulle part ailleurs. Le jeton du custom authorizer donne des droits différents selon la façon dont il est présenté au broker :

| Transport | Où passe le jeton | Droits accordés |
|---|---|---|
| MQTT sur WebSocket | chaîne de requête de l'URL | connexion et abonnement, **publication refusée** |
| WebSocket présigné SigV4 (`iot-role-credentials`) | signature de l'URL | connexion seule |
| **MQTT direct sur TLS, port 443, ALPN `mqtt`** | **nom d'utilisateur MQTT** | **tout, y compris la publication** |

Le troisième mode est celui de l'application officielle (classe `y50.e`, construite sur `AwsIotMqttConnectionBuilder` du SDK AWS IoT). Le nom d'utilisateur MQTT a cette forme, sans mot de passe, avec un identifiant client fourni par Dyson :

```
?x-amz-customauthorizer-name=NOM&x-amz-customauthorizer-signature=SIGNATURE_ENCODEE&token=VALEUR
```

Le Lambda authorizer de Dyson ne renvoie la politique complète que par ce canal : les projets communautaires qui passent par WebSocket sont donc limités à la lecture sans le savoir. Autre détail : la vérification de révocation du certificat AWS doit être désactivée, son serveur n'étant pas joignable depuis certains réseaux ; le certificat lui-même reste validé.

## Développement

Prérequis : Windows 10 ou 11 et le [SDK .NET 10](https://dotnet.microsoft.com/download).

```bash
dotnet build
dotnet test
dotnet run --project src/Dyss.App
```

### Structure

| Projet | Contenu |
|---|---|
| `src/Dyss.Core` | Bibliothèque sans interface : client REST (`DysonCloudClient`), client MQTT (`RobotMqttClient`), session longue durée avec reconnexion (`RobotSession`), modèle d'état des deux dialectes, cartes et grille d'occupation, séquences de commandes (`CleaningSequence`, `SpotCleanSequence`), horaires, session chiffrée par DPAPI (`SessionStore`). |
| `src/Dyss.Presentation` | Tout ce que montrent les fenêtres, sans dépendre d'un framework d'interface : un modèle de vue par onglet, qui partagent un `RobotHub` (session, journal, envoi de commandes, dialogues) et un `MapCatalog` (cartes et géométrie) ; la géométrie de la carte (`MapGeometry`, `RobotMarkerLayout`), ses couleurs, et ses gestes (`MapInteraction` : zoom, déplacement, clics, tracés). L'interface fournit le fil d'interface (`IUiDispatcher`) et les dialogues (`IDialogService`). |
| `src/Dyss.App` | Application WinUI 3 (`DyssCockpit.exe`) : la fenêtre et ses vues, les dialogues (`ContentDialog`), les notifications, le contrôle `MapView` ; `MapRenderer` et `RobotMarkers` dessinent la carte, le robot et la station avec Win2D. |
| `src/Dyss.Cli` | Ligne de commande `dyss`, pour explorer le protocole. |
| `tests/` | Tests xUnit de `Dyss.Core` et de `Dyss.Presentation`, exécutés en CI sans bureau. |

Les tests portent d'abord sur ce qui a été retrouvé par rétro-ingénierie et qu'aucune documentation ne permettrait de retrouver : formes exactes des messages, correspondances entre les deux dialectes, décodage de la grille. Les modèles de vue sont testés sur des réponses HTTP simulées.

L'interface est en WinUI 3, avec le Windows App SDK 1.8 embarqué dans l'application (aucun runtime à installer) ; elle se compile avec le seul SDK .NET, sur x64 comme sur ARM64. Seul ce projet dépend de WinUI : les modèles de vue, la géométrie de la carte et ses gestes sont dans `Dyss.Presentation`, testés sans interface.

### Ligne de commande

```bash
dotnet run --project src/Dyss.Cli -- login --country CH --culture fr-CH
dotnet run --project src/Dyss.Cli -- devices
dotnet run --project src/Dyss.Cli -- status --serial XXX-XX-XXXXXXXX
dotnet run --project src/Dyss.Cli -- watch  --serial XXX-XX-XXXXXXXX --log capture.jsonl
dotnet run --project src/Dyss.Cli -- send   --serial XXX-XX-XXXXXXXX zone --map ID --zones 11
dotnet run --project src/Dyss.Cli -- history --serial XXX-XX-XXXXXXXX
```

`dyss` sans argument liste toutes les commandes. `watch` enregistre aussi les commandes publiées par l'application mobile : c'est ainsi que se constituent les captures de référence, en pilotant le robot depuis le téléphone.

### Options de diagnostic de l'application

Pour vérifier le rendu sans écran et produire les illustrations :

```bash
DyssCockpit.exe --export-map carte.png
DyssCockpit.exe --export-icon app.ico
DyssCockpit.exe --screenshot ecran.png --after 15 --tab 0 --theme light --zones 11,13
DyssCockpit.exe --screenshot editeur.png --after 15 --edit-schedule
DyssCockpit.exe --screenshot connexion.png --after 3 --login
```

`--tab` choisit la page (`0` tableau de bord, `1` historique, `2` horaires, `3` réglages, `4` journal). `--manage-maps` photographie la gestion des cartes (onglet `--layer 0` à `2`, carte `--map-id`). `--login` montre la connexion sans toucher à la session enregistrée. `--export-icon` régénère l'icône à partir du dessin du robot ; [assets/logo.svg](assets/logo.svg) en est la version vectorielle.

### Outillage

- `global.json` épingle le SDK. `Directory.Build.props` active les analyseurs .NET et traite tout avertissement comme une erreur ; il porte aussi le nom du produit et la version. `Directory.Packages.props` centralise les versions des paquets.
- `.github/workflows/ci.yml` compile et lance les tests à chaque push.
- `.github/workflows/release.yml` publie une version à chaque tag.

### Publier une version

Le numéro de version vient du tag Git. Pousser un tag `vX.Y.Z` (ou `vX.Y.Z-beta.1` pour une pré-version) compile cette version, lance les tests, puis crée la release GitHub avec l'application autonome pour x64 et pour ARM64, chacune dans un zip, et leurs empreintes SHA-256 :

```bash
git tag v1.0.0
git push origin v1.0.0
```

Une compilation locale porte la version `0.0.0-dev`.

## Avertissements

- API non officielle, susceptible de changer sans préavis. Usage personnel.
- Les credentials AWS IoT sont de courte durée (environ 20 minutes) mais apparaissent en clair dans les réponses de l'API : évitez de les journaliser.
- Ne publiez pas vos captures telles quelles : elles contiennent le numéro de série du robot, les noms de vos cartes et de vos pièces.

## Références

- [thoukydides/matterbridge-dyson-robot](https://github.com/thoukydides/matterbridge-dyson-robot), dont l'[issue #46](https://github.com/thoukydides/matterbridge-dyson-robot/issues/46) contient une capture complète d'un RB05
- [UNsync3D/ha-dyson-spot-scrub](https://github.com/UNsync3D/ha-dyson-spot-scrub)
- [libdyson-wg/appapi](https://github.com/libdyson-wg/appapi)
- [Diagnostic des connexions AWS IoT](https://docs.aws.amazon.com/iot/latest/developerguide/diagnosing-connectivity-issues.html)

## Licence

[MIT](LICENSE)
