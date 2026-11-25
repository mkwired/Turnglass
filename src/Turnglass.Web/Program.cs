using Turnglass.Infrastructure;
using Turnglass.ServiceDefaults;
using Turnglass.Web.Middleware;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});
if (string.IsNullOrWhiteSpace(builder.Configuration["urls"]) && string.IsNullOrWhiteSpace(builder.Configuration["ASPNETCORE_URLS"]))
{
    builder.WebHost.UseUrls("http://127.0.0.1:5278");
}

builder.AddServiceDefaults();
builder.Services.AddTurnglass(builder.Configuration["data-dir"]);
builder.Services.AddOpenApi();
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Default;
    options.SerializerOptions.NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.Strict;
});
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Default;
        options.JsonSerializerOptions.NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.Strict;
    });
builder.Services.AddProblemDetails();

var app = builder.Build();
app.UseMiddleware<LocalRequestMiddleware>();
app.UseMiddleware<ApiExceptionMiddleware>();
app.MapOpenApi();
app.MapDefaultEndpoints();
app.MapControllers();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapFallbackToFile("index.html");
await app.RunAsync().ConfigureAwait(false);
