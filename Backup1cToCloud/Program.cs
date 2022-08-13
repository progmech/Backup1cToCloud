using Backup1cToCloud;
using Backup1cToCloud.Settings;
using Serilog;

using IHost host = Host.CreateDefaultBuilder(args)
    .UseWindowsService()
    .UseSerilog((context, loggerConfiguration) => loggerConfiguration
        .ReadFrom.Configuration(context.Configuration))
    .ConfigureServices((context, services) =>
    {
        services.AddOptions<BackupOptions>()
            .Bind(context.Configuration.GetSection(BackupOptions.BackupOptionsName));
        services.AddHostedService<BackupWorker>();
    })
    .Build();

await host.RunAsync();
