using DotNetEnv;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using UploadAgent.Services;

namespace UploadAgent;

public class Program
{
    public static void Main(string[] args)
    {
        // Load .env file
        Env.Load();

        var builder = Host.CreateApplicationBuilder(args);

        // Register services
        builder.Services.AddSingleton<AppConfig>();
        builder.Services.AddSingleton<StateManifest>();
        builder.Services.AddSingleton<FileProcessor>();
        builder.Services.AddSingleton<UploadWorkerPool>();
        builder.Services.AddSingleton<WebSocketServer>();
        builder.Services.AddHostedService<AgentWorker>();

        // Windows Service support
        builder.Services.AddWindowsService(options =>
        {
            options.ServiceName = "UploadAgent";
        });

        var host = builder.Build();
        host.Run();
    }
}
