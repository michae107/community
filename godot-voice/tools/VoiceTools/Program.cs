using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

try
{
    if (args.Length < 2) throw new ArgumentException("Usage: VoiceTools install/uninstall PROJECT SOURCE | mcp PROJECT METHOD JSON | kernel PROJECT editor/game (code on stdin) | call/call-game PROJECT METHOD [JSON] | status/status-game PROJECT");
    string project = Path.GetFullPath(args[1]).TrimEnd(Path.DirectorySeparatorChar);
    switch (args[0])
    {
        case "install":
            string source = Path.GetFullPath(args[2]);
            if (!File.Exists(Path.Combine(project, "project.godot"))) throw new IOException("Not a Godot project: " + project);
            string target = Path.Combine(project, "addons", "talon_voice");
            string addon = Path.Combine(source, "addon");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var info = new DirectoryInfo(target);
            if (info.LinkTarget is not null)
            {
                if (info.ResolveLinkTarget(true)?.FullName != addon)
                    throw new IOException("Existing symlink points elsewhere: " + target);
            }
            else if (Directory.Exists(target) || File.Exists(target))
                throw new IOException("Refusing to replace existing addon: " + target);
            else Directory.CreateSymbolicLink(target, addon);
            Console.WriteLine("Linked " + target + " -> " + addon);
            await Run("dotnet", ["build", Path.Combine(source, "GodotVoice.csproj"), "--nologo"], source);
            bool connected = false;
            try
            {
                await Mcp(project, "execute.code", new { code = EditorExpression("set_plugin_enabled(\"talon_voice\", false)") });
                await Mcp(project, "editor.wait_for_idle", new { });
                connected = true;
            }
            catch (Exception e) when (e is KeyNotFoundException or FileNotFoundException or WebSocketException)
            {
                Console.WriteLine("Editor not connected; building the installed addon for the next editor launch.");
            }
            var csproj = Directory.GetFiles(project, "*.csproj").Single();
            try { await Run("dotnet", ["build", csproj, "--nologo"], project); }
            catch
            {
                if (connected)
                {
                    await Mcp(project, "execute.code", new { code = EditorExpression("set_plugin_enabled(\"talon_voice\", true)") });
                    Console.Error.WriteLine("Project build failed; restored the previously built voice addon. Fix the project's compile errors and rerun install.sh.");
                }
                throw;
            }
            if (!connected)
            {
                ConfigurePlugin(project, true);
                Console.WriteLine("Enabled Talon Voice for the next Godot editor launch.");
                break;
            }
            // Keep the enablement in Godot's own serializer, not a project.godot text rewrite.
            await Mcp(project, "editor.refresh", new { });
            await Mcp(project, "execute.code", new { code = EditorExpression("set_plugin_enabled(\"talon_voice\", true)") });
            for (int attempt = 0; attempt < 40; attempt++)
            {
                try { await Call(project, "status", new { }); break; }
                catch (IOException) when (attempt < 39) { await Task.Delay(250); }
            }
            Console.WriteLine("Built, enabled, and verified Talon Voice. Use 'godot voice status' in the editor.");
            break;
        case "uninstall":
            string installed = Path.Combine(project, "addons", "talon_voice");
            var link = new DirectoryInfo(installed);
            if (link.LinkTarget is null || link.ResolveLinkTarget(true)?.FullName != Path.Combine(Path.GetFullPath(args[2]), "addon"))
                throw new IOException("Refusing to remove an addon that is not this installation's symlink.");
            try
            {
                await Mcp(project, "execute.code", new { code = EditorExpression("set_plugin_enabled(\"talon_voice\", false)") });
                await Mcp(project, "editor.wait_for_idle", new { });
            }
            catch (Exception e) when (e is KeyNotFoundException or FileNotFoundException or WebSocketException)
            {
                ConfigurePlugin(project, false);
            }
            Directory.Delete(installed); // Validated symlink only; source files remain here.
            await Run("dotnet", ["build", Directory.GetFiles(project, "*.csproj").Single(), "--nologo"], project);
            Console.WriteLine("Removed the project's addon link. The source is preserved; rerun install.sh to reinstall.");
            break;
        case "mcp":
            Console.WriteLine((await Mcp(project, args[2], JsonSerializer.Deserialize<JsonElement>(args[3]))).ToString());
            break;
        case "kernel":
            if (args[2] is not ("editor" or "game")) throw new ArgumentException("Kernel target must be editor or game.");
            using (var registry = JsonDocument.Parse(File.ReadAllText(Path.Combine(project, ".godot", "gdscript-mcp-" + args[2] + ".json"))))
            {
                var endpoint = new Uri(registry.RootElement.GetProperty("endpoint").GetString()!);
                if (endpoint.Scheme != "http" || endpoint.Host != "127.0.0.1") throw new IOException("Kernel must use loopback HTTP.");
                using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(35) };
                http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", registry.RootElement.GetProperty("token").GetString());
                using var body = new StringContent(JsonSerializer.Serialize(new { code = await Console.In.ReadToEndAsync() }), Encoding.UTF8, "application/json");
                using var response = await http.PostAsync(new Uri(endpoint, "execute"), body);
                string result = await response.Content.ReadAsStringAsync();
                Console.WriteLine(result);
                response.EnsureSuccessStatusCode();
                using var parsed = JsonDocument.Parse(result);
                if (parsed.RootElement.TryGetProperty("success", out var success) && !success.GetBoolean()) throw new IOException("Kernel cell failed; inspect its output before retrying.");
            }
            break;
        case "call": case "call-game":
            Console.WriteLine((await Call(project, args[2], args.Length > 3 ? JsonSerializer.Deserialize<JsonElement>(args[3]) : new { }, args[0] == "call-game" ? "game" : "editor")).ToString());
            break;
        case "status": case "status-game":
            Console.WriteLine(File.ReadAllText(Path.Combine(FindEndpoint(project, args[0] == "status-game" ? "game" : "editor"), "state.json")));
            break;
        default: throw new ArgumentException("Unknown operation: " + args[0]);
    }
}

catch (Exception e)
{
    Console.Error.WriteLine(e.Message);
    Environment.ExitCode = 1;
}

static string EditorExpression(string call) => "get_tree().get_root().find_children(\"*\", \"EditorPlugin\", true, false)[0].get_editor_interface()." + call;

static void ConfigurePlugin(string project, bool enabled)
{
    const string addon = "res://addons/talon_voice/plugin.cfg";
    string config = Path.Combine(project, "project.godot");
    string original = File.ReadAllText(config);
    string newline = original.Contains("\r\n") ? "\r\n" : "\n";
    var section = Regex.Match(original, @"(?ms)^\[editor_plugins\]\r?\n(?<body>.*?)(?=^\[|\z)");
    string body = section.Success ? section.Groups["body"].Value : "";
    var line = Regex.Match(body, @"(?m)^enabled\s*=\s*PackedStringArray\((?<items>[^)]*)\)[ \t]*\r?$");
    if (!line.Success && Regex.IsMatch(body, @"(?m)^enabled\s*=")) throw new IOException("Unrecognized editor_plugins setting; refusing to rewrite it.");
    var entries = Regex.Matches(line.Groups["items"].Value, "\"(?:\\\\.|[^\"\\\\])*\"")
        .Select(m => JsonSerializer.Deserialize<string>(m.Value)!).Where(p => p != addon).ToList();
    if (enabled) entries.Add(addon);
    string replacement = "enabled=PackedStringArray(" + string.Join(", ", entries.Select(p => JsonSerializer.Serialize(p))) + ")";
    body = line.Success ? body.Remove(line.Index, line.Length).Insert(line.Index, replacement) : body + newline + replacement + newline;
    string updated = section.Success
        ? original.Remove(section.Groups["body"].Index, section.Groups["body"].Length).Insert(section.Groups["body"].Index, body)
        : original + newline + "[editor_plugins]" + newline + body;
    if (!enabled)
    {
        string runtime = "res://addons/talon_voice/VoiceRuntime.cs";
        string uidFile = Path.Combine(project, "addons", "talon_voice", "VoiceRuntime.cs.uid");
        string uid = File.Exists(uidFile) ? File.ReadAllText(uidFile).Trim() : "";
        updated = Regex.Replace(updated, @"(?m)^TalonVoiceRuntime=\""\*(?<path>[^\""\r\n]+)\""\r?\n", match =>
            match.Groups["path"].Value == runtime || uid.Length > 0 && match.Groups["path"].Value == uid ? "" : match.Value);
    }
    if (updated != original) File.WriteAllText(config, updated);
}

static async Task Run(string file, string[] arguments, string workingDirectory)
{
    var start = new ProcessStartInfo(file) { WorkingDirectory = workingDirectory };
    foreach (string arg in arguments) start.ArgumentList.Add(arg);
    using var process = Process.Start(start) ?? throw new IOException("Could not start " + file);
    await process.WaitForExitAsync();
    if (process.ExitCode != 0) throw new IOException(file + " failed with exit code " + process.ExitCode);
}

static string FindEndpoint(string project, string expectedKind = "editor")
{
    string root = Path.Combine(Path.GetTempPath(), "talon-godot-" + Environment.UserName);
    var matches = new List<string>();
    if (Directory.Exists(root))
        foreach (string directory in Directory.GetDirectories(root))
        {
            try
            {
                using var state = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "state.json")));
                if (state.RootElement.GetProperty("project").GetString()?.TrimEnd('/') != project) continue;
                string actualKind = state.RootElement.TryGetProperty("kind", out var kind) ? kind.GetString()! : "editor";
                if (actualKind != expectedKind) continue;
                int pid = state.RootElement.GetProperty("pid").GetInt32();
                if (!Process.GetProcessById(pid).HasExited) matches.Add(directory);
            }
            catch (Exception e) when (e is IOException or JsonException or ArgumentException) { }
        }
    return matches.Count == 1 ? matches[0] : throw new IOException($"Expected one live Talon Voice {expectedKind} for {project}; found {matches.Count}.");
}

static async Task<JsonElement> Call(string project, string method, object parameters, string kind = "editor")
{
    string endpoint = FindEndpoint(project, kind);
    string id = Guid.NewGuid().ToString("N");
    string request = Path.Combine(endpoint, id + ".request.json");
    string response = Path.Combine(endpoint, id + ".response.json");
    File.WriteAllText(request + ".tmp", JsonSerializer.Serialize(new { id, method, args = parameters, created = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }));
    File.Move(request + ".tmp", request);
    try
    {
        for (int attempt = 0; attempt < 300; attempt++)
        {
            if (File.Exists(response))
            {
                using var data = JsonDocument.Parse(File.ReadAllText(response));
                var result = data.RootElement.Clone();
                if (result.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
                    throw new IOException(error.GetString());
                return result;
            }
            await Task.Delay(50);
        }
        throw new TimeoutException("Godot did not answer within 15 seconds: " + method);
    }
    finally
    {
        File.Delete(request);
        File.Delete(response);
    }
}

static async Task<JsonElement> Mcp(string project, string method, object parameters)
{
    string dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
    using var registry = JsonDocument.Parse(File.ReadAllText(Path.Combine(dataHome, "godot-mcp-toolkit", "projects.json")));
    var entry = registry.RootElement.GetProperty("by_path").GetProperty(project);
    string tokenPath = entry.GetProperty("token_path").GetString()!;
    if (!Path.IsPathFullyQualified(tokenPath) || !Regex.IsMatch(tokenPath, @"/addons/godot_mcp_toolkit/project_instance_[0-9a-f]{12}/mcp_token$"))
        throw new IOException("Unexpected MCP token location in the project registry.");
    string token = File.ReadAllText(tokenPath).Trim();
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
    using var socket = new ClientWebSocket();
    await socket.ConnectAsync(new Uri("ws://127.0.0.1:" + checked((int)entry.GetProperty("port").GetDouble())), timeout.Token);
    await Send(new { auth = token, version = "1.0.0" });
    var auth = await Receive();
    if (!auth.TryGetProperty("authed", out var authed) || !authed.GetBoolean()) throw new IOException("MCP authentication failed.");
    await Send(new { jsonrpc = "2.0", id = 1, method, @params = parameters });
    while (true)
    {
        var result = await Receive();
        if (!result.TryGetProperty("id", out _)) continue;
        if (result.TryGetProperty("error", out var error)) throw new IOException(error.ToString());
        var payload = result.GetProperty("result");
        if (payload.TryGetProperty("success", out var success) && !success.GetBoolean()) throw new IOException(payload.ToString());
        return payload.Clone();
    }

    async Task Send(object value) => await socket.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value)), WebSocketMessageType.Text, true, timeout.Token);
    async Task<JsonElement> Receive()
    {
        using var stream = new MemoryStream();
        var buffer = new byte[16384];
        WebSocketReceiveResult received;
        do
        {
            received = await socket.ReceiveAsync(buffer, timeout.Token);
            if (received.MessageType == WebSocketMessageType.Close) throw new IOException("MCP connection closed.");
            stream.Write(buffer, 0, received.Count);
            if (stream.Length > 16 * 1024 * 1024) throw new IOException("MCP response exceeded 16 MB.");
        } while (!received.EndOfMessage);
        using var json = JsonDocument.Parse(stream.ToArray());
        return json.RootElement.Clone();
    }
}
