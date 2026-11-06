using SanctionAndForgive.Data;

var builder = WebApplication.CreateBuilder(args);

// The lists database. Screening never touches it (Phase 3 loads indexes from it); EF's tools also find it here.
builder.Services.AddListsDatabase(builder.Configuration.GetConnectionString("Lists"));

var app = builder.Build();

// Placeholder until Phase 3 (endpoints, index load and refresh, website).
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

app.Run();
