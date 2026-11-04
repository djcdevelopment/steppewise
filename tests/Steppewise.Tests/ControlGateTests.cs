using Steppewise;
using Xunit;

namespace Steppewise.Tests;

public class ControlGateTests
{
    static readonly IReadOnlySet<string> All = new HashSet<string> { "start", "inspect", "review" };
    static readonly Artifact Evidence = new("candidate-1", "hash-1", "{}", true, "");
    static RunSnapshot Run() => new("run-1", "service-intake", "Waiting", 3, "fixture", [], [Evidence]);
    static ControlProposal Inspect(string? runId = "run-1", int? version = 3, string[]? refs = null) =>
        new("inspect", null, runId, version, refs ?? ["hash-1"]);

    [Fact]
    public void Refuses_an_operation_the_caller_lacks() =>
        Assert.False(ControlGate.Validate(Inspect(), Run(), new HashSet<string>()).Permitted);

    [Theory]
    [InlineData("service-intake", true)]
    [InlineData("local-ai-work", true)]
    [InlineData("unregistered", false)]
    public void Start_admits_only_registered_workflows(string workflow, bool permitted) =>
        Assert.Equal(permitted, ControlGate.Validate(new("start", workflow, null, null, []), null, All).Permitted);

    [Fact]
    public void Review_can_never_be_delegated_to_a_proposal() =>
        Assert.False(ControlGate.Validate(new("review", null, "run-1", 3, ["hash-1"]), Run(), All).Permitted);

    [Fact]
    public void Inspect_permits_current_state_with_known_evidence() =>
        Assert.True(ControlGate.Validate(Inspect(), Run(), All).Permitted);

    [Fact]
    public void Inspect_refuses_a_missing_run() =>
        Assert.False(ControlGate.Validate(Inspect(), null, All).Permitted);

    [Fact]
    public void Inspect_refuses_a_different_run() =>
        Assert.False(ControlGate.Validate(Inspect(runId: "run-2"), Run(), All).Permitted);

    [Fact]
    public void Inspect_refuses_a_stale_version() =>
        Assert.False(ControlGate.Validate(Inspect(version: 2), Run(), All).Permitted);

    [Fact]
    public void Inspect_refuses_unknown_evidence() =>
        Assert.False(ControlGate.Validate(Inspect(refs: ["hash-unknown"]), Run(), All).Permitted);
}
