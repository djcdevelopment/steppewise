using Steppewise;
using Xunit;

namespace Steppewise.Tests;

public class ValidateDefinitionTests
{
    static List<WorkflowStep> Steps() =>
    [
        new("draft", "fixture.draft", "local"),
        new("check-1", "verify", "local"),
        new("repair", "fixture.repair", "local"),
        new("check-2", "verify", "local"),
        new("review", "human.review", "human"),
    ];
    static WorkflowDefinition Definition(List<WorkflowStep>? steps = null, int schema = 1, int version = 1, string id = "service-intake") =>
        new(schema, id, version, "Title", steps ?? Steps());

    [Fact]
    public void Accepts_the_five_step_sequence() =>
        WorkflowRuntime.ValidateDefinition(Definition(), "service-intake");

    [Fact]
    public void Refuses_a_sixth_step()
    {
        var steps = Steps();
        steps.Add(new("extra", "verify", "local"));
        Assert.Throws<InvalidDataException>(() => WorkflowRuntime.ValidateDefinition(Definition(steps), "service-intake"));
    }

    [Fact]
    public void Refuses_out_of_order_steps()
    {
        var steps = Steps();
        (steps[0], steps[1]) = (steps[1], steps[0]);
        Assert.Throws<InvalidDataException>(() => WorkflowRuntime.ValidateDefinition(Definition(steps), "service-intake"));
    }

    [Fact]
    public void Refuses_duplicate_step_ids()
    {
        var steps = Steps();
        steps[3] = steps[1];
        Assert.Throws<InvalidDataException>(() => WorkflowRuntime.ValidateDefinition(Definition(steps), "service-intake"));
    }

    [Fact]
    public void Refuses_a_blank_boundary()
    {
        var steps = Steps();
        steps[0] = steps[0] with { Boundary = " " };
        Assert.Throws<InvalidDataException>(() => WorkflowRuntime.ValidateDefinition(Definition(steps), "service-intake"));
    }

    [Fact]
    public void Refuses_a_mismatched_id() =>
        Assert.Throws<InvalidDataException>(() => WorkflowRuntime.ValidateDefinition(Definition(), "local-ai-work"));

    [Fact]
    public void Refuses_an_unsupported_schema_version() =>
        Assert.Throws<InvalidDataException>(() => WorkflowRuntime.ValidateDefinition(Definition(schema: 2), "service-intake"));
}
