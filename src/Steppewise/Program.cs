using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Steppewise;

var profile = Environment.GetEnvironmentVariable("STEPPEWISE_ENV");
if (profile is not ("dev" or "qa" or "prod"))
    throw new InvalidOperationException("Set STEPPEWISE_ENV explicitly to dev or qa. Missing or invalid profiles are refused.");
if (profile == "prod")
    throw new InvalidOperationException("Production hosting is unavailable: authentication and production policy qualification are pending.");
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, EnvironmentName = profile == "dev" ? "Development" : "Staging", WebRootPath = Directory.Exists(Path.Combine(Directory.GetCurrentDirectory(), "wwwroot")) ? null : Path.Combine(AppContext.BaseDirectory, "wwwroot") });
// This host intentionally accepts only loopback connections; it is not a remote authenticated service.
var portText = Environment.GetEnvironmentVariable("STEPPEWISE_PORT") ?? "5180";
if (!int.TryParse(portText, out var port) || port < 1024 || port > 65535)
    throw new InvalidOperationException("STEPPEWISE_PORT must be between 1024 and 65535.");
builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, port));
var storage = Environment.GetEnvironmentVariable("STEPPEWISE_STORAGE")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Steppewise", profile);
builder.Services.AddSingleton(new WorkflowRuntime(storage));
var app = builder.Build();
var assembly = typeof(WorkflowRuntime).Assembly;
var informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
var revisionMatch = Regex.Match(informationalVersion, @"(?:\+|\.)([0-9a-fA-F]{40})(?:$|\.)");
string? gitRevision = revisionMatch.Success ? revisionMatch.Groups[1].Value.ToLowerInvariant() : null;
var dllSha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(assembly.Location)));
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; frame-ancestors 'none'; base-uri 'none'";
    // Block cross-origin browser commands, including DNS-rebinding hostnames.
    if (context.Request.Host.Host is not ("localhost" or "127.0.0.1"))
    { context.Response.StatusCode = 403; return; }
    if (context.Request.Method != "GET" && context.Request.Headers.TryGetValue("Origin", out var origin)
        && (!Uri.TryCreate(origin.ToString(), UriKind.Absolute, out var uri)
            || uri.Authority != context.Request.Host.Value || uri.Scheme != context.Request.Scheme))
    { context.Response.StatusCode = 403; return; }
    try { await next(context); }
    catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException or InvalidOperationException or FormatException)
    {
        context.Response.StatusCode = ex is KeyNotFoundException ? 404 : ex is InvalidOperationException ? 409 : 400;
        await context.Response.WriteAsJsonAsync(new { error = ex.Message });
    }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { context.Response.StatusCode = 499; }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Workflow operation failed");
        context.Response.StatusCode = 500;
        await context.Response.WriteAsJsonAsync(new { error = "The operation failed. Inspect local host diagnostics; execution has not been claimed successful." });
    }
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapGet("/api/info", () => new { environment = profile, evidenceMode = "fixture", liveInference = false, authentication = "none-loopback-only", gitRevision, dllSha256 });
app.MapGet("/api/runs", (WorkflowRuntime runtime) => runtime.List());
app.MapGet("/api/runs/{id}", (string id, WorkflowRuntime runtime) => runtime.Get(id));
app.MapGet("/api/runs/{id}/diagnostics", (string id, WorkflowRuntime runtime) =>
{
    var run = runtime.Get(id);
    return new { run.Id, domainEvents = run.Events.Where(e => e.Action != "maf"), frameworkArrivalEvents = run.Events.Where(e => e.Action == "maf"), frameworkOrdering = "arrival-order-not-causal", run.Artifacts };
});
app.MapPost("/api/runs/{workflow}", (string workflow, WorkflowRuntime runtime, CancellationToken ct) => runtime.StartAsync(workflow, ct));
app.MapPost("/api/runs/{id}/review", (string id, ReviewRequest request, WorkflowRuntime runtime, CancellationToken ct) =>
{
    if (request.Accept is null) throw new ArgumentException("An explicit accept or reject decision is required.");
    if (string.IsNullOrWhiteSpace(request.ExpectedHash)) throw new ArgumentException("Expected artifact hash is required.");
    return runtime.ReviewAsync(id, request.Accept.Value, request.ExpectedHash, request.Reviewer, ct);
});
app.Run();
public sealed record ReviewRequest(bool? Accept, string ExpectedHash, string? Reviewer = null);
