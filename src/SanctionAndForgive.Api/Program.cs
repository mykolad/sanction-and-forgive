var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// Placeholder until Phase 3 (endpoints, index load and refresh, website).
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

app.Run();
