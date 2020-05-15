/*
 * Copyright 2018 Amazon.com, Inc. or its affiliates. All Rights Reserved.
 * 
 * Licensed under the Apache License, Version 2.0 (the "License").
 * You may not use this file except in compliance with the License.
 * A copy of the License is located at
 * 
 *  http://aws.amazon.com/apache2.0
 * 
 * or in the "license" file accompanying this file. This file is distributed
 * on an "AS IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either
 * express or implied. See the License for the specific language governing
 * permissions and limitations under the License.
 */
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;
using NLog.Extensions.Logging;

using Amazon.KinesisTap.Core;
using Amazon.KinesisTap.Core.Metrics;

namespace Amazon.KinesisTap.Hosting
{
    public class LogManager
    {
        private const string KINESISTAP_METRICS_SOURCE = "_KinesisTapMetricsSource";
        private const string KINESISTAP_TELEMETRICS_SOURCE = "_KinesisTapTelemetricsSource";
        private const string TELEMETRICS = "Telemetrics";

        private readonly ITypeLoader _typeLoader;
        private readonly IParameterStore _parameterStore;
        private readonly IFactoryCatalog<ISource> _sourceFactoryCatalog = new FactoryCatalog<ISource>();
        private readonly IFactoryCatalog<IEventSink> _sinkFactoryCatalog = new FactoryCatalog<IEventSink>();
        private readonly IFactoryCatalog<ICredentialProvider> _credentialProviderFactoryCatalog = new FactoryCatalog<ICredentialProvider>();
        private readonly IFactoryCatalog<IGenericPlugin> _genericPluginFactoryCatalog = new FactoryCatalog<IGenericPlugin>();
        private readonly IFactoryCatalog<IPipe> _pipeFactoryCatalog = new FactoryCatalog<IPipe>();
        private readonly IFactoryCatalog<IRecordParser> _recordParserCatalog = new FactoryCatalog<IRecordParser>(); 

        private readonly IDictionary<string, ISource> _sources = new Dictionary<string, ISource>();
        private readonly IDictionary<string, ISink> _sinks = new Dictionary<string, ISink>();
        private readonly IDictionary<string, ICredentialProvider> _credentialProviders = new Dictionary<string, ICredentialProvider>();
        private readonly IList<IDisposable> _subscriptions = new List<IDisposable>();
        private readonly IList<IGenericPlugin> _plugins = new List<IGenericPlugin>();
        private readonly IConfigurationRoot _config;
        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger _logger;
        private KinesisTapMetricsSource _metrics;

        private int _updateFrequency;
        private Timer _updateTimer;
        private Timer _configTimer;
        private DateTime _configLoadTime;
        private DateTime _configUpdateTime;

        public LogManager(ITypeLoader typeLoader, IParameterStore parameterStore)
        {
            _typeLoader = typeLoader;
            _parameterStore = parameterStore;

            _loggerFactory = CreateLoggerFactory();
            _logger = _loggerFactory.CreateLogger<LogManager>();
            try
            {
                ConfigurationBuilder configurationBuilder = new ConfigurationBuilder();
                _config = configurationBuilder
                    .SetBasePath(Utility.GetKinesisTapConfigPath())
                    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                    .Build();

            }
            catch(Exception ex)
            {
                _logger.LogError($"Unable to load apsettings.json. {ex.ToMinimized()}");
                throw;
            }

            ChangeToken.OnChange(() => _config.GetReloadToken(), OnConfigChanged);

            _updateTimer = new Timer(CheckUpdate, null, Timeout.Infinite, Timeout.Infinite);
            _configTimer = new Timer(CheckConfig, null, Timeout.Infinite, Timeout.Infinite);
        }

        #region public methods
        public void Start()
        {
            // Clear some of the dictionaries in case there were lingering objects not cleaned up during stop.
            _sources.Clear();
            _sinks.Clear();
            _subscriptions.Clear();

            _configLoadTime = DateTime.Now;

            StackTraceMinimizerExceptionExtensions.DoCompressStackTrace = true;

            _metrics = new KinesisTapMetricsSource(CreatePlugInContext(_config.GetSection("Metrics")));

            PluginContext.ApplicationContext = new PluginContext(_config, _logger, _metrics);

            _sources[KINESISTAP_METRICS_SOURCE] = _metrics;

            LoadFactories();

            LoadCredentialProviders();

            LoadBuiltInSinks();

            LoadEventSinks();

            (int sourcesLoaded, int sourcesFailed) = LoadEventSources();

            LoadPipes();

            StartEventSources(sourcesLoaded, sourcesFailed);

            PublishBuilderNumber();

            LoadSelfUpdator();

            LoadConfigTimer();

            LoadGenericPlugins();
        }

        public void Stop()
        {
            this.Stop(false);
        }

        public void Stop(bool serviceStopping)
        {
            _configTimer.Change(Timeout.Infinite, Timeout.Infinite);
            _updateTimer.Change(Timeout.Infinite, Timeout.Infinite);

            var sourceStopTasks = new List<Task>();
            foreach (ISource source in _sources.Values)
            {
                sourceStopTasks.Add(Task.Run(() =>
                {
                    try
                    {
                        source.Stop();
                        if (source is IDisposable disposableSource)
                            disposableSource.Dispose();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError($"Error stopping source {source.Id}: {ex.ToMinimized()}");
                    }
                }));
            }

            if (!serviceStopping)
            {
                // Wait for items to stop, then move on if even if they haven't within the time limit.
                if (Task.WaitAll(sourceStopTasks.ToArray(), TimeSpan.FromSeconds(300)))
                    _logger.LogDebug("All sources stopped gracefully.");
                _sources.Clear();
            }

            var subscriptionStopTasks = new List<Task>();
            foreach(var subscription in _subscriptions)
            {
                subscriptionStopTasks.Add(Task.Run(() =>
                {
                    try
                    {
                        subscription?.Dispose();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError($"Error stopping subscription: {ex.ToMinimized()}");
                    }
                }));
            }

            if (!serviceStopping)
            {
                // Wait for items to stop, then move on if even if they haven't within the time limit.
                if (Task.WaitAll(subscriptionStopTasks.ToArray(), TimeSpan.FromSeconds(300)))
                    _logger.LogDebug("All subscriptions stopped gracefully.");
                _subscriptions.Clear();
            }

            // Since the sinks need a longer time to flush, we'll stop all the plugins at the same time.
            var sinksAndPluginStopTasks = new List<Task>();
            foreach (ISink sink in _sinks.Values)
            {
                sinksAndPluginStopTasks.Add(Task.Run(() =>
                {
                    try
                    {
                        sink.Stop();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError($"Error stopping sink {sink.Id}: {ex.ToMinimized()}");
                    }
                }));
            }
            
            foreach (var plugin in _plugins)
            {
                sinksAndPluginStopTasks.Add(Task.Run(() =>
                {
                    try
                    {
                        plugin.Stop();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError($"Error stopping plugin {plugin.GetType()}: {ex.ToMinimized()}");
                    }
                }));
            }

            // If the sinks don't flush in time, the class that invoked the Stop method
            // will be logging an event, so we don't need to do so here. We want to give them
            // as long as possible to shut down, so we'll set a super high value, which will cover
            // the use case of both a timeLimited and a non-timeLimited Stop operation.
            if (Task.WaitAll(sinksAndPluginStopTasks.ToArray(), TimeSpan.FromSeconds(600)))
                _logger.LogDebug("All sinks stopped gracefully.");
            _sinks.Clear();

            NetworkStatus.ResetNetworkStatusProviders();
            _logger.LogInformation("Log manager stopped.");
        }

        public int ConfigInterval { get; set; } = 10000; //default to 10 seconds
        #endregion

        #region private methods
        private void OnConfigChanged()
        {
            _configUpdateTime = DateTime.Now;
            _logger.LogInformation("Config file changed.");
        }

        private static ILoggerFactory CreateLoggerFactory()
        {
            ILoggerFactory loggerFactory = new LoggerFactory();
            loggerFactory.AddNLog();
            NLog.LogManager.LoadConfiguration(Path.Combine(Utility.GetKinesisTapConfigPath(), "NLog.xml"));
            return loggerFactory;
        }

        private void LoadCredentialProviders()
        {
            var credentialsSection = _config.GetSection("Credentials");
            var credentialSections = credentialsSection.GetChildren();
            int credentialStarted = 0;
            int credentialFailed = 0;
            foreach (var credentialSection in credentialSections)
            {
                string id = _config.GetChildConfig(credentialSection.Path, ConfigConstants.ID);
                string credentialType = _config.GetChildConfig(credentialSection.Path, ConfigConstants.CREDENTIAL_TYPE);
                var factory = _credentialProviderFactoryCatalog.GetFactory(credentialType);
                if (factory != null)
                {
                    try
                    {
                        ICredentialProvider credentialProvider = factory.CreateInstance(credentialType, CreatePlugInContext(credentialSection));
                        credentialProvider.Id = id;
                        _credentialProviders[id] = credentialProvider;
                        credentialStarted++;
                    }
                    catch (Exception ex)
                    {
                        credentialFailed++;
                        _logger.LogError($"Unable to load event sink {id} exception {ex.ToMinimized()}");
                    }
                }
                else
                {
                    credentialFailed++;
                    _logger.LogError("Credential Type {0} is not recognized.", credentialType);
                }
            }
            _metrics.PublishCounters(string.Empty, MetricsConstants.CATEGORY_PROGRAM, CounterTypeEnum.CurrentValue, new Dictionary<string, MetricValue>()
                {
                    { MetricsConstants.SINKS_STARTED, new MetricValue(credentialStarted) },
                    { MetricsConstants.SINKS_FAILED_TO_START, new MetricValue(credentialFailed) }
                });
        }

        private void LoadFactories()
        {
            LoadFactories<IEventSink>(_sinkFactoryCatalog, (loaded, failed) => {
                _metrics.PublishCounters(string.Empty, MetricsConstants.CATEGORY_PROGRAM, CounterTypeEnum.CurrentValue, new Dictionary<string, MetricValue>()
                {
                    { MetricsConstants.SINK_FACTORIES_LOADED, new MetricValue(loaded) },
                    { MetricsConstants.SINK_FACTORIES_FAILED_TO_LOAD, new MetricValue(failed) }
                });
            });

            LoadFactories<IRecordParser>(_recordParserCatalog, (loaded, failed) =>
            {
                _metrics.PublishCounters(string.Empty, MetricsConstants.CATEGORY_PROGRAM, CounterTypeEnum.CurrentValue, new Dictionary<string, MetricValue>()
                {
                    { MetricsConstants.PARSER_FACTORIES_LOADED, new MetricValue(loaded) },
                    { MetricsConstants.PARSER_FACTORIES_FAILED_TO_LOAD, new MetricValue(failed) }
                });
            });

            LoadFactories<ISource>(_sourceFactoryCatalog, (loaded, failed) => {
                _metrics.PublishCounters(string.Empty, MetricsConstants.CATEGORY_PROGRAM, CounterTypeEnum.CurrentValue, new Dictionary<string, MetricValue>()
                {
                    { MetricsConstants.SOURCE_FACTORIES_LOADED, new MetricValue(loaded) },
                    { MetricsConstants.SOURCE_FACTORIES_FAILED_TO_LOAD, new MetricValue(failed) }
                });
            });

            LoadFactories<IPipe>(_pipeFactoryCatalog, (loaded, failed) =>
            {
                _metrics.PublishCounters(string.Empty, MetricsConstants.CATEGORY_PROGRAM, CounterTypeEnum.CurrentValue, new Dictionary<string, MetricValue>()
                {
                    { MetricsConstants.PIPE_FACTORIES_LOADED, new MetricValue(loaded) },
                    { MetricsConstants.PIPE_FACTORIES_FAILED_TO_LOAD, new MetricValue(failed) }
                });
            });

            LoadFactories<ICredentialProvider>(_credentialProviderFactoryCatalog, (loaded, failed) => {
                _metrics.PublishCounters(string.Empty, MetricsConstants.CATEGORY_PROGRAM, CounterTypeEnum.CurrentValue, new Dictionary<string, MetricValue>()
                {
                    { MetricsConstants.CREDENTIAL_PROVIDER_FACTORIES_LOADED, new MetricValue(loaded) },
                    { MetricsConstants.CREDENTIAL_PROVIDER_FACTORIES_FAILED_TO_LOAD, new MetricValue(failed) }
                });
            });

            LoadFactories<IGenericPlugin>(_genericPluginFactoryCatalog, (loaded, failed) => {
                _metrics.PublishCounters(string.Empty, MetricsConstants.CATEGORY_PROGRAM, CounterTypeEnum.CurrentValue, new Dictionary<string, MetricValue>()
                {
                    { MetricsConstants.GENERIC_PLUGIN_FACTORIES_LOADED, new MetricValue(loaded) },
                    { MetricsConstants.GENERIC_PLUGIN_FACTORIES_FAILED_TO_LOAD, new MetricValue(failed) }
                });
            });
        }

        internal void LoadFactories<T>(IFactoryCatalog<T> catalog, Action<int, int> writeMetrics)
        {
            int loaded = 0;
            int failed = 0;
            try
            {
                var factories = _typeLoader.LoadTypes<IFactory<T>>();
                foreach (IFactory<T> factory in factories)
                {
                    try
                    {
                        factory.RegisterFactory(catalog);
                        loaded++;
                        _logger.LogInformation("Registered factory {0}.", factory);
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        _logger.LogError("Failed to register factory {0}: {1}.", factory, ex.ToMinimized());
                    }
                }
            }
            catch (Exception ex)
            {
                failed++;
                _logger.LogError("Error discovering IFactory<{0}>: {1}.", typeof(T), ex.ToMinimized());
                // If the problem discovering the factory is a missing type then provide more details to make debugging easier.
                if (ex is System.Reflection.ReflectionTypeLoadException)
                {
                    _logger.LogError("Loader exceptions: {0}",
                        string.Join(", ",
                        ((System.Reflection.ReflectionTypeLoadException)ex).LoaderExceptions.Select(exception => exception.ToMinimized()).ToArray()));
                }
            }
            writeMetrics(loaded, failed);
        }

        private (int sourceLoaded, int sourcesFailed) LoadEventSources()
        {
            var sourcesSection = _config.GetSection("Sources");
            var sourceSections = sourcesSection.GetChildren();
            int sourcesLoaded = 0;
            int sourcesFailed = 0;

            foreach (var sourceSection in sourceSections)
            {
                string id = _config.GetChildConfig(sourceSection.Path, ConfigConstants.ID);
                string sourceType = _config.GetChildConfig(sourceSection.Path, ConfigConstants.SOURCE_TYPE);
                var factory = _sourceFactoryCatalog.GetFactory(sourceType);
                if (factory != null)
                {
                    try
                    {
                        ISource source = (ISource)factory.CreateInstance(sourceType, CreatePlugInContext(sourceSection));
                        source.Id = id;
                        _sources[id] = source;
                        sourcesLoaded++;
                    }
                    catch (Exception ex)
                    {
                        sourcesFailed++;
                        _logger.LogError("Unable to load event source {0} exception {1}", id, ex.ToMinimized());
                    }
                }
                else
                {
                    _logger.LogError("Source Type {0} is not recognized.", sourceType);
                }
            }
            return (sourcesLoaded, sourcesFailed);
        }

        private void StartEventSources(int sourceLoaded, int sourcesFailed)
        {
            int sourceStarted = 0;
            foreach(var source in _sources.Values)
            {
                try
                {
                    source.Start();
                    sourceStarted++;
                }
                catch(Exception ex)
                {
                    sourceLoaded--;
                    sourcesFailed++;
                    _logger.LogError("Unable to load event source {0} exception {1}", source.Id, ex.ToMinimized());
                }
            }
            _metrics.PublishCounters(string.Empty, MetricsConstants.CATEGORY_PROGRAM, CounterTypeEnum.CurrentValue, new Dictionary<string, MetricValue>()
                {
                    { MetricsConstants.SOURCES_STARTED, new MetricValue(sourceStarted) },
                    { MetricsConstants.SOURCES_FAILED_TO_START, new MetricValue(sourcesFailed) }
                });
        }

        private void LoadEventSinks()
        {
            var sinksSection = _config.GetSection("Sinks");
            var sinkSections = sinksSection.GetChildren();
            int sinksStarted = 0;
            int sinksFailed = 0;
            foreach (var sinkSection in sinkSections)
            {
                string id = _config.GetChildConfig(sinkSection.Path, ConfigConstants.ID);
                string sinkType = _config.GetChildConfig(sinkSection.Path, ConfigConstants.SINK_TYPE);
                var factory = _sinkFactoryCatalog.GetFactory(sinkType);
                if (factory != null)
                {
                    try
                    {
                        if (string.IsNullOrWhiteSpace(id))
                        {
                            throw new Exception("Sink id is required.");
                        }

                        IEventSink sink = factory.CreateInstance(sinkType, CreatePlugInContext(sinkSection));
                        sink.Start();
                        _sinks[sink.Id] = sink;
                        sinksStarted++;
                    }
                    catch (Exception ex)
                    {
                        sinksFailed++;
                        _logger.LogError($"Unable to load event sink {id} exception {ex}");
                    }
                }
                else
                {
                    sinksFailed++;
                    _logger.LogError("Sink Type {0} is not recognized.", sinkType);
                }
            }
            _metrics.PublishCounters(string.Empty, MetricsConstants.CATEGORY_PROGRAM, CounterTypeEnum.CurrentValue, new Dictionary<string, MetricValue>()
                {
                    { MetricsConstants.SINKS_STARTED, new MetricValue(sinksStarted) },
                    { MetricsConstants.SINKS_FAILED_TO_START, new MetricValue(sinksFailed) }
                });
        }

        private void LoadPipes()
        {
            var pipesSection = _config.GetSection("Pipes");
            var pipeSections = pipesSection.GetChildren();
            int pipesConnected = 0;
            int pipesFailedToConnect = 0;
            foreach (var pipeSection in pipeSections)
            {
                if (LoadPipe(pipeSection))
                {
                    pipesConnected++;
                }
                else
                {
                    pipesFailedToConnect++;
                }
            }

            //If telemetry is redirected, connect it to sink
            IConfiguration telemetricsSection = _config.GetSection(TELEMETRICS);
            string redirectToSinkId = telemetricsSection[ConfigConstants.REDIRECT_TO_SINK_ID];
            if (!string.IsNullOrWhiteSpace(redirectToSinkId))
            {
                if (ConnectTelemetry(redirectToSinkId))
                {
                    pipesConnected++;
                }
                else
                {
                    pipesFailedToConnect++;
                }
            }

            _metrics.PublishCounters(string.Empty, MetricsConstants.CATEGORY_PROGRAM, CounterTypeEnum.CurrentValue, new Dictionary<string, MetricValue>()
                {
                    { MetricsConstants.PIPES_CONNECTED, new MetricValue(pipesConnected) },
                    { MetricsConstants.PIPES_FAILED_TO_CONNECT, new MetricValue(pipesFailedToConnect) }
                });
        }

        private bool ConnectTelemetry(string redirectToSinkId)
        {
            try
            {
                if (!_sinks.TryGetValue(redirectToSinkId, out ISink sink))
                {
                    _logger.LogError($"Sink {redirectToSinkId} not found for telemetry.");
                    return false;
                }
                else
                {
                    ((IEventSource)_sources[ConfigConstants.TELEMETRY_CONNECTOR]).Subscribe((IEventSink)sink);
                    return true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Unable to connect telemetry to Sink {redirectToSinkId} failed: {ex.ToMinimized()}");
                return true;
            }
        }

        private bool LoadPipe(IConfigurationSection config)
        {
            string id = config[ConfigConstants.ID];
            string sourceRef = config["SourceRef"];
            string sinkRef = config["SinkRef"];
            string pipeType = config[ConfigConstants.TYPE];
            try
            {
                if (string.IsNullOrEmpty(sinkRef))
                {
                    _logger.LogError($"SinkRef is required for pipe id {id}");
                    return false;
                }

                if (!_sinks.TryGetValue(sinkRef, out ISink sink))
                {
                    _logger.LogError($"SinkRef {sinkRef} not found for pipe id {id}");
                    return false;
                }

                if (!string.IsNullOrEmpty(sourceRef))
                {
                    if (!_sources.TryGetValue(sourceRef, out ISource source))
                    {
                        _logger.LogError($"Unable to connect source {sourceRef} to sink {sinkRef}.");
                        _logger.LogError($"SourceRef {sourceRef} not found for pipe id {id}");
                        return false;
                    }

                    if (source is IEventSource eventSource && sink is IEventSink eventSink)
                    {
                        if (string.IsNullOrWhiteSpace(pipeType)) //No type specified, just connect sink to source directly
                        {
                            _subscriptions.Add(eventSource.Subscribe(eventSink));
                        }
                        else
                        {
                            return LoadPipe(config, id, pipeType, eventSource, eventSink);
                        }
                    }
                    else if (source is IDataSource<object> dataSource && sink is IDataSink<object> dataSink)
                    {
                        dataSink.RegisterDataSource(dataSource);
                    }
                    else
                    {
                        _logger.LogError($"Unable to connect SourceRef {sourceRef} to SinkRef {sinkRef} for pipe id {id}");
                        return false;
                    }
                }

                _logger.LogInformation($"Connected source {sourceRef} to sink {sinkRef}");
                return true;
            }
            catch(Exception ex)
            {
                _logger.LogError($"Unable to connect source {sourceRef} to sink {sinkRef}. Error: {ex.ToMinimized()}");
                return false;
            }
        }

        private bool LoadPipe(IConfigurationSection config, string id, string pipeType, IEventSource eventSource, IEventSink eventSink)
        {
            var factory = _pipeFactoryCatalog.GetFactory(pipeType);
            if (factory != null)
            {
                try
                {
                    var plugInContext = CreatePlugInContext(config);
                    plugInContext.ContextData[PluginContext.SOURCE_TYPE] = eventSource.GetType();
                    plugInContext.ContextData[PluginContext.SINK_TYPE] = eventSink.GetType();
                    IPipe pipe = factory.CreateInstance(pipeType, plugInContext);
                    pipe.Start();
                    _subscriptions.Add(eventSource.Subscribe(pipe));
                    _subscriptions.Add(pipe.Subscribe(eventSink));
                    return true;
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Unable to load event pipe {id} exception {ex.ToMinimized()}");
                    return false;
                }
            }
            else
            {
                _logger.LogError("Pipe Type {0} is not recognized.", pipeType);
                return false;
            }
        }

        private void LoadSelfUpdator()
        {
            string selfUpdate = _config["SelfUpdate"];
            if (int.TryParse(selfUpdate, out _updateFrequency) && _updateFrequency > 0)
            {
                int dueTime = (int)(Utility.Random.NextDouble() * _updateFrequency * 60000);
                _updateTimer.Change(dueTime, _updateFrequency * 60000);
            }
            _metrics.PublishCounter(string.Empty, MetricsConstants.CATEGORY_PROGRAM, CounterTypeEnum.CurrentValue, MetricsConstants.SELF_UPDATE_FREQUENCY, _updateFrequency, MetricUnit.Seconds);
        }

        private void CheckUpdate(object stateInfo)
        {
#if !DEBUG
            _updateTimer.Change(Timeout.Infinite, Timeout.Infinite);

            try
            {
                _logger.LogInformation("Running self-updater");
                ProcessStartInfo startInfo = new ProcessStartInfo("choco", "upgrade KinesisTap -y");
                startInfo.CreateNoWindow = true;
                Process.Start(startInfo);
            }
            catch(Exception ex)
            {
                _logger.LogError($"KinesisTap update error: {ex.ToMinimized()}");
            }

            _updateTimer.Change(_updateFrequency * 60000, _updateFrequency * 60000);
#endif
        }

        private void LoadConfigTimer()
        {
            _configTimer.Change(ConfigInterval, ConfigInterval);
        }

        private void CheckConfig(object state)
        {
            _configTimer.Change(Timeout.Infinite, Timeout.Infinite);
            if (_configUpdateTime > _configLoadTime)
            {
                Reload();
            }
            _configTimer.Change(ConfigInterval, ConfigInterval);
        }

        private void Reload()
        {
            int configReloadSuccess = 0;
            int configReloadFail = 0;
            try
            {
                this.Stop();
                this.Start();
                configReloadSuccess++;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Reload exception: {ex.ToMinimized()}");
                configReloadFail++;
            }
            _metrics.PublishCounters(string.Empty, MetricsConstants.CATEGORY_PROGRAM, CounterTypeEnum.CurrentValue, new Dictionary<string, MetricValue>()
                {
                    { MetricsConstants.CONFIG_RELOAD_COUNT, new MetricValue(configReloadSuccess) },
                    { MetricsConstants.CONFIG_RELOAD_FAILED_COUNT, new MetricValue(configReloadFail) }
                });
        }

        private void LoadBuiltInSinks()
        {
            CreatePerformanceCounterSink();

            CreateTelemetricsSink();
        }

        private void CreateTelemetricsSink()
        {
            try
            {
                IConfiguration telemetricsSection = _config.GetSection(TELEMETRICS);
                if ("true".Equals(telemetricsSection["off"]))
                {
                    return;
                }
                var factory = _sinkFactoryCatalog.GetFactory(TELEMETRICS);
                if (factory != null)
                {
                    var context = CreatePlugInContext(telemetricsSection);
                    var sink = factory.CreateInstance(TELEMETRICS, context);
                    sink.Start();
                    _sinks["_" + TELEMETRICS] = sink;
                    _subscriptions.Add(_metrics.Subscribe(sink));
                    if (context.ContextData.TryGetValue(ConfigConstants.TELEMETRY_CONNECTOR, out object telemetryConnector))
                    {
                        _sources[ConfigConstants.TELEMETRY_CONNECTOR] = (ISource)telemetryConnector;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Unable to load telemetrics. Error: {ex.Message}"); //Only send a brief error message at Error level
                _logger.LogDebug($"{ex.ToMinimized()}"); //Send the detailed message if the user has Debug level on.
            }
        }

        private void CreatePerformanceCounterSink()
        {
            const string PERFORMANCE_COUNTER = "PerformanceCounter";
            try
            {
                var factory = _sinkFactoryCatalog.GetFactory(PERFORMANCE_COUNTER);
                if (factory != null)
                {
                    IConfiguration perfCounterSection = _config.GetSection("PerformanceCounter");
                    var sink = factory.CreateInstance(PERFORMANCE_COUNTER, CreatePlugInContext(perfCounterSection));
                    sink.Start();
                    _sinks["_" + PERFORMANCE_COUNTER] = sink;
                    _subscriptions.Add(_metrics.Subscribe(sink));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Unable to load performance counter. Error: {ex.ToMinimized()}");
            }
        }

        private void PublishBuilderNumber()
        {
            int buildNumber = ProgramInfo.GetBuildNumber();
            _metrics.PublishCounter(string.Empty, MetricsConstants.CATEGORY_PROGRAM, CounterTypeEnum.CurrentValue, MetricsConstants.KINESISTAP_BUILD_NUMBER, buildNumber, MetricUnit.None);
        }

        private IPlugInContext CreatePlugInContext(IConfiguration config)
        {
            // Previously, all sources, sinks, pipes and plugins all used the same instance of a logger (the LogManager logger).
            // This is confusing when reading the logs because it means that all log entries appear to come from the same class.
            // This new logic will check the config for an "Id" property and if found, will create a new logger with that Id as the name.
            // If there is no "Id" property specified, it will use the default logger (i.e. the LogManager logger instance).
            // This means that every line in the log will contain the Id of the source/sink/pipe/plugin that it originated from.
            var plugInContext = new PluginContext(config, string.IsNullOrWhiteSpace(config["Id"]) ? _logger : _loggerFactory.CreateLogger(config["Id"]), _metrics, _credentialProviders, _parameterStore);
            plugInContext.ContextData[PluginContext.PARSER_FACTORIES] = new ReadOnlyFactoryCatalog<IRecordParser>(_recordParserCatalog); //allow plug-ins access a list of parsers
            return plugInContext;
        }

        private void LoadGenericPlugins()
        {
            var pluginsSection = _config.GetSection("Plugins");
            var pluginSections = pluginsSection.GetChildren();
            int pluginsStarted = 0;
            int pluginsFailedToStart = 0;
            foreach (var pluginSection in pluginSections)
            {
                if (LoadPlugin(pluginSection))
                {
                    pluginsStarted++;
                }
                else
                {
                    pluginsFailedToStart++;
                }
            }
            _metrics.PublishCounters(string.Empty, MetricsConstants.CATEGORY_PROGRAM, CounterTypeEnum.CurrentValue, new Dictionary<string, MetricValue>()
                {
                    { MetricsConstants.PLUGINS_STARTED, new MetricValue(pluginsStarted) },
                    { MetricsConstants.PLUGINS_FAILED_TO_START, new MetricValue(pluginsFailedToStart) }
                });
        }

        private bool LoadPlugin(IConfigurationSection pluginSection)
        {
            string pluginType = _config.GetChildConfig(pluginSection.Path, ConfigConstants.TYPE);
            var factory = _genericPluginFactoryCatalog.GetFactory(pluginType);
            if (factory != null)
            {
                try
                {
                    IGenericPlugin plugin = factory.CreateInstance(pluginType, CreatePlugInContext(pluginSection));
                    plugin.Start();
                    _plugins.Add(plugin);
                    _logger.LogInformation($"Plugin type {pluginType} started.");
                    if (plugin is INetworkStatus networkStatusProvider)
                    {
                        NetworkStatus.RegisterNetworkStatusProvider(networkStatusProvider);
                        _logger.LogInformation($"Registered network status provider {plugin.Id}");
                    }
                    return true;
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Unable to load plugin type {pluginType} exception {ex.ToMinimized()}");
                    return false;
                }
            }
            else
            {
                _logger.LogError("Plugin Type {0} is not recognized.", pluginType);
                return false;
            }
        }

        #endregion
    }
}
