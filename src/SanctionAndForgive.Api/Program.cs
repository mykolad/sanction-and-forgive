using SanctionAndForgive.Api;
using SanctionAndForgive.Api.Controllers;
using SanctionAndForgive.Api.Lists;
using SanctionAndForgive.Data;
using SanctionAndForgive.Matching;

var builder = WebApplication.CreateBuilder(args);

// The lists database. Only the refresh reads it; a screening never does.
builder.Services.AddListsDatabase(builder.Configuration.GetConnectionString("Lists"));

var matchingOptions = builder.Configuration.GetSection("Matching").Get<MatchingOptions>() ?? MatchingOptions.Default;
builder.Services.AddSingleton(matchingOptions);
builder.Services.AddSingleton(new Screener(matchingOptions));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(AppInfo.From(builder.Configuration));
builder.Services.AddSingleton<ListCatalog>();
builder.Services.AddSingleton<ListLoader>();
if (builder.Configuration.GetValue("Lists:RefreshEnabled", true))
{
    builder.Services.AddHostedService<ListRefreshService>();
}

builder.Services.AddControllers();
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddScreeningRateLimit(builder.Configuration.GetValue("RateLimiting:PerMinute", 120));

var app = builder.Build();

app.UseForwardedHeaders();
app.UseExceptionHandler();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRateLimiter();
app.MapOpenApi();
app.MapControllers();

app.Run();

/// <summary>For WebApplicationFactory in the HTTP tests.</summary>
public partial class Program;
