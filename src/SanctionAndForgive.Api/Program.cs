using SanctionAndForgive.Api;
using SanctionAndForgive.Api.Controllers;
using SanctionAndForgive.Api.Lists;
using SanctionAndForgive.Api.Screening;
using SanctionAndForgive.Data;
using SanctionAndForgive.Matching;

var builder = WebApplication.CreateBuilder(args);

// One JSON object per log line outside Development: Log Analytics keeps each line as one row, so a screening's audit
// line (and its reference) is one searchable record.
if (!builder.Environment.IsDevelopment())
{
    builder.Logging.ClearProviders();
    builder.Logging.AddJsonConsole(json => json.IncludeScopes = false);
}

// The lists database. Only the refresh reads it; a screening never does.
builder.Services.AddListsDatabase(builder.Configuration.GetConnectionString("Lists"));

var matchingOptions = builder.Configuration.GetSection("Matching").Get<MatchingOptions>() ?? MatchingOptions.Default;
builder.Services.AddSingleton(matchingOptions);
builder.Services.AddSingleton(new Screener(matchingOptions));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(AppInfo.From(builder.Configuration));
builder.Services.AddSingleton<ListCatalog>();
builder.Services.AddSingleton<ListLoader>();
builder.Services.AddSingleton<ScreeningService>();
builder.Services.AddSingleton(_ => new ScreeningGate(builder.Configuration.GetValue("Screening:MaxConcurrent", 4), builder.Configuration.GetValue("Screening:MaxWaiting", 256), TimeSpan.FromSeconds(builder.Configuration.GetValue("Screening:MaxWaitSeconds", 2.0))));
// Admin: the Entra sign-in (off unless the deployment names the app registration), and reviewing held updates.
var signInSettings = SanctionAndForgive.Api.Admin.SignInSettings.From(builder.Configuration);
builder.Services.AddSingleton(signInSettings);
builder.Services.AddSingleton(_ => signInSettings.Metadata());
builder.Services.AddSingleton<SanctionAndForgive.Api.Admin.AdminSignIn>();
builder.Services.AddSingleton<SanctionAndForgive.Api.Admin.AdminAccess>();
builder.Services.AddSingleton<SanctionAndForgive.Api.Admin.IImportReview, SanctionAndForgive.Api.Admin.ImportReview>();
builder.Services.AddSingleton<SanctionAndForgive.Api.Admin.IAllowListAdmin, SanctionAndForgive.Api.Admin.AllowListAdmin>();
builder.Services.AddSingleton<SanctionAndForgive.Api.Admin.IListSwitch, SanctionAndForgive.Api.Admin.ListSwitch>();
if (builder.Configuration.GetValue("Lists:RefreshEnabled", true))
{
    builder.Services.AddHostedService<ListRefreshService>();
}

builder.Services.AddControllers();
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddScreeningRateLimit(builder.Configuration.GetValue("RateLimiting:PerMinute", 120), builder.Configuration.GetValue("Screening:ConcurrentStreams", 1));

var app = builder.Build();

app.UseForwardedHeaders();
app.UseExceptionHandler();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRateLimiter();
SanctionAndForgive.Api.Admin.AdminAccess.UseAdminSession(app);
app.MapOpenApi();
app.MapControllers();

app.Run();

/// <summary>For WebApplicationFactory in the HTTP tests.</summary>
public partial class Program;
