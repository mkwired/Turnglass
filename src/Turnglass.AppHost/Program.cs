var builder = DistributedApplication.CreateBuilder(args);

var api = builder.AddDotnetProject("api", "../Turnglass.Web/Turnglass.Web.csproj")
    .WithHttpHealthCheck("/health");

var frontend = builder.AddViteApp("frontend", "../../frontend")
    .WithReference(api)
    .WaitFor(api);

api.WithEnvironment("Turnglass__FrontendOrigin", frontend.GetEndpoint("http"));

await builder.Build()
    .RunAsync()
    .ConfigureAwait(false);
