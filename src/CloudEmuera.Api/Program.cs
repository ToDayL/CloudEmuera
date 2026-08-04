using CloudEmuera.Contracts;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHealthChecks();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/v1/version", () =>
    Results.Ok(new BuildInfo(
        Product: "CloudEmuera",
        Version: typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0-dev",
        Runtime: System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
        HttpProtocolVersion: 1,
        RealtimeProtocolVersion: 1,
        IpcProtocolVersion: 1)));

app.MapHealthChecks("/health/live");
app.MapHealthChecks("/health/ready");

app.MapFallbackToFile("index.html");

app.Run();

public partial class Program;

