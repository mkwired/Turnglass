using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Turnglass.Core;

namespace Turnglass.Infrastructure;

public sealed class ViewerOptions
{
    public string DataDirectory { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Turnglass");

    public IReadOnlyList<string>? Sources { get; init; }

    public IReadOnlyList<CopilotSourceRoot>? CopilotSources { get; init; }
}

public static class TurnglassRegistration
{
    public static IServiceCollection AddTurnglass(this IServiceCollection services, string? dataDirectory = null)
    {
        return services.AddTurnglass(new ViewerOptions
        {
            DataDirectory = Path.GetFullPath(dataDirectory ?? Environment.GetEnvironmentVariable("TURNGLASS_DATA_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Turnglass")),
        });
    }

    public static IServiceCollection AddTurnglass(this IServiceCollection services, ViewerOptions options)
    {
        services.AddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ViewerDatabase>();
        services.AddSingleton<SessionIndexer>();
        services.AddSingleton<IViewerService, ViewerService>();
        services.AddSingleton<KnowledgeDatabase>();
        services.AddSingleton<IKnowledgeService, KnowledgeService>();
        services.AddSingleton<CopilotDatabase>();
        services.AddSingleton<CopilotIndexer>();
        services.AddSingleton<ICopilotUsageService, CopilotUsageService>();
        services.AddHostedService<IndexingWorker>();
        services.AddHostedService<CopilotIndexingWorker>();
        return services;
    }
}

