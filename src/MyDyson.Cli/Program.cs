using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MyDyson.Core;

namespace MyDyson.Cli;

internal static class Program
{
    private const string Usage = """
        MyDyson CLI - prototype de contrôle du robot Dyson Spot+Scrub AI via le cloud Dyson.

        Usage:
          mydyson login   [--email X] [--country CH] [--culture fr-CH]   Connexion (mot de passe + code reçu par e-mail)
          mydyson logout                                                Supprime la session enregistrée
          mydyson devices [--json]                                      Liste les appareils du compte (manifest)
          mydyson iot     --serial S                                    Affiche les credentials AWS IoT de l'appareil
          mydyson status  --serial S [--timeout 15]                     Demande et affiche l'état courant du robot
          mydyson watch   --serial S [--log fichier.jsonl] [--poll 30]  Affiche tous les messages MQTT en continu
          mydyson send    --serial S <start|pause|resume|stop|dock|state|faults>   Envoie une commande connue
          mydyson send    --serial S --json '{"msg":"..."}'             Envoie un JSON brut sur .../command
          mydyson send    --serial S --jdm service.xxx [--params '{}']  Envoie une requête JDM sur .../command/jdm
          mydyson api     <path>                                        GET authentifié brut (ex: /v3/manifest)

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
            if (!string.IsNullOrWhiteSpace(ex.ResponseBody))
                Console.Error.WriteLine(Truncate(ex.ResponseBody, 800));
            return 2;
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
            Console.WriteLine(JsonSerializer.Serialize(devices, new JsonSerializerOptions { WriteIndented = true }));
        else
            PrintDevices(devices);
        return 0;
    }

    private static async Task<int> IotAsync(Options o, CancellationToken ct)
    {
        using var api = OpenSession();
        var serial = RequireSerial(o);
        var iot = await api.GetIotCredentialsAsync(serial, ct);
        Console.WriteLine($"Endpoint   : {iot.Endpoint}");
        Console.WriteLine($"ClientId   : {iot.IoTCredentials.ClientId}");
        Console.WriteLine($"Authorizer : {iot.AuthorizerName}");
        Console.WriteLine($"TokenKey   : {iot.IoTCredentials.TokenKey}");
        Console.WriteLine($"TokenValue : {iot.IoTCredentials.TokenValue}");
        Console.WriteLine($"Signature  : {Truncate(iot.IoTCredentials.TokenSignature, 40)}...");
        return 0;
    }

    private static async Task<int> StatusAsync(Options o, CancellationToken ct)
    {
        var timeout = TimeSpan.FromSeconds(o.GetInt("timeout") ?? 15);
        await using var robot = await ConnectRobotAsync(o, ct);

        var done = new TaskCompletionSource<RobotMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        robot.MessageReceived += m =>
        {
            if (m.Kind == "CURRENT-STATE" && m.Json is JsonObject obj && obj.Count > 2)
                done.TrySetResult(m);
        };

        await robot.RequestCurrentStateAsync(ct);
        Console.Error.WriteLine("REQUEST-CURRENT-STATE envoyé, attente de la réponse...");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            var msg = await done.Task.WaitAsync(timeoutCts.Token);
            Console.WriteLine($"# {msg.Topic}");
            Console.WriteLine(Pretty(msg.Payload));
            return 0;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Fail($"Pas de CURRENT-STATE complet reçu en {timeout.TotalSeconds}s (robot hors ligne ?).");
        }
    }

    private static async Task<int> WatchAsync(Options o, CancellationToken ct)
    {
        var logPath = o.Get("log");
        var poll = o.GetInt("poll") ?? 30;
        await using var log = logPath is null ? null : new StreamWriter(logPath, append: true, Encoding.UTF8) { AutoFlush = true };

        await using var robot = await ConnectRobotAsync(o, ct);
        robot.MessageReceived += m =>
        {
            var line = $"[{m.ReceivedUtc.ToLocalTime():HH:mm:ss}] {m.Topic} {m.Kind ?? "?"}";
            Console.WriteLine(line);
            Console.WriteLine(Pretty(m.Payload));
            log?.WriteLine(JsonSerializer.Serialize(new { time = m.ReceivedUtc, topic = m.Topic, payload = m.Json ?? (JsonNode)m.Payload }));
        };
        robot.Disconnected += reason => Console.Error.WriteLine($"MQTT déconnecté: {reason}");

        Console.Error.WriteLine($"Abonné à +/{robot.Serial}/#. Ctrl+C pour quitter. Poll REQUEST-CURRENT-STATE toutes les {poll}s.");
        while (!ct.IsCancellationRequested)
        {
            if (robot.IsConnected)
                await robot.RequestCurrentStateAsync(ct);
            try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(5, poll)), ct); }
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
                case "pause": await robot.PauseAsync(ct); break;
                case "resume": await robot.ResumeAsync(ct); break;
                case "stop": await robot.StopAsync(ct); break;
                case "dock": await robot.ReturnToDockAsync(ct); break;
                case "state": await robot.RequestCurrentStateAsync(ct); break;
                case "faults": await robot.RequestCurrentFaultsAsync(ct); break;
                default: return Fail("Commande à envoyer inconnue. Voir --help.");
            }
        }

        var wait = TimeSpan.FromSeconds(o.GetInt("wait") ?? 8);
        try { await Task.Delay(wait, ct); } catch (OperationCanceledException) { }
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

    // ---- Helpers --------------------------------------------------------------

    private static DysonCloudClient OpenSession()
    {
        var s = SessionStore.Load() ?? throw new DysonAuthException("Aucune session enregistrée. Lancez d'abord `mydyson login`.");
        return new DysonCloudClient(s.Country, s.Culture) { BearerToken = s.Token };
    }

    private static async Task<RobotMqttClient> ConnectRobotAsync(Options o, CancellationToken ct)
    {
        var serial = RequireSerial(o);
        using var api = OpenSession();

        var devices = await api.GetManifestAsync(ct);
        var device = devices.FirstOrDefault(d => string.Equals(d.SerialNumber, serial, StringComparison.OrdinalIgnoreCase))
                     ?? throw new DysonApiException($"Appareil {serial} introuvable dans le compte.");
        var prefix = o.Get("prefix") ?? device.GuessTopicPrefix();

        var iot = await api.GetIotCredentialsAsync(serial, ct);
        Console.Error.WriteLine($"Connexion MQTT à {iot.Endpoint} (préfixe {prefix})...");

        var robot = new RobotMqttClient(device.SerialNumber, prefix, iot);
        robot.PrefixChanged += p => Console.Error.WriteLine($"Préfixe réel du robot: {p}");
        await robot.ConnectAsync(ct);
        Console.Error.WriteLine("MQTT connecté.");
        return robot;
    }

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
            return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (JsonException) { return json; }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

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
            if (a.StartsWith("--"))
            {
                var name = a[2..];
                var eq = name.IndexOf('=');
                if (eq >= 0) { o.Named[name[..eq]] = name[(eq + 1)..]; continue; }
                if (i + 1 < list.Count && !list[i + 1].StartsWith("--")) { o.Named[name] = list[++i]; }
                else o.Named[name] = null;
            }
            else o.Positionals.Add(a);
        }
        return o;
    }
}
