using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MyDyson.Core;

namespace MyDyson.Cli;

internal static class Program
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions IndentedNoNulls = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private const string Usage = """
        MyDyson CLI - prototype de contrôle du robot Dyson Spot+Scrub AI via le cloud Dyson.

        Usage:
          mydyson login   [--email X] [--country CH] [--culture fr-CH]   Connexion (mot de passe + code reçu par e-mail)
          mydyson logout                                                Supprime la session enregistrée
          mydyson devices [--json]                                      Liste les appareils du compte (manifest)
          mydyson iot     --serial S                                    Affiche les credentials AWS IoT de l'appareil
          mydyson status  --serial S [--timeout 15] [--json]            Demande et affiche l'état courant du robot
          mydyson watch   --serial S [--log f.jsonl] [--poll 30] [--raw] Suit le robot en continu, reconnexion automatique
          mydyson maps    --serial S                                    Cartes et zones (REST)
          mydyson map     --serial S --id MAPID                         Géométrie d'une carte (REST)
          mydyson live    --serial S                                    Position du robot et tracé en cours (REST)
          mydyson history --serial S                                    Historique des nettoyages (REST)
          mydyson clean   --serial S --id CLEANID                       Détail d'un nettoyage (REST)
          mydyson send    --serial S <zone|pause|abort|mapping|merge|dry-stop|tz|maps|props|state|start|resume|stop|faults>
                          zone: --map ID --zones 11,12 ; mapping: [--lang fr-CH] ; merge: --map ID --rooms 15,16
                          start, resume, stop et faults restent non observés dans l'application
          mydyson send    --serial S --json '{"msg":"..."}'             Envoie un JSON brut sur .../command
          mydyson send    --serial S --jdm service.xxx [--params '{}']  Envoie une requête JDM sur .../command/jdm
          mydyson api     <path>                                        GET authentifié brut (ex: /v3/manifest)
          mydyson rest    <path> --method PUT [--body '{..}']            Requête REST brute avec corps JSON
          mydyson rotate  --serial S --map-id ID --degrees 0|90|180|270   Tourne une carte (orientation, sens horaire)
          mydyson probe   --serial S [--filters a,b] [--topics a,b]     Teste les abonnements et publications autorisés
          mydyson wstest  --serial S [--client-ids a,b]                 Teste CONNECT et PUBLISH en WebSocket brut

        Options communes: --client-id X, --prefix RB05, --mqtt-log (journaux MQTTnet).
        Transport: par défaut MQTT direct sur TLS avec le jeton dans le nom d'utilisateur, comme
        l'application officielle. --websocket et --sigv4 sélectionnent les autres modes, qui
        n'accordent pas le droit de publier (voir README).

        La session (bearer token) est chiffrée avec DPAPI dans %APPDATA%\MyDyson\session.bin.
        """;

    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Console.WriteLine(Usage);
            return 0;
        }

        var opts = ParseOptions(args.Skip(1));
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "login" => await LoginAsync(opts, cts.Token),
                "logout" => Logout(),
                "devices" => await DevicesAsync(opts, cts.Token),
                "iot" => await IotAsync(opts, cts.Token),
                "status" => await StatusAsync(opts, cts.Token),
                "watch" => await WatchAsync(opts, cts.Token),
                "send" => await SendAsync(opts, cts.Token),
                "api" => await ApiAsync(opts, cts.Token),
                "rotate" => await RotateAsync(opts, cts.Token),
                "rest" => await RestAsync(opts, cts.Token),
                "probe" => await ProbeAsync(opts, cts.Token),
                "maps" => await MapsAsync(opts, cts.Token),
                "map" => await MapAsync(opts, cts.Token),
                "live" => await LiveAsync(opts, cts.Token),
                "history" => await HistoryAsync(opts, cts.Token),
                "clean" => await CleanAsync(opts, cts.Token),
                "wstest" => await WsTestAsync(opts, cts.Token),
                _ => Fail($"Commande inconnue: {args[0]}\n\n{Usage}"),
            };
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Interrompu.");
            return 130;
        }
        catch (DysonApiException ex)
        {
            Console.Error.WriteLine($"Erreur API: {ex.Message}");
            if (ex.InnerException is not null)
                Console.Error.WriteLine($"  cause: {ex.InnerException.Message}");
            if (!string.IsNullOrWhiteSpace(ex.ResponseBody))
                Console.Error.WriteLine(Truncate(ex.ResponseBody, 300));
            return 2;
        }
        catch (HttpRequestException ex)
        {
            Console.Error.WriteLine($"Erreur réseau: {ex.Message}");
            return 3;
        }
    }

    // ---- Commands -------------------------------------------------------------

    private static async Task<int> LoginAsync(Options o, CancellationToken ct)
    {
        var email = o.Get("email") ?? Prompt("E-mail du compte MyDyson: ");
        var country = (o.Get("country") ?? Prompt("Pays (code ISO, ex. CH): ")).ToUpperInvariant();
        var culture = o.Get("culture") ?? $"en-{country}";

        using var api = new DysonCloudClient(country, culture);
        await api.ProvisionAsync(ct);

        var status = await api.GetUserStatusAsync(email, ct);
        Console.WriteLine($"Compte: {status.AccountStatus}, méthode: {status.AuthenticationMethod}");
        if (!string.Equals(status.AccountStatus, "ACTIVE", StringComparison.OrdinalIgnoreCase))
            return Fail("Ce compte n'est pas actif ou n'existe pas pour ce pays.");

        var challenge = await api.BeginLoginAsync(email, ct);
        Console.WriteLine("Un code à usage unique a été envoyé par e-mail.");

        var password = PromptSecret("Mot de passe: ");
        var otp = Prompt("Code reçu par e-mail: ").Trim();

        var login = await api.CompleteLoginAsync(email, password, challenge.ChallengeId, otp, ct);
        SessionStore.Save(new StoredSession(email, country, culture, login.Account, login.Token, DateTimeOffset.UtcNow));
        Console.WriteLine($"Connecté. Session enregistrée dans {SessionStore.FilePath}");

        var devices = await api.GetManifestAsync(ct);
        PrintDevices(devices);
        return 0;
    }

    private static int Logout()
    {
        SessionStore.Delete();
        Console.WriteLine("Session supprimée.");
        return 0;
    }

    private static async Task<int> DevicesAsync(Options o, CancellationToken ct)
    {
        using var api = OpenSession();
        var devices = await api.GetManifestAsync(ct);
        if (o.Has("json"))
            Console.WriteLine(JsonSerializer.Serialize(devices, Indented));
        else
            PrintDevices(devices);
        return 0;
    }

    private static async Task<int> IotAsync(Options o, CancellationToken ct)
    {
        using var api = OpenSession();
        var serial = RequireSerial(o);

        var (status, changed) = await api.GetConnectionStatusAsync(serial, ct);
        Console.WriteLine($"Robot            : {status}" + (changed is null ? "" : $" (depuis {changed:u})"));

        var role = await api.GetIotRoleCredentialsAsync(serial, ct);
        Console.WriteLine($"Endpoint         : {role.Endpoint}  région {role.Region}");
        Console.WriteLine($"AccessKeyId      : {role.IamCredentials.AccessKeyId}");
        Console.WriteLine($"Expiration       : {role.IamCredentials.Expiration:u}");
        Console.WriteLine($"SecretAccessKey  : (masqué, {role.IamCredentials.SecretAccessKey.Length} caractères)");
        Console.WriteLine($"SessionToken     : (masqué, {role.IamCredentials.SessionToken?.Length ?? 0} caractères)");

        var iot = await api.GetIotCredentialsAsync(serial, ct);
        Console.WriteLine($"Custom authorizer: ClientId {iot.IoTCredentials.ClientId}, authorizer {iot.AuthorizerName}");

        // Diagnostic aid: the presigned URL embeds the credentials, so it is only printed on request.
        if (o.Has("ws-url"))
            Console.WriteLine(role.BuildWebSocketUri());
        return 0;
    }

    private static async Task<int> StatusAsync(Options o, CancellationToken ct)
    {
        var timeout = TimeSpan.FromSeconds(o.GetInt("timeout") ?? 15);
        await using var robot = await ConnectRobotAsync(o, ct);
        try
        {
            var state = await robot.RequestStateAsync(timeout, ct);
            if (o.Has("json"))
                Console.WriteLine(JsonSerializer.Serialize(state, IndentedNoNulls));
            else
                PrintState(state);
            return 0;
        }
        catch (TimeoutException ex)
        {
            return Fail(ex.Message + " (robot hors ligne ?)");
        }
    }

    private static void PrintState(RobotState s)
    {
        Console.WriteLine($"État        : {s.State}   batterie {s.BatteryChargeLevel}%   dock {s.DockState}   action {s.FullCleanAction}");
        Console.WriteLine($"Nettoyage   : mode {s.CurrentCleaningMode} (défaut {s.DefaultCleaningMode}), stratégie {s.CurrentCleaningStrategy}, carte {s.PersistentMapId}, durée {s.CleanDurationSeconds}s");
        Console.WriteLine($"Station     : eau chaude {s.HotWaterMop}/{s.HotWaterSwitch}, détergent {s.Detergent}, rinçage {s.BackWashType} {s.BackWashTime}min/{s.BackWashFrequency}, séchage {s.AirDryFrequency}, vidage auto {s.CollectDustOnSelfClean}");
        Console.WriteLine($"Robot       : alarme {s.Alarm}, volume {s.Volume}, voix {s.VoiceLanguage}, verrou enfant {s.ChildLock}, NPD {s.DoNotDisturbMode?.IsOn} {s.DoNotDisturbMode?.StartTime}-{s.DoNotDisturbMode?.EndTime}");
        var faults = s.ActiveFaults is { Count: > 0 } f ? string.Join(", ", f.Select(x => $"{x.FaultCode} ({x.NextActionRequired})")) : "aucune";
        Console.WriteLine($"Fautes      : {faults}" + (s.RealFaults.Any() ? "   <-- intervention requise" : ""));
        if (s.Consumables is { } c)
            Console.WriteLine("Consommables: " + string.Join(", ", c.Select(x => x.Usage is { } u ? $"{x.Type} {u}%" : $"{x.Type} recharge={x.NeedsRefill}")));
        if (s.LatestPosition is { } p)
            Console.WriteLine($"Position    : x={p.X:F2} y={p.Y:F2} angle={p.Angle:F2}");
    }

    private static async Task<int> WatchAsync(Options o, CancellationToken ct)
    {
        var logPath = o.Get("log");
        var poll = o.GetInt("poll") ?? 30;
        var raw = o.Has("raw");
        // No byte order mark: a BOM at the head of a JSON Lines file breaks standard JSON parsers.
        await using var log = logPath is null
            ? null
            : new StreamWriter(logPath, append: true, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = true };

        var serial = RequireSerial(o);
        using var api = OpenSession();
        var device = await FindDeviceAsync(api, serial, ct);

        await using var session = new RobotSession(api, device, m => Console.Error.WriteLine($"[session] {m}"));
        session.ConnectionChanged += (status, detail) => Console.Error.WriteLine($"[connexion] {status}" + (detail is null ? "" : $" ({detail})"));
        session.MessageReceived += m =>
        {
            if (raw)
            {
                Console.WriteLine($"[{m.ReceivedUtc.ToLocalTime():HH:mm:ss}] {m.Topic} {m.Kind ?? "?"}");
                Console.WriteLine(Pretty(m.Payload));
            }
            log?.WriteLine(JsonSerializer.Serialize(new { time = m.ReceivedUtc, topic = m.Topic, payload = m.Json ?? (JsonNode)m.Payload }));
        };
        if (!raw)
        {
            session.Tracker.StateChanged += st =>
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {st.State,-24} batterie {st.BatteryChargeLevel,3}%  dock {st.DockState,-16} action {st.FullCleanAction,-22}" +
                                  (st.LatestPosition is { } p ? $" pos ({p.X:F2}, {p.Y:F2})" : "") +
                                  (st.RealFaults.Any() ? "  FAUTE " + string.Join(",", st.RealFaults.Select(f => f.FaultCode)) : ""));
            session.Tracker.EventReceived += (name, json) =>
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] événement {name} {Truncate(json.ToJsonString(), 160)}");
        }

        await session.ConnectAsync(ct);
        Console.Error.WriteLine($"Écoute de {serial} (préfixe {session.TopicPrefix}). Ctrl+C pour quitter." +
                                (poll > 0 ? $" État demandé toutes les {poll}s." : " Aucune publication."));

        while (!ct.IsCancellationRequested)
        {
            if (poll > 0 && session.Status == RobotConnectionStatus.Connected)
            {
                try { await session.RefreshStateAsync(ct); }
                catch (Exception ex) when (ex is TimeoutException or InvalidOperationException) { Console.Error.WriteLine($"[état] {ex.Message}"); }
            }
            try { await Task.Delay(TimeSpan.FromSeconds(poll > 0 ? Math.Max(5, poll) : 3600), ct); }
            catch (OperationCanceledException) { break; }
        }
        return 0;
    }

    private static async Task<int> SendAsync(Options o, CancellationToken ct)
    {
        await using var robot = await ConnectRobotAsync(o, ct);

        // Print what comes back for a few seconds so the user sees the reaction.
        robot.MessageReceived += m =>
        {
            Console.WriteLine($"<- {m.Topic} {m.Kind ?? "?"}");
            Console.WriteLine(Pretty(m.Payload));
        };

        // AWS IoT closes the connection instead of rejecting a publish, so name the cause.
        var publishDenied = false;
        robot.Disconnected += _ => publishDenied = true;

        if (o.Get("json") is { } json)
        {
            Console.WriteLine($"-> {robot.CommandTopic} {json}");
            await robot.PublishRawAsync(robot.CommandTopic, json, ct);
        }
        else if (o.Get("jdm") is { } method)
        {
            var p = o.Get("params") is { } ps ? JsonNode.Parse(ps) as JsonObject : null;
            Console.WriteLine($"-> {robot.JdmCommandTopic} {method} {p?.ToJsonString()}");
            await robot.PublishJdmAsync(method, p, ct);
        }
        else
        {
            var name = o.Positionals.FirstOrDefault()?.ToLowerInvariant();
            Console.WriteLine($"-> {name}");
            switch (name)
            {
                case "start": await robot.StartGlobalCleanAsync(ct); break;
                case "pause": await robot.PauseAsync(ct: ct); break;
                case "abort":
                case "dock":
                    // ABORT carries the robot's current state, so ask for it first.
                    var current = await robot.RequestStateAsync(ct: ct);
                    await robot.AbortAsync(current.State ?? "FULL_CLEAN_RUNNING", current.CurrentCleaningMode ?? "zoneConfigured", ct);
                    break;
                case "mapping": await robot.StartMappingAsync(o.Get("lang") ?? "fr-CH", ct); break;
                case "merge":
                    await robot.MergeRoomsAsync(long.Parse(o.Get("map") ?? RequireOption("--map"), CultureInfo.InvariantCulture),
                        (o.Get("rooms") ?? RequireOption("--rooms")).Split(',').Select(int.Parse), ct: ct);
                    break;
                case "zone":
                    var map = o.Get("map") ?? RequireOption("--map");
                    var zones = (o.Get("zones") ?? RequireOption("--zones"))
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    await robot.SetCurrentMapAsync(long.Parse(map, CultureInfo.InvariantCulture), ct);
                    await robot.StartZoneCleanAsync(map, zones, ct);
                    await robot.SetRoomCleanAsync(zones.Select(int.Parse), ct: ct);
                    break;
                case "dry-stop":
                    await robot.AbortDockActionAsync("DRY_MOP", ct);
                    await robot.StartStationActionAsync(0, 2, ct);
                    break;
                case "tz":
                    await robot.SetRobotTimeZoneAsync(
                        o.Positionals.Skip(1).FirstOrDefault() ?? RequireOption("un fuseau, ex. Europe/Amsterdam"), ct);
                    break;
                case "maps": await robot.GetMapListAsync(ct); break;
                case "props": await robot.GetPropertiesAsync(ct: ct); break;
                case "resume": await robot.ResumeAsync(ct: ct); break;
                case "stop": await robot.StopAsync(ct); break;
                case "state": await robot.RequestCurrentStateAsync(ct); break;
                case "faults": await robot.RequestCurrentFaultsAsync(ct); break;
                default: return Fail("Commande à envoyer inconnue. Voir --help.");
            }
        }

        var wait = TimeSpan.FromSeconds(o.GetInt("wait") ?? 8);
        try { await Task.Delay(wait, ct); } catch (OperationCanceledException) { }

        if (publishDenied)
            return Fail("Publication refusée: le broker AWS IoT a fermé la connexion. " +
                        "Les credentials délivrés par l'API Dyson n'accordent pas iot:Publish (voir README).");
        return 0;
    }

    private static async Task<int> MapsAsync(Options o, CancellationToken ct)
    {
        using var api = OpenSession();
        foreach (var m in await api.GetMapMetadataAsync(RequireSerial(o), ct))
        {
            Console.WriteLine($"{(m.IsCurrentMap ? "*" : " ")} {m.Id}  {m.Name}");
            foreach (var z in m.Zones ?? [])
                Console.WriteLine($"     zone {z.Id,-3} {z.Name,-20} {z.Type,-12} {z.Area,6:F1} m²  ordre {z.Order}  sélection {z.IsSelected}  {z.Settings?.CleanType}/{z.Settings?.WaterLevel}");
        }
        return 0;
    }

    private static async Task<int> MapAsync(Options o, CancellationToken ct)
    {
        using var api = OpenSession();
        var mapId = o.Get("id") ?? RequireOption("--id");
        var m = await api.GetPersistentMapAsync(RequireSerial(o), mapId, ct);
        var d = m.Dimensions;
        Console.WriteLine($"Carte {m.Id}: {d?.Width}x{d?.Height} cellules de {d?.Resolution} m, origine ({d?.OffsetX}, {d?.OffsetY}), station ({m.DockLocation?.X:F2}, {m.DockLocation?.Y:F2})");
        foreach (var z in m.Zones ?? [])
            Console.WriteLine($"  zone {z.Id,-3} {z.Name,-20} {z.Area,6:F1} m²  {z.Visited?.Count ?? 0} points visités, {z.Presentation?.Count ?? 0} segments, statut {z.CleanStatus}");
        Console.WriteLine($"  {m.Furniture?.Count ?? 0} meubles, {m.Restrictions?.Count ?? 0} restrictions");
        return 0;
    }

    private static async Task<int> LiveAsync(Options o, CancellationToken ct)
    {
        using var api = OpenSession();
        var m = await api.GetLiveCleaningMapAsync(RequireSerial(o), ct);
        var r = m.RobotLocation;
        Console.WriteLine($"Carte {m.Id}, tâche depuis {m.TaskBeginTime}, robot en ({r?.X:F2}, {r?.Y:F2}) angle {r?.Angle:F2}, tracé {m.CleanPath?.Count ?? 0} points");
        foreach (var z in m.Zones ?? [])
            Console.WriteLine($"  zone {z.Id,-3} {z.Name,-20} statut {z.CleanStatus}");
        return 0;
    }

    private static async Task<int> HistoryAsync(Options o, CancellationToken ct)
    {
        using var api = OpenSession();
        foreach (var c in await api.GetCleanHistoryAsync(RequireSerial(o), ct))
            Console.WriteLine($"{c.Start?.ToLocalTime():yyyy-MM-dd HH:mm}  {c.CleanDurationMinutes,3} min  {c.AreaCleanedSquareMetres,6:F1} m²  batterie {c.StartBattery}->{c.EndBattery}%  " +
                              $"{(c.IsSpotClean == true ? "spot " : "")}{c.Faults?.Count ?? 0} faute(s)  {c.CleanId}");
        return 0;
    }

    private static async Task<int> CleanAsync(Options o, CancellationToken ct)
    {
        using var api = OpenSession();
        var id = o.Get("id") ?? RequireOption("--id");
        var c = await api.GetCleanDetailAsync(RequireSerial(o), id, ct);
        Console.WriteLine($"Nettoyage {c.CleanId} sur la carte {c.PersistentMapId}: tracé {c.CleanPath?.Count ?? 0} points, {c.Dirt?.Count ?? 0} zone(s) de saleté, {c.Obstacles?.Count ?? 0} obstacle(s), {c.Faults?.Count ?? 0} faute(s)");
        foreach (var z in c.Zones ?? [])
            Console.WriteLine($"  zone {z.Id,-3} {z.Name,-20} statut {z.CleanStatus}  {z.Visited?.Count ?? 0} points");
        if (c.Dirt is { Count: > 0 })
            Console.WriteLine("  saleté: " + Truncate(string.Join(" ", c.Dirt.Select(d => d.ToString())), 300));
        return 0;
    }

    private static async Task<int> RotateAsync(Options o, CancellationToken ct)
    {
        var serial = RequireSerial(o);
        var mapId = o.Get("map-id") ?? throw new ArgumentException("--map-id requis");
        var degrees = o.GetInt("degrees") ?? throw new ArgumentException("--degrees requis (0, 90, 180 ou 270)");
        using var api = OpenSession();
        await api.SetMapOrientationAsync(serial, mapId, degrees, ct);
        var map = await api.GetPersistentMapAsync(serial, mapId, ct);
        Console.WriteLine($"Carte {mapId} : orientation {map.Orientation}");
        return 0;
    }

    private static async Task<int> RestAsync(Options o, CancellationToken ct)
    {
        var path = o.Positionals.FirstOrDefault() ?? throw new ArgumentException("chemin requis");
        var method = new HttpMethod((o.Get("method") ?? "GET").ToUpperInvariant());
        using var api = OpenSession();
        var text = await api.SendRawAsync(method, path, o.Get("body"), ct);
        Console.WriteLine(string.IsNullOrWhiteSpace(text) ? "(réponse vide)" : Pretty(text));
        return 0;
    }

    private static async Task<int> ApiAsync(Options o, CancellationToken ct)
    {
        var path = o.Positionals.FirstOrDefault() ?? "/v3/manifest";
        using var api = OpenSession();
        var text = await api.GetRawAsync(path, ct);
        Console.WriteLine(Pretty(text));
        return 0;
    }

    /// <summary>
    /// Diagnostic: for each candidate topic, opens a fresh connection, publishes a harmless
    /// REQUEST-CURRENT-STATE and reports whether the broker kept the connection (topic allowed by
    /// the AWS IoT policy) and whether the robot answered. Timings are printed so a disconnect can
    /// be attributed to the publish rather than to process shutdown.
    /// </summary>
    private static async Task<int> ProbeAsync(Options o, CancellationToken ct)
    {
        var serial = RequireSerial(o);
        var wait = TimeSpan.FromSeconds(o.GetInt("wait") ?? 12);

        using var api = OpenSession();
        var devices = await api.GetManifestAsync(ct);
        var device = devices.FirstOrDefault(d => string.Equals(d.SerialNumber, serial, StringComparison.OrdinalIgnoreCase))
                     ?? throw new DysonApiException($"Appareil {serial} introuvable dans le compte.");
        var prefix = o.Get("prefix") ?? device.GuessTopicPrefix();

        var filters = (o.Get("filters")
                       ?? $"{prefix}/{serial}/status,{prefix}/{serial}/status/jdm,{prefix}/{serial}/#,+/{serial}/#")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        var topics = (o.Get("topics") ?? $"{prefix}/{serial}/command,{prefix}/{serial}/command/jdm")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        Console.WriteLine("== Abonnements (AWS IoT ferme la connexion quand la politique refuse un filtre) ==");
        foreach (var filter in filters)
            await RunOneAsync(filter, subscribeTo: filter, publishTo: null);

        Console.WriteLine("== Publications ==");
        await RunOneAsync("(connexion seule - référence)", subscribeTo: null, publishTo: null);
        foreach (var topic in topics)
            await RunOneAsync(topic, subscribeTo: null, publishTo: topic);

        return 0;

        async Task RunOneAsync(string label, string? subscribeTo, string? publishTo)
        {
            var endpoint = await GetMqttEndpointAsync(api, serial, o, ct);
            await using var robot = new RobotMqttClient(serial, prefix, endpoint,
                o.Has("mqtt-log") ? m => Console.Error.WriteLine($"  [mqtt] {m}") : null);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var received = 0;
            string? disconnect = null;
            robot.MessageReceived += _ => Interlocked.Increment(ref received);
            robot.Disconnected += d => disconnect ??= $"à t+{sw.Elapsed.TotalSeconds:F1}s";

            try
            {
                await robot.ConnectAsync(subscribeTo is null ? Array.Empty<string>() : new[] { subscribeTo }, ct);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  {label,-46} REFUSÉ: {Describe(ex)}");
                return;
            }

            if (publishTo is not null)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
                await robot.PublishRawAsync(publishTo, """{"msg":"REQUEST-CURRENT-STATE"}""", ct);
            }

            try { await Task.Delay(wait, ct); } catch (OperationCanceledException) { }

            var verdict = disconnect is null ? "OK, connexion maintenue" : $"REFUSÉ, déconnecté {disconnect}";
            Console.WriteLine($"  {label,-46} {verdict}, messages reçus={received}");
        }
    }

    /// <summary>
    /// Diagnostic: raw WebSocket + hand-built MQTT CONNECT, to separate a signature problem
    /// (handshake refused) from an authorization problem (no CONNACK). Tries several client ids.
    /// </summary>
    private static async Task<int> WsTestAsync(Options o, CancellationToken ct)
    {
        var serial = RequireSerial(o);
        using var api = OpenSession();
        var session = SessionStore.Load()!;

        var clientIds = (o.Get("client-ids")
                         ?? $"{Guid.NewGuid()},{serial},{session.Account},{session.Account}_{serial}")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var mode in new[] { "sigv4", "custom-authorizer" })
        {
            MqttEndpoint endpoint;
            try
            {
                endpoint = mode == "sigv4"
                    ? MqttEndpoint.FromRoleCredentials(await api.GetIotRoleCredentialsAsync(serial, ct))
                    : MqttEndpoint.FromCustomAuthorizerWebSocket(await api.GetIotCredentialsAsync(serial, ct));
            }
            catch (DysonApiException ex)
            {
                Console.WriteLine($"[{mode}] credentials indisponibles: {ex.Message}");
                continue;
            }

            foreach (var clientId in clientIds)
            {
                var result = await RawMqttProbe.TryConnectAsync(endpoint.WebSocketUrl!, clientId, ct);
                Console.WriteLine($"[{mode}] clientId={Truncate(clientId, 42),-42} {result.Describe()}");
            }

            // The client id supplied with the custom-authorizer credentials is the one Dyson expects.
            if (mode == "custom-authorizer")
            {
                var result = await RawMqttProbe.TryConnectAsync(endpoint.WebSocketUrl!, endpoint.ClientId, ct);
                Console.WriteLine($"[{mode}] clientId fourni par Dyson {Truncate(endpoint.ClientId, 20)} -> {result.Describe()}");
            }

            // Publish outside MQTTnet, to tell a library problem from an AWS policy denial.
            var pubTopic = o.Get("publish") ?? $"RB05/{serial}/command";
            var pub = await RawMqttProbe.TryPublishAsync(
                endpoint.WebSocketUrl!, endpoint.ClientId, pubTopic, """{"msg":"REQUEST-CURRENT-STATE"}""", ct);
            Console.WriteLine($"[{mode}] PUBLISH brut sur {pubTopic} -> {pub}");
        }
        return 0;
    }

    // ---- Helpers --------------------------------------------------------------

    private static DysonCloudClient OpenSession()
    {
        var s = SessionStore.Load() ?? throw new DysonAuthException("Aucune session enregistrée. Lancez d'abord `mydyson login`.");
        return new DysonCloudClient(s.Country, s.Culture) { BearerToken = s.Token };
    }

    private static async Task<Device> FindDeviceAsync(DysonCloudClient api, string serial, CancellationToken ct)
    {
        var devices = await api.GetManifestAsync(ct);
        return devices.FirstOrDefault(d => string.Equals(d.SerialNumber, serial, StringComparison.OrdinalIgnoreCase))
               ?? throw new DysonApiException($"Appareil {serial} introuvable dans le compte.");
    }

    private static async Task<RobotMqttClient> ConnectRobotAsync(Options o, CancellationToken ct)
    {
        var serial = RequireSerial(o);
        using var api = OpenSession();
        var device = await FindDeviceAsync(api, serial, ct);
        var prefix = o.Get("prefix") ?? device.GuessTopicPrefix();

        var endpoint = await GetMqttEndpointAsync(api, serial, o, ct);
        Console.Error.WriteLine($"Connexion MQTT à {endpoint.Endpoint} (préfixe {prefix}, auth {endpoint.AuthMode})...");

        var robot = new RobotMqttClient(device.SerialNumber, prefix, endpoint);
        robot.PrefixChanged += p => Console.Error.WriteLine($"Préfixe réel du robot: {p}");
        await robot.ConnectAsync(ct);
        Console.Error.WriteLine("MQTT connecté.");
        return robot;
    }

    /// <summary>
    /// Fetches broker credentials. Default: the custom-authorizer token over direct TLS, which is what
    /// the official app does. --websocket uses the same token over a WebSocket (subscribe only),
    /// --sigv4 the temporary IAM credentials over a presigned WebSocket (connect only).
    /// </summary>
    private static async Task<MqttEndpoint> GetMqttEndpointAsync(DysonCloudClient api, string serial, Options o, CancellationToken ct)
    {
        if (o.Has("sigv4"))
            return MqttEndpoint.FromRoleCredentials(await api.GetIotRoleCredentialsAsync(serial, ct), o.Get("client-id"));
        var iot = await api.GetIotCredentialsAsync(serial, ct);
        var endpoint = o.Has("websocket") ? MqttEndpoint.FromCustomAuthorizerWebSocket(iot) : MqttEndpoint.FromCustomAuthorizerTls(iot);
        return o.Get("client-id") is { } cid ? endpoint with { ClientId = cid } : endpoint;
    }

    private static string RequireOption(string what) =>
        throw new DysonApiException($"{what} est requis pour cette commande.");

    private static string RequireSerial(Options o) =>
        o.Get("serial") ?? throw new DysonApiException("--serial est requis (voir `mydyson devices`).");

    private static void PrintDevices(List<Device> devices)
    {
        if (devices.Count == 0) { Console.WriteLine("Aucun appareil."); return; }
        foreach (var d in devices)
        {
            var fw = d.ConnectedConfiguration?.Firmware?.Version ?? "-";
            var mqtt = d.ConnectedConfiguration?.Mqtt;
            Console.WriteLine($"- {d.SerialNumber}  {d.Name ?? "(sans nom)"}");
            Console.WriteLine($"    type={d.Type} model={d.Model} category={d.Category} variant={d.Variant} connexion={d.ConnectionCategory}");
            Console.WriteLine($"    firmware={fw} mqttRoot={mqtt?.MqttRootTopicLevel} broker={mqtt?.RemoteBrokerType} prefixe_devine={d.GuessTopicPrefix()}");
        }
    }

    private static string Pretty(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(doc.RootElement, Indented);
        }
        catch (JsonException) { return json; }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    /// <summary>Flattens an exception chain, since MQTTnet wraps the useful cause several levels deep.</summary>
    private static string Describe(Exception ex)
    {
        var parts = new List<string>();
        for (var e = ex; e is not null; e = e.InnerException)
            parts.Add($"{e.GetType().Name}: {e.Message}");
        return string.Join(" <- ", parts);
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }

    private static string Prompt(string label)
    {
        Console.Write(label);
        return Console.ReadLine()?.Trim() ?? "";
    }

    private static string PromptSecret(string label)
    {
        Console.Write(label);
        var sb = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); break; }
            if (key.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; continue; }
            if (!char.IsControl(key.KeyChar)) sb.Append(key.KeyChar);
        }
        return sb.ToString();
    }

    // Minimal "--name value" / "--flag" / positional parser.
    private sealed class Options
    {
        public Dictionary<string, string?> Named { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Positionals { get; } = new();
        public string? Get(string name) => Named.TryGetValue(name, out var v) ? v : null;
        public bool Has(string name) => Named.ContainsKey(name);
        public int? GetInt(string name) => int.TryParse(Get(name), out var i) ? i : null;
    }

    private static Options ParseOptions(IEnumerable<string> args)
    {
        var o = new Options();
        var list = args.ToList();
        for (var i = 0; i < list.Count; i++)
        {
            var a = list[i];
            if (a.StartsWith("--", StringComparison.Ordinal))
            {
                var name = a[2..];
                var eq = name.IndexOf('=');
                if (eq >= 0) { o.Named[name[..eq]] = name[(eq + 1)..]; continue; }
                if (i + 1 < list.Count && !list[i + 1].StartsWith("--", StringComparison.Ordinal)) { o.Named[name] = list[++i]; }
                else o.Named[name] = null;
            }
            else o.Positionals.Add(a);
        }
        return o;
    }
}
