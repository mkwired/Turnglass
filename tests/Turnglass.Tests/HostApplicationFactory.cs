using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Turnglass.Core;
using Turnglass.Infrastructure;

namespace Turnglass.Tests;

internal sealed class HostApplicationFactory(HostFakeViewer viewer, bool development = false, IKnowledgeService? knowledge = null, ICopilotUsageService? copilot = null) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(development ? "Development" : "Production");
        builder.UseContentRoot(AppContext.BaseDirectory);
        builder.UseSetting("data-dir", Path.Combine(Path.GetTempPath(), "Turnglass-host-test"));
        builder.ConfigureTestServices(services =>
        {
            foreach (var service in services.Where(service => service.ServiceType == typeof(IHostedService) && service.ImplementationType?.Assembly == typeof(TurnglassRegistration).Assembly).ToArray())
            {
                services.Remove(service);
            }

            services.RemoveAll<IViewerService>();
            services.AddSingleton<IViewerService>(viewer);
            services.RemoveAll<ICopilotUsageService>();
            services.AddSingleton(copilot ?? new HostFakeCopilot());
            if (knowledge is not null)
            {
                services.RemoveAll<IKnowledgeService>();
                services.AddSingleton(knowledge);
            }
        });
        builder.ConfigureLogging(logging => logging.ClearProviders());
    }
}
