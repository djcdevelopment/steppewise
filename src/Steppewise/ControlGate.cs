namespace Steppewise;

// Proposals are untrusted data. This gate grants no execution or review authority.
public sealed record ControlProposal(string Operation, string? Workflow, string? RunId, int? ExpectedVersion, string[] EvidenceRefs);
public sealed record ControlDecision(bool Permitted, string Reason, string EvidenceMode = "policy-only; no inference");
public static class ControlGate
{
    public static ControlDecision Validate(ControlProposal proposal, RunSnapshot? run, IReadOnlySet<string> callerPermissions)
    {
        if (!callerPermissions.Contains(proposal.Operation)) return new(false, "Caller lacks this permission.");
        if (proposal.Operation == "start")
            return proposal.Workflow is "service-intake" or "local-ai-work"
                ? new(true, "Registered fixture workflow; admission still required.")
                : new(false, "Unknown workflow.");
        if (proposal.Operation != "inspect") return new(false, "Operation is not implemented for model control. Human review cannot be delegated.");
        if (run is null || proposal.RunId != run.Id) return new(false, "Run does not exist or differs from proposal.");
        if (proposal.ExpectedVersion != run.Version) return new(false, "Stale state; inspect current evidence before proposing again.");
        var known = run.Artifacts.Select(a => a.Hash).ToHashSet(StringComparer.Ordinal);
        if (proposal.EvidenceRefs is null || proposal.EvidenceRefs.Any(e => !known.Contains(e))) return new(false, "Unsupported evidence reference.");
        return new(true, "Inspection permitted; no mutation performed.");
    }
}
