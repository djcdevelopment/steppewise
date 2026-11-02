using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI.Workflows;

namespace Steppewise;

/// <summary>Single-process fixture runtime. The storage lock prevents unsupported concurrent writers.</summary>
public sealed class WorkflowRuntime : IAsyncDisposable
{
    static readonly JsonSerializerOptions CompactJson = new(JsonSerializerDefaults.Web);
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    static readonly string[] Actions = ["fixture.draft", "verify", "fixture.repair", "verify", "human.review"];
    readonly string root;
    readonly FileStream ownership;
    readonly SemaphoreSlim gate = new(1, 1);
    readonly Dictionary<string, Session> sessions = [];
    readonly Dictionary<string, WorkflowDefinition> definitions = [];
    readonly ActivitySource source = new("Steppewise");
    readonly ActivityListener listener;
    readonly object sync = new();
    bool disposed;
    readonly bool qa = Environment.GetEnvironmentVariable("STEPPEWISE_ENV") == "qa";
    readonly string? emitTrace = Environment.GetEnvironmentVariable("STEPPEWISE_EMIT_TRACE");
    readonly string? faultMarker = Environment.GetEnvironmentVariable("STEPPEWISE_FAULT_MARKER");
    int emitOrdinal;
    int faultAfter;
    public static readonly string[] EmitSites = ["recovery.reconcile", "recovery.interrupted", "start.admitted", "start.waiting", "start.failed", "review.requested", "review.failed", "review.restored", "review.recorded", "maf.received", "action.started", "candidate.produced", "candidate.checked"];


    public WorkflowRuntime(string storageRoot)
    {
        var fault = Environment.GetEnvironmentVariable("STEPPEWISE_FAULT_AFTER");
        if (fault is not null && (!qa || !int.TryParse(fault, out faultAfter) || faultAfter < 1 || string.IsNullOrWhiteSpace(faultMarker) || string.IsNullOrWhiteSpace(emitTrace)))
            throw new InvalidOperationException("Fault injection requires QA, positive STEPPEWISE_FAULT_AFTER, STEPPEWISE_FAULT_MARKER and STEPPEWISE_EMIT_TRACE.");
        if (!qa && (emitTrace is not null || (Environment.GetEnvironmentVariable("STEPPEWISE_FAIL_REPAIR") is not null || Environment.GetEnvironmentVariable("STEPPEWISE_FAIL_REVIEW") is not null)))
            throw new InvalidOperationException("Qualification hooks require QA.");
        root = Path.GetFullPath(storageRoot);
        Directory.CreateDirectory(root);
        ownership = new FileStream(Path.Combine(root, ".owner.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        listener = new ActivityListener {
            ShouldListenTo = s => ReferenceEquals(s, source),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = a => { lock (sync) AppendFlushed(Path.Combine(root, "spans.jsonl"), JsonSerializer.Serialize(new { traceId = a.TraceId.ToString(), spanId = a.SpanId.ToString(), parentSpanId = a.ParentSpanId.ToString(), name = a.OperationName, started = a.StartTimeUtc, durationMs = a.Duration.TotalMilliseconds, tags = a.TagObjects.ToDictionary(t => t.Key, t => t.Value) }) + "\n"); }
        };
        ActivitySource.AddActivityListener(listener);
        try {
            foreach (var id in new[] { "service-intake", "local-ai-work" }) {
                var dir = Environment.GetEnvironmentVariable("STEPPEWISE_DEFINITIONS");
                var file = dir is null ? null : Path.Combine(dir, id + ".json");
                var def = file is null ? new WorkflowDefinition(1, id, 1, id, Actions.Select((a, i) => new WorkflowStep($"step-{i + 1}", a, a == "human.review" ? "human" : "software")).ToList())
                    : JsonSerializer.Deserialize<WorkflowDefinition>(File.ReadAllText(file), Json) ?? throw new InvalidDataException("Missing definition.");
                ValidateDefinition(def, id);
                definitions.Add(id, def);
            }
            LoadEvidence();

        } catch { listener.Dispose(); source.Dispose(); ownership.Dispose(); throw; }
    }

    public RunSnapshot Get(string id) { lock (sync) { ThrowDisposed(); return Clone(GetSession(id).Snapshot); } }
    public IEnumerable<RunSnapshot> List() { lock (sync) { ThrowDisposed(); return sessions.Values.Select(s => Clone(s.Snapshot)).ToArray(); } }

    public async Task<RunSnapshot> StartAsync(string workflow, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        Session? s = null;
        try {
            ThrowDisposed();
            if (!definitions.TryGetValue(workflow, out var definition)) throw new ArgumentException("Unknown workflow.");
            s = new Session(new(Guid.NewGuid().ToString("N"), workflow, "Running", 0, "fixture", [], [])) { DefinitionHash = Hash(JsonSerializer.Serialize(definition, Json)) };
            lock (sync) sessions.Add(s.Snapshot.Id, s);
            Emit(s, "admission", "software", "Accepted", "Fixture request accepted; execution is not yet evidenced. Definition SHA256: " + Hash(JsonSerializer.Serialize(definition, Json)), site: "start.admitted");
            using var activity = source.StartActivity("workflow.run");
            activity?.SetTag("workflow.run.id", s.Snapshot.Id);
            s.DefinitionHash = Hash(JsonSerializer.Serialize(definition, Json));
            var graph = Compile(s, definition);
            s.Run = await InProcessExecution.RunStreamingAsync(graph, new RunInput(s.Snapshot.Id, workflow, s.DefinitionHash, 0, [], s.Snapshot.Version, null), cancellationToken: ct);
            await Drain(s, ct);
            if (s.Request is null || !s.Snapshot.Artifacts.Last().Passed) throw new InvalidOperationException("Workflow did not reach verified review.");
            Emit(s, "human.review", "human", "Waiting", "Automated checks passed. Explicit human acceptance is still required.", nextStatus: "AwaitingReview", site: "start.waiting");
            return Clone(s.Snapshot);
        } catch {
            if (s is not null) { Emit(s, "runtime", "software", "Failed", "Execution failed; retained evidence is available.", nextStatus: "Failed", site: "start.failed"); }
            throw;
        } finally { gate.Release(); }
    }

    public Task<RunSnapshot> ReviewAsync(string id, bool accept, string expectedHash, CancellationToken ct = default) => ReviewAsync(id, accept, expectedHash, null, ct);
    public async Task<RunSnapshot> ReviewAsync(string id, bool accept, string expectedHash, string? reviewer, CancellationToken ct = default)
    {
        if (reviewer is not null && (string.IsNullOrWhiteSpace(reviewer) || reviewer.Length > 128 || reviewer.Any(char.IsControl))) throw new ArgumentException("Reviewer name must be 1–128 visible characters.");
        await gate.WaitAsync(ct);
        try {
            ThrowDisposed();
            var s = GetSession(id);
            if (s.Archived) throw new InvalidOperationException("Legacy snapshot-only evidence is a read-only archive.");
            if (s.Snapshot.Status != "AwaitingReview") throw new InvalidOperationException("No pending human review.");
            var artifact = s.Snapshot.Artifacts.Last();
            if (!artifact.Passed || !VerifyArtifact(s.Snapshot.Workflow, artifact.Json) || artifact.Hash != expectedHash || Hash(artifact.Json) != expectedHash) throw new InvalidOperationException("Review does not match the verified artifact.");
            ct.ThrowIfCancellationRequested();
            // After admission, finish the serialized decision independently of HTTP disconnect.
            if (s.Run is null) await RestoreReview(s, CancellationToken.None);
            s.Reviewer = reviewer?.Trim() ?? Environment.UserName;
            s.ReviewerSource = reviewer is null ? "local-process-user" : "self-reported";
            s.DecisionId = Guid.NewGuid().ToString("N");
            Emit(s, "human.review", "human", "DecisionRequested", "Explicit review command admitted; execution receipt is pending.", site: "review.requested");
            try {
            using var activity = source.StartActivity("workflow.human-review");
            if (qa && Environment.GetEnvironmentVariable("STEPPEWISE_FAIL_REVIEW") == "1") throw new InvalidOperationException("Synthetic QA review failure after admission.");
            await s.Run!.SendResponseAsync(s.Request!.CreateResponse(new ReviewDecision(id, expectedHash, accept, s.Reviewer!, s.ReviewerSource!, s.DecisionId!, s.Snapshot.Version, s.Snapshot.Events.Last().CauseSequence)));
            s.Request = null;
            await Drain(s, CancellationToken.None);
            if (s.Snapshot.Status is not ("Accepted" or "Rejected")) throw new InvalidOperationException("Review executor did not record decision.");
            return Clone(s.Snapshot);
            } catch {
                lock (sync) {
                    if (s.Snapshot.Status is not ("Accepted" or "Rejected")) {
                        try { Emit(s, "human.review", "storage", "NeedsReconciliation", "Review completion was interrupted; no acceptance is claimed. Inspect durable evidence before any new command.", nextStatus: "Failed", site: "review.failed"); }
                        catch (IOException) { /* Storage is unavailable; keep the visible state failed. */ }
                        catch (UnauthorizedAccessException) { /* Storage is unavailable; keep the visible state failed. */ }
                    }
                }
                throw;
            }
        } finally { gate.Release(); }
    }

    async Task RestoreReview(Session s, CancellationToken ct)
    {
        var definition = definitions[s.Snapshot.Workflow];
        if (s.DefinitionHash != Hash(JsonSerializer.Serialize(definition, Json))) throw new InvalidOperationException("Definition changed; review cannot be restored.");
        var input = new RunInput(s.Snapshot.Id, s.Snapshot.Workflow, s.DefinitionHash, definition.Steps.Count - 1, s.Snapshot.Artifacts.ToArray(), s.Snapshot.Version, s.Snapshot.Events.LastOrDefault(e => e.Action != "maf")?.Sequence);
        var begin = new TypedStep<RunInput, Verdict>("restore-review", x => new Verdict(x, x.History.Last(), x.History.Last().Passed));
        var port = RequestPort.Create<Verdict, ReviewDecision>(definition.Steps.Single(x => x.Action == "human.review").Id);
        var finish = new TypedStep<ReviewDecision, RunOutcome>("record-human-review", x => RecordReview(s, x));
        var graph = new WorkflowBuilder(begin).AddEdge(begin, port).AddEdge(port, finish).WithOutputFrom(finish).WithOpenTelemetry(o => o.EnableSensitiveData = false, source).Build();
        s.Run = await InProcessExecution.RunStreamingAsync(graph, input, cancellationToken: ct);
        await Drain(s, ct);
        if (s.Request is null) throw new InvalidOperationException("Could not restore review.");
        Emit(s, "recovery", "storage", "ReviewRestored", "Restored review from verified durable fixture artifacts using a new MAF review graph. This is not MAF checkpoint resume.", site: "review.restored");
    }

    RunOutcome RecordReview(Session s, ReviewDecision decision)
    {
        lock (sync) {
            if (decision.RunId != s.Snapshot.Id || decision.ArtifactHash != s.Snapshot.Artifacts.Last().Hash || s.Snapshot.Status != "AwaitingReview" || !s.Snapshot.Artifacts.Last().Passed || string.IsNullOrWhiteSpace(decision.Actor)) throw new InvalidOperationException("Invalid human decision state.");
            s.Reviewer = decision.Actor; s.ReviewerSource = decision.ActorSource; s.DecisionId = decision.DecisionId;
            var status = decision.Accept ? "Accepted" : "Rejected";
            Emit(s, "human.review", "human", status, "Explicit user decision recorded by MAF executor for SHA256 " + decision.ArtifactHash, status, "review.recorded");
            return new RunOutcome(s.Snapshot.Id, s.Snapshot.Version, decision.Sequence, status);
        }
    }

    Workflow Compile(Session s, WorkflowDefinition definition)
    {
        var registry = new Dictionary<string, ActionRegistration> {
            ["fixture.draft"] = new(typeof(RunInput), typeof(Candidate), step => new TypedStep<RunInput, Candidate>(step.Id, x => Produce(s, step, x, false))),
            ["verify"] = new(typeof(Candidate), typeof(Verdict), step => new TypedStep<Candidate, Verdict>(step.Id, x => Verify(s, step, x))),
            ["fixture.repair"] = new(typeof(Verdict), typeof(Candidate), step => new TypedStep<Verdict, Candidate>(step.Id, x => Produce(s, step, x.State, true))),
            ["human.review"] = new(typeof(Verdict), typeof(ReviewDecision), step => RequestPort.Create<Verdict, ReviewDecision>(step.Id))
        };
        var bindings = new List<ExecutorBinding>();
        Type message = typeof(RunInput);
        foreach (var step in definition.Steps) {
            if (!registry.TryGetValue(step.Action, out var action) || action.Input != message) throw new InvalidDataException("Unsupported or incompatible definition action: " + step.Action);
            bindings.Add(action.Factory(step)); message = action.Output;
        }
        var builder = new WorkflowBuilder(bindings.First());
        foreach (var pair in bindings.Zip(bindings.Skip(1))) builder.AddEdge(pair.First, pair.Second);
        var finish = new TypedStep<ReviewDecision, RunOutcome>("record-human-review", x => RecordReview(s, x));
        builder.AddEdge(bindings.Last(), finish).WithOutputFrom(finish);
        return builder.WithOpenTelemetry(o => o.EnableSensitiveData = false, source).Build();
    }
    sealed record ActionRegistration(Type Input, Type Output, Func<WorkflowStep, ExecutorBinding> Factory);

    async Task Drain(Session s, CancellationToken ct)
    {
        await foreach (var e in s.Run!.WatchStreamAsync(false, ct)) {
            Emit(s, "maf", "software", e.GetType().Name, e is ExecutorCompletedEvent c ? c.ExecutorId : e is ExecutorInvokedEvent i ? i.ExecutorId : e.GetType().Name, site: "maf.received");
            if (e is RequestInfoEvent r) s.Request = r.Request;
            if (e is WorkflowErrorEvent error) throw error.Exception ?? new InvalidOperationException("MAF execution error.");
        }
    }

    Candidate Produce(Session s, WorkflowStep step, RunInput state, bool isRepair)
    {
        Emit(s, step.Id, step.Boundary, "ActionStarted", "Executor entered " + step.Action + ".", site: "action.started");
        bool repaired = isRepair && !(qa && Environment.GetEnvironmentVariable("STEPPEWISE_FAIL_REPAIR") == "1");
                var json = state.Workflow == "service-intake"
                    ? JsonSerializer.Serialize(new { ticketId = "CR-1042", title = "Rotate webhook signing key", rollbackPlan = repaired ? "Revert key version v2 to v1 in secrets store" : "", service = "webhook-dispatcher", environment = "staging", evidence = "synthetic-fixture" }, Json)
                    : JsonSerializer.Serialize(new { task = "Summarize a synthetic maintenance note", candidate = repaired ? "Replace the filter every 30 days." : "Replace the filter every 300 days.", expectedIntervalDays = 30, intervalDays = repaired ? 30 : 300, provider = "fixture", execution = "synthetic", humanAccepted = false }, Json);
        var artifact = new Artifact($"candidate-{state.History.Count + 1}", Hash(json), json, false, "Not checked.");
        Emit(s, step.Id, step.Boundary, "CandidateProduced", repaired ? "Repair fixture produced; original candidate retained." : "Initial fixture candidate produced; no inference occurred.", site: "candidate.produced", artifact: artifact);
        return new Candidate(state with { NextStep = state.NextStep + 1, Sequence = s.Snapshot.Version, CauseSequence = state.Sequence, History = state.History.Append(artifact).ToArray() }, artifact);
    }
    Verdict Verify(Session s, WorkflowStep step, Candidate candidate)
    {
        Emit(s, step.Id, step.Boundary, "ActionStarted", "Executor entered " + step.Action + ".", site: "action.started");
        var passed = VerifyArtifact(candidate.State.Workflow, candidate.Value.Json);
        var detail = candidate.State.Workflow == "service-intake" ? "Rollback plan must be present." : "Candidate interval must equal the source's 30-day interval.";
        var artifact = candidate.Value with { Passed = passed, Detail = detail };
        Emit(s, step.Id, step.Boundary, passed ? "CheckPassed" : "CheckFailed", detail, site: "candidate.checked", artifact: artifact);
        return new Verdict(candidate.State with { NextStep = candidate.State.NextStep + 1, Sequence = s.Snapshot.Version, CauseSequence = candidate.Sequence, History = candidate.State.History.Select(x => x.Id == artifact.Id ? artifact : x).ToArray() }, artifact, passed);
    }

    void Emit(Session s, string action, string boundary, string status, string detail, string? nextStatus = null, string site = "", Artifact? artifact = null)
    {
        lock (sync) {
            var next = Clone(s.Snapshot) with { Version = s.Snapshot.Version + 1, Status = nextStatus ?? s.Snapshot.Status };
            string? traceId = null, spanId = null;
            if (action != "maf") {
                using var eventSpan = source.StartActivity("workflow.event." + status, ActivityKind.Internal, s.TraceContext);
                if (eventSpan is null) throw new InvalidOperationException("Domain event span was not recorded.");
                eventSpan.SetTag("workflow.run.id", next.Id);
                eventSpan.SetTag("event.sequence", next.Events.Count + 1);
                traceId = eventSpan.TraceId.ToString(); spanId = eventSpan.SpanId.ToString();
                s.TraceContext = eventSpan.Context;
                eventSpan.Stop(); // Synchronous listener flushes the completed span before its referencing row.
            }
            bool decisionEvent = action == "human.review" && status is "DecisionRequested" or "Accepted" or "Rejected" or "NeedsReconciliation";
            next.Events.Add(new(next.Events.Count + 1, DateTimeOffset.UtcNow, action, boundary, status, detail,
                action == "maf" ? null : next.Events.LastOrDefault(e => e.Action != "maf")?.Sequence,
                traceId, spanId, decisionEvent ? s.Reviewer : null, decisionEvent ? s.ReviewerSource : null, decisionEvent ? s.DecisionId : null));
            var envelope = new DurableEvent(2, next.Id, next.Workflow, s.DefinitionHash, next.Events.Last(), artifact, nextStatus);
            var projected = Reduce(s.Snapshot, envelope);
            AppendFlushed(Path.Combine(root, "run-" + next.Id + ".events.jsonl"), JsonSerializer.Serialize(envelope, CompactJson) + "\n");
            s.Snapshot = projected;
            WriteDiagnostic(next.Id, "event-append", projected.Version);
            if (site is "start.admitted" or "start.waiting" || nextStatus is "Accepted" or "Rejected" or "Failed") WriteSnapshot(projected);
            if (qa && emitTrace is not null) {
                var marker = JsonSerializer.Serialize(new { ordinal = ++emitOrdinal, site, runId = next.Id, sequence = next.Events.Count, status = s.Snapshot.Status });
                AppendFlushed(emitTrace, marker + "\n");
                if (faultAfter == emitOrdinal) {
                    using (var file = new FileStream(faultMarker!, FileMode.Create, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough)) {
                        var bytes = Encoding.UTF8.GetBytes(marker); file.Write(bytes); file.Flush(true);
                    }
                    Thread.Sleep(Timeout.Infinite);
                }
            }
        }
    }
    void LoadEvidence()
    {
        foreach (var path in Directory.EnumerateFiles(root, "run-*.events.jsonl")) {
            var bytes = File.ReadAllText(path);
            if (bytes.Length == 0 || !bytes.EndsWith('\n')) throw new InvalidDataException("Incomplete event log; evidence is preserved and mutation refused.");
            RunSnapshot? snapshot = null;
            var snapshotPath = path.Replace(".events.jsonl", ".json", StringComparison.Ordinal);
            if (File.Exists(snapshotPath)) {
                snapshot = JsonSerializer.Deserialize<RunSnapshot>(File.ReadAllText(snapshotPath), Json) ?? throw new InvalidDataException("Missing snapshot.");
                ValidateSnapshot(snapshot);
            }
            RunSnapshot? replay = null;
            string definitionHash = "";
            bool snapshotMatched = snapshot is null;
            foreach (var line in bytes.Split('\n', StringSplitOptions.RemoveEmptyEntries)) {
                DurableEvent envelope;
                try { envelope = JsonSerializer.Deserialize<DurableEvent>(line, CompactJson) ?? throw new InvalidDataException("Null event envelope."); }
                catch (JsonException e) { throw new InvalidDataException("Malformed event log; mutation refused.", e); }
                if (replay is null) {
                    definitionHash = envelope.DefinitionHash;
                    replay = new(envelope.RunId, envelope.Workflow, "Running", 0, "fixture", [], []);
                    if (Path.GetFileName(path) != "run-" + replay.Id + ".events.jsonl") throw new InvalidDataException("Event filename does not match run.");
                }
                if (envelope.DefinitionHash != definitionHash) throw new InvalidDataException("Definition identity changed in event log.");
                replay = Reduce(replay, envelope);
                ValidateSnapshot(replay);
                if (snapshot is not null && snapshot.Version == replay.Version) {
                    if (JsonSerializer.Serialize(snapshot, Json) != JsonSerializer.Serialize(replay, Json)) throw new InvalidDataException("Snapshot disagrees with event log prefix.");
                    snapshotMatched = true;
                }
            }
            if (replay is null || !snapshotMatched) throw new InvalidDataException("Snapshot sequence is not present in the event log.");
            var session = new Session(replay) { DefinitionHash = definitionHash };
            sessions.Add(replay.Id, session);
            var decision = replay.Events.LastOrDefault(e => e.Action == "human.review" && e.Status == "DecisionRequested");
            var resolved = decision is null || replay.Events.Any(e => e.Sequence > decision.Sequence && (e.Status == "NeedsReconciliation" || (e.Action == "human.review" && e.Status is "Accepted" or "Rejected")));
            if (!resolved) Emit(session, "recovery", "storage", "NeedsReconciliation", "An admitted decision has no terminal receipt. No acceptance is inferred; review is refused.", "Failed", "recovery.reconcile");
            else if (replay.Status == "Running") Emit(session, "recovery", "storage", "Interrupted", "Process ended during execution; no effects are replayed automatically. Start a new fixture run.", "Failed", "recovery.interrupted");
        }
        foreach (var path in Directory.EnumerateFiles(root, "run-*.json")) {
            var snapshot = JsonSerializer.Deserialize<RunSnapshot>(File.ReadAllText(path), Json) ?? throw new InvalidDataException("Missing snapshot.");
            if (sessions.ContainsKey(snapshot.Id)) continue;
            ValidateSnapshot(snapshot);
            sessions.Add(snapshot.Id, new Session(snapshot) { Archived = true });
        }
    }
    static RunSnapshot Reduce(RunSnapshot previous, DurableEvent envelope)
    {
        if (envelope.SchemaVersion != 2 || envelope.RunId != previous.Id || envelope.Workflow != previous.Workflow || envelope.Event.Sequence != previous.Version + 1) throw new InvalidDataException("Invalid event version, identity or sequence.");
        var next = Clone(previous) with { Version = envelope.Event.Sequence, Status = envelope.NextStatus ?? previous.Status };
        next.Events.Add(envelope.Event);
        if (envelope.Artifact is { } artifact) {
            if (Hash(artifact.Json) != artifact.Hash) throw new InvalidDataException("Artifact payload hash is invalid.");
            var index = next.Artifacts.FindIndex(a => a.Id == artifact.Id);
            if (index < 0) next.Artifacts.Add(artifact); else next.Artifacts[index] = artifact;
        }
        return next;
    }
    void WriteSnapshot(RunSnapshot snapshot)
    {
        var target = Path.Combine(root, "run-" + snapshot.Id + ".json");
        var temporary = target + ".tmp";
        using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) {
            JsonSerializer.Serialize(file, snapshot, Json); file.Flush(true);
        }
        File.Move(temporary, target, true);
        WriteDiagnostic(snapshot.Id, "snapshot", snapshot.Version);
    }
    void WriteDiagnostic(string runId, string kind, int sequence)
    {
        if (!qa) return;
        try { AppendFlushed(Path.Combine(root, "storage-writes.jsonl"), JsonSerializer.Serialize(new { runId, kind, sequence }) + "\n"); }
        catch (IOException) { /* Non-authoritative measurement failure cannot undo a committed event. */ }
        catch (UnauthorizedAccessException) { /* Qualification will report missing diagnostics. */ }
    }

    static void AppendFlushed(string path, string value)
    {
        using var file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
        file.Write(Encoding.UTF8.GetBytes(value)); file.Flush(true);
    }
    static void ValidateDefinition(WorkflowDefinition d, string expected)
    {
        if (d.SchemaVersion != 1 || d.Version != 1 || d.Id != expected || d.Steps is null || d.Steps.Count != 5 || !d.Steps.Select(s => s.Action).SequenceEqual(Actions) || d.Steps.Select(s => s.Id).Distinct().Count() != 5 || d.Steps.Any(s => string.IsNullOrWhiteSpace(s.Id) || string.IsNullOrWhiteSpace(s.Boundary))) throw new InvalidDataException("Unsupported definition: v1 supports exactly five steps in order: fixture.draft, verify, fixture.repair, verify, human.review.");
    }
    static bool VerifyArtifact(string workflow, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var data = doc.RootElement;
        return workflow == "service-intake"
            ? data.TryGetProperty("rollbackPlan", out var s) && !string.IsNullOrWhiteSpace(s.GetString())
            : data.TryGetProperty("intervalDays", out var interval) && interval.TryGetInt32(out var days) && days == 30 && data.TryGetProperty("expectedIntervalDays", out var expected) && expected.GetInt32() == 30;
    }
    static void ValidateSnapshot(RunSnapshot s)
    {
        if (s.Status is not ("Running" or "AwaitingReview" or "Accepted" or "Rejected" or "Failed")) throw new InvalidDataException("Unknown stored status.");
        if (!Guid.TryParseExact(s.Id, "N", out _) || s.EvidenceMode != "fixture" || s.Workflow is not ("service-intake" or "local-ai-work") || s.Artifacts.Any(a => Hash(a.Json) != a.Hash)) throw new InvalidDataException("Stored run failed integrity validation.");
        if (s.Status is "AwaitingReview" or "Accepted" or "Rejected") {
            if (s.Artifacts.Count != 2 || !s.Artifacts[^1].Passed || !VerifyArtifact(s.Workflow, s.Artifacts[^1].Json)) throw new InvalidDataException("Stored review artifact is invalid.");
        }
    }
    Session GetSession(string id) => sessions.TryGetValue(id, out var s) ? s : throw new KeyNotFoundException("Run not found.");
    static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    static RunSnapshot Clone(RunSnapshot s) => s with { Events = s.Events.ToList(), Artifacts = s.Artifacts.ToList() };
    void ThrowDisposed() => ObjectDisposedException.ThrowIf(disposed, this);
    public async ValueTask DisposeAsync() { await gate.WaitAsync(); try { if (disposed) return; disposed = true; foreach (var s in sessions.Values) if (s.Run is not null) await s.Run.DisposeAsync(); listener.Dispose(); source.Dispose(); ownership.Dispose(); } finally { gate.Release(); } }
    sealed class Session(RunSnapshot snapshot) { public RunSnapshot Snapshot = snapshot; public StreamingRun? Run; public ExternalRequest? Request; public bool Archived; public string DefinitionHash = ""; public string? Reviewer; public string? ReviewerSource; public string? DecisionId;
        public ActivityContext TraceContext = InitialContext(snapshot);
        static ActivityContext InitialContext(RunSnapshot snapshot) {
            var last = snapshot.Events.LastOrDefault(e => e.TraceId is not null && e.SpanId is not null);
            return last is null ? default : new ActivityContext(ActivityTraceId.CreateFromString(last.TraceId), ActivitySpanId.CreateFromString(last.SpanId), ActivityTraceFlags.Recorded);
        }
    }
    // Pure representative values for the external qualification executable. No special converters or runtime handles.
    public static object[] MessageContractSamples()
    {
        const string runId = "11111111111111111111111111111111";
        const string json = "{\"synthetic\":true}";
        var artifact = new Artifact("candidate-1", Hash(json), json, true, "Synthetic message contract sample.");
        var input = new RunInput(runId, "service-intake", Hash("synthetic-definition"), 0, [], 1, null);
        var candidate = new Candidate(input with { NextStep = 1, History = new[] { artifact }, Sequence = 3, CauseSequence = 1 }, artifact);
        var verdict = new Verdict(candidate.State with { NextStep = 2, Sequence = 5, CauseSequence = 3 }, artifact, true);
        var decision = new ReviewDecision(runId, artifact.Hash, true, "qualification", "self-reported", "22222222222222222222222222222222", 7, 5);
        return [input, candidate, verdict, decision, new RunOutcome(runId, 8, 7, "Accepted")];
    }

    sealed class TypedStep<TIn,TOut>(string id, Func<TIn,TOut> action) : Executor<TIn,TOut>(id) {
        public override ValueTask<TOut> HandleAsync(TIn message, IWorkflowContext context, CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(action(message)); }
    }
}
