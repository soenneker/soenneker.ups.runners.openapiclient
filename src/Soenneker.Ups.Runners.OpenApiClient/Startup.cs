using Microsoft.Extensions.DependencyInjection;
using Soenneker.Managers.Runners.Registrars;
using Soenneker.OpenApi.Fixer.Registrars;
using Soenneker.OpenApi.Merger.Registrars;
using Soenneker.Ups.Runners.OpenApiClient.Utils;
using Soenneker.Ups.Runners.OpenApiClient.Utils.Abstract;
using Soenneker.Utils.Yaml.Registrars;

namespace Soenneker.Ups.Runners.OpenApiClient;

/// <summary>
/// Console type startup
/// </summary>
public static class Startup
{
    // This method gets called by the runtime. Use this method to add services to the container.
    public static void ConfigureServices(IServiceCollection services)
    {
        services.SetupIoC();
    }

    public static IServiceCollection SetupIoC(this IServiceCollection services)
    {
        services.AddHostedService<ConsoleHostedService>()
                .AddScoped<IFileOperationsUtil, FileOperationsUtil>()
                .AddRunnersManagerAsScoped()
                .AddOpenApiMergerAsScoped()
                .AddOpenApiFixerAsScoped()
                .AddYamlUtilAsScoped();

        return services;
    }
}
