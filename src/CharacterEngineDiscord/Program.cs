﻿using CharacterEngine.App;
using CharacterEngine.App.Exceptions;
using CharacterEngine.App.Helpers;
using CharacterEngine.App.Helpers.Infrastructure;
using CharacterEngineDiscord.Models.Db;
using Microsoft.EntityFrameworkCore;
using NLog;

namespace CharacterEngine
{
    internal static class Program
    {
        private static readonly Logger _log = LogManager.GetCurrentClassLogger();


        private static async Task Main(string[] args)
        {
            var nlogPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Settings", "NLog.config");
            LogManager.Configuration = new NLog.Config.XmlLoggingConfiguration(nlogPath);

            _log.Info("[ Starting Character Engine ]");

            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                _log.Info("[ Character Engine Stopped ]");
            };

            AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
            {
                _log.Error($"Unhandled exception: {sender}\n{e.ExceptionObject}");
            };

            TaskScheduler.UnobservedTaskException += (sender, e) =>
            {
                if (e.Exception.InnerException is UserFriendlyException)
                {
                    return;
                }

                _log.Error($"Unobserved task exception: {sender}\n{e.Exception}");
            };

            BotConfig.Initialize();

            await using (var db = DatabaseHelper.GetDbContext())
            {
                await db.Database.MigrateAsync();
            }

            MetricsWriter.Create(MetricType.ApplicationLaunch);
            AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

            await CharacterEngineBot.RunAsync(args.All(arg => arg != "no-update"));
        }

    }
}
