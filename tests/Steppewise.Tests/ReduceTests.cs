using Steppewise;
using Xunit;

namespace Steppewise.Tests;

public class ReduceTests
{
    static RunSnapshot Run() => new("run-1", "service-intake", "Running", 1, "fixture", [], []);
    static RunEvent Event(int sequence) => new(sequence, DateTimeOffset.UnixEpoch, "verify", "local", "Running", "");
    static DurableEvent Envelope(int sequence = 2, int schema = 2, string runId = "run-1", string workflow = "service-intake", Artifact? artifact = null, string? nextStatus = null) =>
        new(schema, runId, workflow, "definition-hash", Event(sequence), artifact, nextStatus);

    [Fact]
    public void Advances_version_status_and_events_without_mutating_the_input()
    {
        var previous = Run();
        var next = WorkflowRuntime.Reduce(previous, Envelope(nextStatus: "Waiting"));
        Assert.Equal(2, next.Version);
        Assert.Equal("Waiting", next.Status);
        Assert.Single(next.Events);
        Assert.Equal(1, previous.Version);
        Assert.Empty(previous.Events);
    }

    [Fact]
    public void Keeps_the_status_when_the_event_does_not_change_it() =>
        Assert.Equal("Running", WorkflowRuntime.Reduce(Run(), Envelope()).Status);

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void Refuses_an_out_of_sequence_event(int sequence) =>
        Assert.ThrowsAny<Exception>(() => WorkflowRuntime.Reduce(Run(), Envelope(sequence: sequence)));

    [Fact]
    public void Refuses_an_unknown_schema_version() =>
        Assert.ThrowsAny<Exception>(() => WorkflowRuntime.Reduce(Run(), Envelope(schema: 1)));

    [Fact]
    public void Refuses_an_event_from_another_run() =>
        Assert.ThrowsAny<Exception>(() => WorkflowRuntime.Reduce(Run(), Envelope(runId: "run-2")));

    [Fact]
    public void Refuses_an_event_from_another_workflow() =>
        Assert.ThrowsAny<Exception>(() => WorkflowRuntime.Reduce(Run(), Envelope(workflow: "local-ai-work")));

    [Fact]
    public void Refuses_an_artifact_whose_payload_does_not_match_its_hash() =>
        Assert.Throws<InvalidDataException>(() => WorkflowRuntime.Reduce(Run(), Envelope(artifact: new("candidate-1", "not-the-hash", "{}", true, ""))));
}
