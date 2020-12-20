namespace PipelineBuilder
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading.Tasks;
    using Microsoft.Extensions.DependencyInjection;

    public static class Program
    {
        public static async Task Main()
        {
            var provider = new ServiceCollection()
                .AddSingleton<Handler1>()
                .AddPipeline<MyContext>(
                    builder =>
                    {
                        builder
                            .AddStage((context, next) => BigBrother(context, next))
                            .AddStage<Handler1>(handler =>
                            {
                                return async (context, next) =>
                                {
                                    await handler.DoSomethingAsync(context);

                                    await next(context);
                                };
                            })
                            .AddStage<Handler2>(handler =>
                            {
                                return (context, next) => handler.DoSomethingElseAsync(context, next);
                            })
                            .AddStage<Handler1>(handler =>
                            {
                                return (context, next) =>
                                {
                                    handler.DoSomethingSync(context);

                                    return next(context);
                                };
                            });
                    },
                    ServiceLifetime.Singleton)
                .BuildServiceProvider();

            var pipeline = provider.GetRequiredService<IPipeline<MyContext>>();

            await pipeline.ExecuteAsync(new MyContext { Something = "Run1" });

            Console.WriteLine();

            pipeline = provider.GetRequiredService<IPipeline<MyContext>>();

            await pipeline.ExecuteAsync(new MyContext { Something = "Run2" });
        }
        
        private static async Task BigBrother(MyContext context, Func<MyContext, Task> next)
        {
            Console.WriteLine($"[BigBrother] Something before = {context.Something}");

            await next(context);
            
            Console.WriteLine($"[BigBrother] Something after = {context.Something}");
        }
    }

    #region The test classes

    public class MyContext
    {
        public string Something { get; set; }
    }

    public class Handler1
    {
        private readonly Guid id = Guid.NewGuid();

        public Task DoSomethingAsync(MyContext context)
        {
            Console.WriteLine($"[{nameof(Handler1)} {this.id}] Something before = {context.Something}");

            context.Something += $" => {nameof(Handler1)}";

            return Task.CompletedTask;
        }

        public void DoSomethingSync(MyContext context)
        {
            Console.WriteLine($"[{nameof(Handler1)} {this.id}] Something before = {context.Something}");

            context.Something += $" => {nameof(Handler1)}";
        }
    }

    public class Handler2
    {
        private readonly Guid id = Guid.NewGuid();

        public Task DoSomethingElseAsync(MyContext context, Func<MyContext, Task> next)
        {
            Console.WriteLine($"[{nameof(Handler2)} {this.id}] Something before = {context.Something}");

            context.Something += $" => {nameof(Handler2)}";

            return next(context);
        }
    }

    #endregion

    #region The magic

    public static class ServiceCollectionExtensions
    {
        public static IServiceCollection AddPipeline<TContext>(
            this IServiceCollection services,
            Action<IPipelineBuilder<TContext>> configurator,
            ServiceLifetime lifetime)
        {
            var builder = new PipelineBuilder<TContext>();

            configurator(builder);

            services.AddSingleton<IPipelineBuilder<TContext>>(builder);

            services.Add(
                ServiceDescriptor.Describe(
                    typeof(IPipeline<TContext>),
                    provider => provider.GetRequiredService<IPipelineBuilder<TContext>>().Build(provider),
                    lifetime));

            return services;
        }
    }

    public interface IPipeline<TContext>
    {
        Task ExecuteAsync(TContext context);
    }

    public delegate Task StageDelegate<TContext>(TContext context, Func<TContext, Task> next);

    public interface IPipelineBuilder<TContext>
    {
        IPipelineBuilder<TContext> AddStage(StageDelegate<TContext> stage);
        
        IPipelineBuilder<TContext> AddStage<THandler>(Func<THandler, StageDelegate<TContext>> stageFactory);
        
        IPipelineBuilder<TContext> AddStage(Func<IServiceProvider, StageDelegate<TContext>> stageFactory);

        IPipeline<TContext> Build(IServiceProvider serviceProvider);
    }

    public class Pipeline<TContext> : IPipeline<TContext>
    {
        private readonly Func<TContext, Task> pipelineDelegate;

        public Pipeline(IEnumerable<Func<IServiceProvider, StageDelegate<TContext>>> stageFactories, IServiceProvider provider)
        {
            this.pipelineDelegate = BuildPipelineDelegate(stageFactories, provider);
        }

        public Task ExecuteAsync(TContext context)
        {
            return pipelineDelegate(context);
        }

        private static Func<TContext, Task> BuildPipelineDelegate(
            IEnumerable<Func<IServiceProvider, StageDelegate<TContext>>> stageFactories,
            IServiceProvider provider)
        {
            var pipelineDelegate = (Func<TContext, Task>)NullStage;

            foreach (var stageFactory in stageFactories.Reverse())
            {
                var next = pipelineDelegate;
                pipelineDelegate = context => stageFactory(provider)(context, next);
            }

            return pipelineDelegate;
        }


        private static Task NullStage(TContext context)
        {
            return Task.CompletedTask;
        }
    }

    public class PipelineBuilder<TContext> : IPipelineBuilder<TContext>
    {
        private readonly IList<Func<IServiceProvider, StageDelegate<TContext>>> stageFactories;

        public PipelineBuilder()
        {
            this.stageFactories = new List<Func<IServiceProvider, StageDelegate<TContext>>>();
        }

        public IPipelineBuilder<TContext> AddStage(StageDelegate<TContext> stage)
        {
            return this.AddStage(_ => stage);
        }

        public IPipelineBuilder<TContext> AddStage<THandler>(Func<THandler, StageDelegate<TContext>> stageFactory)
        {
            return this.AddStage(provider =>
            {
                var handler = ActivatorUtilities.GetServiceOrCreateInstance<THandler>(provider);

                return stageFactory(handler);
            });
        }

        public IPipelineBuilder<TContext> AddStage(Func<IServiceProvider, StageDelegate<TContext>> stageFactory)
        {
            this.stageFactories.Add(stageFactory);

            return this;
        }

        public IPipeline<TContext> Build(IServiceProvider provider)
        {
            return new Pipeline<TContext>(this.stageFactories, provider);
        }
    }

    #endregion
}
