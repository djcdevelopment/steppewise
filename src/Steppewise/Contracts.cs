namespace Steppewise;
public sealed record RunSnapshot(string Id,string Workflow,string Status,int Version,string EvidenceMode,List<RunEvent> Events,List<Artifact> Artifacts);
public sealed record RunEvent(int Sequence,DateTimeOffset Timestamp,string Action,string Boundary,string Status,string Detail,int? CauseSequence = null,string? TraceId = null,string? SpanId = null,string? Actor = null,string? ActorSource = null,string? DecisionId = null);
public sealed record Artifact(string Id,string Hash,string Json,bool Passed,string Detail);
public sealed record WorkflowDefinition(int SchemaVersion,string Id,int Version,string Title,List<WorkflowStep> Steps);
public sealed record WorkflowStep(string Id,string Action,string Boundary);
// Checkpoint-shaped messages; no MAF checkpoint resume is implemented.
public sealed record RunInput(string RunId,string Workflow,string DefinitionHash,int NextStep,IReadOnlyList<Artifact> History,int Sequence = 0,int? CauseSequence = null);
public sealed record Candidate(RunInput State,Artifact Value) { public string RunId => State.RunId; public int Sequence => State.Sequence; public int? CauseSequence => State.CauseSequence; }
public sealed record Verdict(RunInput State,Artifact Value,bool Passed) { public string RunId => State.RunId; public int Sequence => State.Sequence; public int? CauseSequence => State.CauseSequence; }
public sealed record ReviewDecision(string RunId,string ArtifactHash,bool Accept,string Actor,string ActorSource,string DecisionId,int Sequence = 0,int? CauseSequence = null);
public sealed record DurableEvent(int SchemaVersion,string RunId,string Workflow,string DefinitionHash,RunEvent Event,Artifact? Artifact,string? NextStatus);

public sealed record RunOutcome(string RunId,int Sequence,int? CauseSequence,string Status);
