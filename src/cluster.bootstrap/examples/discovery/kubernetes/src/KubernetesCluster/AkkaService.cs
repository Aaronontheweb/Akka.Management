﻿using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Akka.Actor;
using Akka.Bootstrap.Docker;
using Akka.Cluster.Tools.PublishSubscribe;
using Akka.Configuration;
using Akka.Coordination.KubernetesApi;
using Akka.DependencyInjection;
using Akka.Event;
using Akka.Management;
using Akka.Management.Cluster.Bootstrap;
using Akka.Util;
using Akka.Util.Internal;
using Microsoft.Extensions.Hosting;
using Petabridge.Cmd.Cluster;
using Petabridge.Cmd.Host;
using Petabridge.Cmd.Remote;

namespace KubernetesCluster
{
    public sealed class ChaosActor : ReceiveActor
    {
        public ChaosActor()
        {
            var log = Context.GetLogger();
            
            ReceiveAsync<int>(async i =>
            {
                log.Info($"Received {i}");
                switch (i)
                {
                    case 1: // graceful shutdown
                        log.Error("======== Shutting down gracefully ========");
                        await Task.Delay(100);
                        await Context.System.Terminate();
                        return;
                    case 2: // crash
                        log.Error("======== Crashing system ========");
                        await Task.Delay(100);
                        Context.System.AsInstanceOf<ExtendedActorSystem>().Abort();
                        return;
                }
            });
        }
    }
    
    public class Subscriber : ReceiveActor
    {
        private readonly ILoggingAdapter _log = Context.GetLogger();

        public Subscriber()
        {
            var mediator = DistributedPubSub.Get(Context.System).Mediator;

            // subscribe to the topic named "content"
            mediator.Tell(new Subscribe("content", Self));

            Receive<int>(s =>
            {
                _log.Info($"Got {s}");
                if (s % 2 == 0)
                {
                    mediator.Tell(new Publish("content", ThreadLocalRandom.Current.Next(0,10)));
                }
            });

            Receive<SubscribeAck>(subscribeAck =>
            {
                if (subscribeAck.Subscribe.Topic.Equals("content")
                    && subscribeAck.Subscribe.Ref.Equals(Self)
                    && subscribeAck.Subscribe.Group == null)
                {
                    _log.Info("subscribing");
                }
            });
        }
    }
    
    public class AkkaService: IHostedService
    {
        private ActorSystem _system;
        public Task TerminationHandle => _system.WhenTerminated;
        private readonly IServiceProvider _serviceProvider;

        // needed to help guarantee clean shutdowns
        private readonly IHostApplicationLifetime _lifetime;

        public IActorRef ClusterListener {get; private set;}

        public AkkaService(IServiceProvider serviceProvider, IHostApplicationLifetime lifetime)
        {
            _serviceProvider = serviceProvider;
            _lifetime = lifetime;
        }
        
        public Task StartAsync(CancellationToken cancellationToken)
        {
            var config = ConfigurationFactory.ParseString(File.ReadAllText("app.conf"))
                .BootstrapFromDocker()
                .WithFallback(KubernetesLease.DefaultConfiguration);
                
            var bootstrap = BootstrapSetup.Create()
                .WithConfig(config) // load HOCON
                .WithActorRefProvider(ProviderSelection.Cluster.Instance); // launch Akka.Cluster

            // enable DI support inside this ActorSystem, if needed
            var diSetup = DependencyResolverSetup.Create(_serviceProvider);

            // merge this setup (and any others) together into ActorSystemSetup
            var actorSystemSetup = bootstrap.And(diSetup);
            var systemName = Environment.GetEnvironmentVariable("ACTORSYSTEM")?.Trim();

            _system = ActorSystem.Create(systemName, actorSystemSetup);

            var chaos = _system.ActorOf(Props.Create<ChaosActor>(), "chaos");
            var subscriber = _system.ActorOf(Props.Create(() => new Subscriber()), "subscriber");

            // start https://cmd.petabridge.com/ for diagnostics and profit
            var pbm = PetabridgeCmd.Get(_system); // start Pbm
            pbm.RegisterCommandPalette(ClusterCommands.Instance);
            pbm.RegisterCommandPalette(new RemoteCommands());
            pbm.Start(); // begin listening for PBM management commands
            
            ClusterListener = _system.ActorOf(KubernetesCluster.ClusterListener.Props(), "listener");

            
            _system.WhenTerminated.ContinueWith(tr =>
            {
                _lifetime.StopApplication(); // when the ActorSystem terminates, terminate the process
            });

            var mediator = DistributedPubSub.Get(_system).Mediator;
            
            _system.Scheduler.Advanced.ScheduleRepeatedly(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), () =>
            {
               // mediator.Tell(new Publish("content", ThreadLocalRandom.Current.Next(0,10)));
                chaos.Tell(ThreadLocalRandom.Current.Next(0,200));
            });

            return Task.CompletedTask;
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            await CoordinatedShutdown.Get(_system).Run(CoordinatedShutdown.ClrExitReason.Instance);
        }
    }
}