using System.Diagnostics;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using RestfulAPIDemo.Api;
using RestfulAPIDemo.BestStories;
using RestfulAPIDemo.HackerNews;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHackerNewsClient();
builder.Services.AddBestStories();

builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
    context.ProblemDetails.Extensions.TryAdd("traceId", Activity.Current?.Id ?? context.HttpContext.TraceIdentifier));

// Titles and URLs are emitted verbatim (no &-style escaping of '&', '<' or non-ASCII). The output is still
// valid JSON and is only ever served as application/json, never embedded in HTML.
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping);

builder.Services.AddOpenApi(options => options.AddOperationTransformer<BestStoriesOpenApiTransformer>());

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

// The development certificate only exists on developer machines; containers speak plain HTTP behind a proxy.
if (app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

app.MapOpenApi();
app.UseSwaggerUI(options => options.SwaggerEndpoint("/openapi/v1.json", "RestfulAPIDemo v1"));
app.MapBestStories();
app.MapHealthChecks("/health", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = registration => registration.Tags.Contains("ready") });

app.Run();

/// <summary>Exposed so integration tests can host the application with <c>WebApplicationFactory&lt;Program&gt;</c>.</summary>
public partial class Program;
