using System.Text.Json.Serialization;
using FireData.Api.Endpoints;
using FireData.Api.Services;
using FireData.Cli;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("FireData")
    ?? $"Data Source={Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "../../data/fire-observations.sqlite"))};Foreign Keys=True";

builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddDbContext<FireDataContext>(options => options.UseSqlite(connectionString));
builder.Services.AddScoped<IObservationSearchService, ObservationSearchService>();
builder.Services.AddScoped<IObservationTimeSeriesService, ObservationTimeSeriesService>();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseAntiforgery();

app.MapObservationEndpoints();
app.MapStaticAssets();
app.MapRazorComponents<FireData.Api.Components.App>()
    .AddInteractiveServerRenderMode();

app.Run();
