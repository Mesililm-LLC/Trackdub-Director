using System.Text.Json.Serialization;
using Trackdub.Director.Core;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod();
    });
});

// HttpClient used by MetaClient. Registered so tests can replace it with a fake handler.
builder.Services.AddSingleton(_ => new HttpClient(new HttpClientHandler()) { Timeout = TimeSpan.FromSeconds(60) });
builder.Services.AddTransient(sp => new MetaClient(sp.GetRequiredService<HttpClient>()));
builder.Services.AddTransient<TranslationService>(sp => new TranslationService(sp.GetRequiredService<MetaClient>()));
builder.Services.AddTransient<SceneContextService>(sp => new SceneContextService(sp.GetRequiredService<MetaClient>()));
// One provider for the process: it owns a 120-second client sized for audio synthesis.
builder.Services.AddSingleton<ITtsProvider>(_ => new ElevenLabsTtsProvider());

// JSON: keep defaults but ensure case-insensitive reads and respect JsonPropertyName attributes
builder.Services.ConfigureHttpJsonOptions(opts =>
{
    opts.SerializerOptions.PropertyNameCaseInsensitive = true;
    opts.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

var app = builder.Build();

app.UseCors();

// Default files are served only outside development. In dev, static files still work if wwwroot exists
// (after a prod build), but Vite is primary.
if (!app.Environment.IsDevelopment())
{
    app.UseDefaultFiles();
}

app.UseStaticFiles();

// --- API ---

app.MapGet("/api/status", ApiEndpoints.Status);
app.MapGet("/api/models", ApiEndpoints.ModelsAsync);
app.MapPost("/api/translate", ApiEndpoints.TranslateAsync);
app.MapPost("/api/qa", ApiEndpoints.QaAsync);
app.MapPost("/api/scene-context", ApiEndpoints.SceneContextAsync);
app.MapPost("/api/director-adaptation", ApiEndpoints.DirectorAdaptationAsync);
app.MapPost("/api/audition", ApiEndpoints.AuditionAsync);
app.MapFallback(ApiEndpoints.FallbackAsync);

app.Run();

// For WebApplicationFactory in tests
public partial class Program
{
}
