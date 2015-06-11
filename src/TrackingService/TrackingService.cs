﻿namespace TrackingService
{
    using System;
    using System.Configuration;
    using Automatonymous;
    using MassTransit;
    using MassTransit.NHibernateIntegration.Saga;
    using MassTransit.Policies;
    using MassTransit.RabbitMqTransport;
    using MassTransit.Saga;
    using NHibernate;
    using Topshelf;
    using Topshelf.Logging;
    using Tracking;


    class TrackingService :
        ServiceControl
    {
        readonly LogWriter _log = HostLogger.Get<TrackingService>();
        RoutingSlipMetrics _activityMetrics;

        IBusControl _busControl;
        BusHandle _busHandle;
        RoutingSlipStateMachine _machine;
        RoutingSlipMetrics _metrics;
        SQLiteSessionFactoryProvider _provider;
        ISagaRepository<RoutingSlipState> _repository;
        ISessionFactory _sessionFactory;

        public bool Start(HostControl hostControl)
        {
            _log.Info("Creating bus...");

            _metrics = new RoutingSlipMetrics("Routing Slip");
            _activityMetrics = new RoutingSlipMetrics("Validate Activity");

            _machine = new RoutingSlipStateMachine();
            _provider = new SQLiteSessionFactoryProvider(false, typeof(RoutingSlipStateSagaMap));
            _sessionFactory = _provider.GetSessionFactory();

            _repository = new NHibernateSagaRepository<RoutingSlipState>(_sessionFactory);

            _busControl = Bus.Factory.CreateUsingRabbitMq(x =>
            {
                IRabbitMqHost host = x.Host(new Uri(ConfigurationManager.AppSettings["RabbitMQHost"]), h =>
                {
                    h.Username("guest");
                    h.Password("guest");
                });

                x.ReceiveEndpoint(host, "routing_slip_metrics", e =>
                {
                    e.PrefetchCount = 100;
                    e.Retry(Retry.None);
                    e.Consumer(() => new RoutingSlipMetricsConsumer(_metrics));
                });

                x.ReceiveEndpoint(host, "routing_slip_activity_metrics", e =>
                {
                    e.PrefetchCount = 100;
                    e.Retry(Retry.None);
                    e.Consumer(() => new RoutingSlipActivityConsumer(_activityMetrics, "Validate"));
                });

                x.ReceiveEndpoint(host, "routing_slip_state", e =>
                {
                    e.PrefetchCount = 10;

                    e.StateMachineSaga(_machine, _repository);
                });
            });

            _log.Info("Starting bus...");

            _busHandle = _busControl.Start();

            return true;
        }

        public bool Stop(HostControl hostControl)
        {
            _log.Info("Stopping bus...");

            if (_busHandle != null)
                _busHandle.Stop(TimeSpan.FromSeconds(30));

            return true;
        }
    }
}