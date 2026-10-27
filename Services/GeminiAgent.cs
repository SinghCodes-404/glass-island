using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;

namespace GlassIsland.Services;

public sealed record ToolCall(string Id, string Name, JsonObject Args);

public sealed class GeminiException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>
/// Minimal client for the Gemini Interactions API (REST + server-sent events) with a function-calling loop.
/// Conversation state lives server-side: each turn continues from <c>previous_interaction_id</c>.
/// </summary>
public sealed class GeminiAgent
{
    const string Endpoint = "https://generativelanguage.googleapis.com/v1beta/interactions";
    static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    public string Model { get; set; } = "gemini-3.8-flash";

    /// <summary>Set GI_TRACE=1 to log every stream event (never the key).</summary>
    public static bool Debug { get; set; }

    string? _previousId;
    bool _sendThinkingLevel = true;

    /// <summary>Start a new conversation.</summary>
    public void Reset() => _previousId = null;

    /// <summary>
    /// Sends <paramref name="userText"/>, streams the reply through <paramref name="onText"/> and runs any tools the
    /// model asks for via <paramref name="execute"/>, feeding results back until the model answers in text.
    /// </summary>
    public async Task RunAsync(string apiKey, string userText, string systemInstruction, JsonArray tools,
        Func<ToolCall, Task<JsonNode?>> execute, Action<string> onText, CancellationToken ct)
    {
        JsonNode input = JsonValue.Create(userText)!;
        for (int round = 0; round < 8; round++)
        {
            var calls = await StreamWithRetryAsync(apiKey, input, systemInstruction, tools, onText, ct);
            if (calls.Count == 0) return;

            var results = new JsonArray();
            foreach (var call in calls)
            {
                JsonNode? result;
                try { result = await execute(call); }
                catch (Exception ex) { result = new JsonObject { ["error"] = ex.Message }; }
                results.Add(new JsonObject
                {
                    ["type"] = "function_result",
                    ["name"] = call.Name,
                    ["call_id"] = call.Id,
                    ["result"] = new JsonObject
                    {
                        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = result?.ToJsonString() ?? "{}" }),
                    },
                });
            }
            input = results;
        }
    }

    /// <summary>Used, in order, when the chosen model is overloaded (HTTP 503) or rate-limited.</summary>
    public static readonly string[] Fallbacks = ["gemini-3.6-flash", "gemini-3.5-flash-lite"];

    /// <summary>
    /// "High demand" (503), rate limits (429) and server errors are usually momentary: retry the same model after
    /// ~1 s and ~3 s, then fall back to lighter models. Only retries if nothing has been shown to the user yet.
    /// </summary>
    /// <summary>Models that just reported overload are skipped for a while, so every message doesn't wait on them.</summary>
    static readonly Dictionary<string, DateTime> OverloadedUntil = [];

    async Task<List<ToolCall>> StreamWithRetryAsync(string apiKey, JsonNode input, string systemInstruction, JsonArray tools,
        Action<string> onText, CancellationToken ct)
    {
        string preferred = Model;
        var models = new[] { preferred }.Concat(Fallbacks.Where(m => m != preferred))
            .Where(m => !OverloadedUntil.TryGetValue(m, out var until) || until < DateTime.Now).ToList();
        if (models.Count == 0) models = [preferred, .. Fallbacks.Where(m => m != preferred)];   // all marked busy: try anyway
        bool shown = false;
        void Show(string t) { shown = true; onText(t); }
        try
        {
            for (int m = 0; m < models.Count; m++)
            {
                var model = models[m];
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    Model = model;
                    var started = DateTime.Now;
                    try { return await StreamOnceAsync(apiKey, input, systemInstruction, tools, Show, ct); }
                    catch (GeminiException ex) when (!shown && ex.Status is 429 or 500 or 503 or 504 && (m < models.Count - 1 || attempt == 0))
                    {
                        App.Log($"Gemini {ex.Status} on {model}; {(m < models.Count - 1 ? "trying the next model" : "retrying")} ({ex.Message})");
                        // A quick refusal is worth one retry; a slow one (overload mid-stream) means: move on now.
                        bool quick = DateTime.Now - started < TimeSpan.FromSeconds(4);
                        if (ex.Status == 503) OverloadedUntil[model] = DateTime.Now.AddMinutes(10);
                        if (!quick || attempt == 1) break;
                        await Task.Delay(1200, ct);
                    }
                    catch (GeminiException ex) when (!shown && model != preferred && ex.Status == 400 && _previousId != null)
                    {
                        // A different model may not continue this conversation: start a fresh one rather than fail.
                        _previousId = null;
                        return await StreamOnceAsync(apiKey, input, systemInstruction, tools, Show, ct);
                    }
                }
            }
            throw new GeminiException(503, "All Gemini models are busy right now.");
        }
        finally { Model = preferred; }
    }

    sealed class StepState
    {
        public string Type = "";
        public string? Id, Name;
        public JsonObject? ArgsObject;
        public readonly StringBuilder Args = new();
        public bool Emitted;
    }

    async Task<List<ToolCall>> StreamOnceAsync(string apiKey, JsonNode input, string systemInstruction, JsonArray tools,
        Action<string> onText, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = Model,
            ["input"] = input.DeepClone(),
            ["system_instruction"] = systemInstruction,
            ["tools"] = tools.DeepClone(),
            ["stream"] = true,
        };
        if (_previousId != null) body["previous_interaction_id"] = _previousId;
        if (_sendThinkingLevel) body["generation_config"] = new JsonObject { ["thinking_level"] = "low" };

        using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("x-goog-api-key", apiKey);
        if (Debug) App.Log($"gemini ← POST interactions (model {Model}, continuing={_previousId != null})");
        using var headers = CancellationTokenSource.CreateLinkedTokenSource(ct);
        headers.CancelAfter(TimeSpan.FromSeconds(60));
        HttpResponseMessage respRaw;
        try { respRaw = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, headers.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new GeminiException(504, "Gemini didn't answer within 60 s.");
        }
        using var resp = respRaw;
        if (!resp.IsSuccessStatusCode)
        {
            var msg = ErrorMessage(await resp.Content.ReadAsStringAsync(ct));
            // Not every model accepts every thinking level: retry once with the model's default.
            if (resp.StatusCode == HttpStatusCode.BadRequest && _sendThinkingLevel && msg.Contains("thinking", StringComparison.OrdinalIgnoreCase))
            {
                _sendThinkingLevel = false;
                return await StreamOnceAsync(apiKey, input, systemInstruction, tools, onText, ct);
            }
            throw new GeminiException((int)resp.StatusCode, msg);
        }

        var steps = new Dictionary<int, StepState>();
        var calls = new List<ToolCall>();

        void Emit(StepState s)
        {
            if (s.Emitted || s.Type != "function_call" || s.Name == null) return;
            s.Emitted = true;
            JsonObject args = s.ArgsObject ?? [];
            if (s.Args.Length > 0)
            {
                try { args = JsonNode.Parse(s.Args.ToString()) as JsonObject ?? args; } catch { /* keep what we have */ }
            }
            calls.Add(new ToolCall(s.Id ?? s.Name, s.Name, args));
        }

        void Handle(string? sseEvent, string data)
        {
            if (data == "[DONE]") return;
            JsonNode? node;
            try { node = JsonNode.Parse(data); } catch { return; }
            if (node == null) return;
            string type = Str(node["event_type"]) ?? sseEvent ?? (node["error"] != null ? "error" : "");
            if (Debug) App.Log($"gemini ← {type}: {(data.Length > 300 ? data[..300] + "…" : data)}");

            switch (type)
            {
                case "interaction.created":
                case "interaction.status_update":
                case "interaction.completed":
                    var id = Str(node["interaction"]?["id"]) ?? Str(node["id"]);
                    if (id != null) _previousId = id;
                    break;
                case "step.start":
                {
                    int index = (int?)node["index"] ?? steps.Count;
                    var step = node["step"];
                    var s = steps[index] = new StepState
                    {
                        Type = Str(step?["type"]) ?? "",
                        Id = Str(step?["id"]),
                        Name = Str(step?["name"]),
                    };
                    if (step?["arguments"] is JsonObject a && a.Count > 0) s.ArgsObject = (JsonObject)a.DeepClone();
                    if (s.Type == "model_output" && step?["content"] is JsonArray content)
                        foreach (var c in content) if (Str(c?["text"]) is { } t) onText(t);
                    break;
                }
                case "step.delta":
                {
                    int index = (int?)node["index"] ?? -1;
                    steps.TryGetValue(index, out var s);
                    var delta = node["delta"];
                    switch (Str(delta?["type"]))
                    {
                        case "text" when s == null || s.Type is "model_output" or "":
                            if (Str(delta?["text"]) is { } t) onText(t);
                            break;
                        case "arguments_delta" when s != null:
                            s.Args.Append(delta?["arguments"] is JsonObject o ? o.ToJsonString() : Str(delta?["arguments"]));
                            break;
                    }
                    break;
                }
                case "step.stop":
                    if (steps.TryGetValue((int?)node["index"] ?? -1, out var stopped)) Emit(stopped);
                    break;
                case "error":
                    throw new GeminiException(StatusFor(Str(node["error"]?["code"])), Str(node["error"]?["message"]) ?? "The model returned an error.");
            }
        }

        if (Debug) App.Log($"gemini → {(int)resp.StatusCode} {resp.Content.Headers.ContentType} (model {Model})");
        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        string? evt = null;
        var buf = new StringBuilder();
        int rawLogged = 0;
        // Never hang silently. An overloaded model can accept the request and then send only ": keep-alive" pings for
        // minutes, so pings don't count as progress: a healthy stream sends its first event at once (12 s allowed),
        // and after that a 60 s gap without events ends it. Timeouts surface as 503 so the next model is tried.
        bool gotEvent = false;
        var lastEvent = DateTime.Now;
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        while (true)
        {
            var allowed = TimeSpan.FromSeconds(gotEvent ? 60 : 12) - (DateTime.Now - lastEvent);
            idle.CancelAfter(allowed > TimeSpan.Zero ? allowed : TimeSpan.FromMilliseconds(1));
            string? line;
            try { line = await reader.ReadLineAsync(idle.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new GeminiException(gotEvent ? 504 : 503, gotEvent ? "Gemini stopped responding." : $"{Model} is too busy to start answering.");
            }
            catch (IOException ex)
            {
                throw new GeminiException(503, "The connection to Gemini dropped: " + ex.Message);
            }
            if (line == null) break;
            if (line.Length > 0 && line[0] != ':') { gotEvent = true; lastEvent = DateTime.Now; }
            if (Debug && rawLogged < 12) { rawLogged++; App.Log("gemini raw: " + (line.Length > 200 ? line[..200] + "…" : line)); }
            if (line.StartsWith('{'))
            {
                // Errors can arrive as a bare JSON line, without an SSE "data:" prefix.
                Handle(evt, line);
                continue;
            }
            if (line.Length == 0)
            {
                if (buf.Length > 0) Handle(evt, buf.ToString());
                buf.Clear();
                evt = null;
            }
            else if (line.StartsWith("event:")) evt = line[6..].Trim();
            else if (line.StartsWith("data:"))
            {
                if (buf.Length > 0) buf.Append('\n');
                buf.Append(line.AsSpan(5).TrimStart());
            }
        }
        if (buf.Length > 0) Handle(evt, buf.ToString());
        foreach (var s in steps.Values) Emit(s);   // calls that arrived whole, without a step.stop
        return calls;
    }

    /// <summary>Checks a key with a cheap authenticated request. Returns null if it works, else the reason.</summary>
    public static async Task<string?> ValidateKeyAsync(string apiKey, CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://generativelanguage.googleapis.com/v1beta/models?pageSize=1");
            req.Headers.Add("x-goog-api-key", apiKey);
            using var resp = await Http.SendAsync(req, ct);
            if (resp.IsSuccessStatusCode) return null;
            return ErrorMessage(await resp.Content.ReadAsStringAsync(ct));
        }
        catch (Exception ex) { return "Couldn't reach Google: " + ex.Message; }
    }

    static string ErrorMessage(string body)
    {
        try
        {
            var n = JsonNode.Parse(body);
            if (Str(n?["error"]?["message"]) is { } m) return m;
        }
        catch { }
        return string.IsNullOrWhiteSpace(body) ? "Request failed." : body.Length > 200 ? body[..200] : body;
    }

    /// <summary>Stream errors carry a code string; map it to the HTTP status it stands for.</summary>
    static int StatusFor(string? code) => code switch
    {
        "service_unavailable" or "unavailable" => 503,
        "resource_exhausted" or "rate_limit_exceeded" => 429,
        "gateway_timeout" or "deadline_exceeded" => 504,
        "invalid_argument" or "failed_precondition" => 400,
        "unauthenticated" => 401,
        "permission_denied" => 403,
        _ => 500,
    };

    static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
