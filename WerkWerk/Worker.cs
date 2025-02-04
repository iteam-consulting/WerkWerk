﻿using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;

namespace WerkWerk
{
    using System.Diagnostics;
    using Data;

    public abstract class Worker<T> : BackgroundService
    {
        private readonly IServiceProvider _provider;

        public Worker(IServiceProvider provider)
        {
            _provider = provider;
        }

        protected abstract WorkBuilder<T> Configure(WorkBuilder<T> builder);

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var workerName = GetType().Name;
            var factory = _provider.GetService<ILoggerFactory>();
            var logger = factory.CreateLogger(workerName);
            var builder = Configure(new WorkBuilder<T>());

            var work = builder.Build();
            if (!work)
            {
                logger.LogError($"[{workerName}] Unable to begin work. Worker.Configure method did not configure valid work.");
                return;
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                using var scope = _provider.CreateScope();
                var watch = new Stopwatch();
                var provider = scope.ServiceProvider;
                var repo = provider.GetRequiredService<IWorkRepository>();
                var job = await repo.GetNextJob(work.JobName, work.MaxRetries, stoppingToken);

                if (job)
                {
                    logger.LogInformation($"Job found: {job.Name}");

                    watch.Start();
                    var cleanup = stoppingToken.Register(repo => (repo as IWorkRepository)?.CancelJobSync(job), repo);

                    using (logger.BeginScope(job))
                    {
                        try
                        {
                            await repo.StartJob(job, stoppingToken);
                            var result = await work.Do(WorkContext<T>.FromJob(job, logger, provider, stoppingToken));

                            if (result.Succeeded)
                            {
                                await repo.Completejob(job, stoppingToken);
                            }
                            else
                            {
                                logger.LogError(result.Error);
                                await repo.FailJob(job, stoppingToken);
                            }
                        }
                        catch (Exception ex)
                        {
                            logger.LogError(ex, "Job run failed.");
                            await repo.FailJob(job, stoppingToken);
                        }
                    }

                    cleanup.Unregister();
                    watch.Stop();
                }

                await Task.Delay(Convert.ToInt32(
                    Math.Max(0.0, work.Interval.TotalMilliseconds - watch.Elapsed.TotalMilliseconds)
                ));
            }
        }
    }
}
