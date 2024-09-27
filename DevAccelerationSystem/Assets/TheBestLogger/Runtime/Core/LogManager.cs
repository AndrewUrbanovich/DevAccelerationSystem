using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cysharp.Text;
using UnityEngine;

namespace TheBestLogger
{
    //references https://learn.microsoft.com/en-us/dotnet/core/extensions/logging
    //https://docs.unity.com/ugs/en-us/manual/game-server-hosting/manual/beta/debugging-logging
    //https://opentelemetry.io/docs/what-is-opentelemetry/

    
    public class LogManager
    {
        private static ConcurrentDictionary<string, ILogger> _loggers;
        private static IReadOnlyList<ILogTarget> _decoratedLogTargets = Array.Empty<ILogTarget>();
        private static IReadOnlyList<LogTarget> _originalLogTargets = Array.Empty<LogTarget>();
        private static IReadOnlyList<ILogSource> _logSources = Array.Empty<ILogSource>();
        private static LogManagerConfiguration _configuration;
        private static IUtilitySupplier _utilitySupplier;
        private static uint _minUpdatesPeriodMs;
        private static DateTime _timeStampPrevious;
        private static string _timeStampPreviousString;

        private static bool _isRunningUpdates = false;
        private static bool _wasDisposed = false;
        private static List<IScheduledUpdate> _targetUpdates;
        private static CancellationToken _disposingToken;
        private static bool _isInitialized = false;
        private static readonly ILogger FallbackLogger = new FallbackLogger();

        public static ILogger CreateLogger(string categoryName)
        {
            Diagnostics.Write("starting for category: " + categoryName);

            if (!_isInitialized)
            {
                FallbackLogger.LogWarning(
                    "LogManager is not initialized! Fallback logger based on Debug.Log* for the requested category : " + categoryName + " will be returned");
                return FallbackLogger;
            }

            if (!_loggers.TryGetValue(categoryName, out var logger))
            {
                Diagnostics.Write(" will create a new logger for category: " + categoryName);

                logger = new CoreLogger(categoryName, _decoratedLogTargets, _utilitySupplier);
                _loggers.TryAdd(categoryName, logger);
            }
            else
            {
                Diagnostics.Write(" will get a cached logger for category: " + categoryName);
            }

            return logger;
        }

#if UNITY_EDITOR
        /// <summary>
        /// to avoid changes on scriptable objects when modified configs at runtime
        /// </summary>
        /// <param name="obj"></param>
        /// <typeparam name="T"></typeparam>
        /// <returns></returns>
        private static T DeepCopyInUnityEditor<T>(T obj)
        {
            var json = JsonUtility.ToJson(obj);
            return (T)(object)JsonUtility.FromJson(json, obj.GetType());
        }
#endif

        private static Dictionary<string, LogTargetConfiguration> ConvertToDictionaryWithKeyNameAndValueConfigSpecificData(LogTargetConfigurationSO[] logTargetConfigurationsSo)
        {
            var logTargetConfigurationsData = new Dictionary<string, LogTargetConfiguration>();

            foreach (var logTargetConfigSo in logTargetConfigurationsSo)
            {
                if (logTargetConfigSo != null)
                {
#if !UNITY_EDITOR
                    logTargetConfigurationsData[logTargetConfigSo.name] = logTargetConfigSo.Configuration;
#else
                    var config = logTargetConfigSo.Configuration;
                    var logTargetConfigurationNew = DeepCopyInUnityEditor(config);
                    logTargetConfigurationsData[logTargetConfigSo.name] = logTargetConfigurationNew;
#endif
                }
                else
                {
                    Diagnostics.Write("logTargetConfigSO is null", LogLevel.Warning);
                }
            }

            return logTargetConfigurationsData;
        }

        public static void UpdateLogTargetsConfigurations(Dictionary<string, LogTargetConfiguration> logTargetConfigurations)
        {
            Diagnostics.Write("begin public");
            TryApplyConfigurations(logTargetConfigurations, _decoratedLogTargets);
            Diagnostics.Write("end public");
        }

        public static Dictionary<string, LogTargetConfiguration> GetCurrentLogTargetConfigurations()
        {
            var logTargetConfigurations = new Dictionary<string, LogTargetConfiguration>();
            foreach (var logTarget in _decoratedLogTargets)
            {
                //logTarget.ToString() is GetType().Name
                var logTargetConfigurationId = ZString.Concat(logTarget.ToString(), "Configuration");
                logTargetConfigurations[logTargetConfigurationId] = logTarget.Configuration;
            }

            return logTargetConfigurations;
        }
        /// <summary>
        /// Manager will apply automaticaly configs from provided lists with logTarget.GetType().Name+Configuration == logTargetConfig.name
        /// </summary>
        /// <param name="logTargetConfigurations"></param>
        /// <param name="logTargets"></param>
        private static void TryApplyConfigurations(Dictionary<string, LogTargetConfiguration> logTargetConfigurations, 
                                                   IReadOnlyList<ILogTarget> logTargets)
        {
            Diagnostics.Write("begin");
            if (logTargetConfigurations == null || logTargetConfigurations.Count < 1)
            {
                Diagnostics.Write("logTargetConfigurations is null or empty", LogLevel.Warning);
                return;
            }

            if (logTargets == null || logTargets.Count < 1)
            {
                Diagnostics.Write("logTargets is null or empty", LogLevel.Warning);
                return;
            }

            var udid = SystemInfo.deviceUniqueIdentifier; // main thread needs

            foreach (var logTarget in logTargets)
            {
                var logTargetId = ZString.Concat(logTarget.ToString(), "Configuration");
                Diagnostics.Write("logTarget: "+ logTarget.GetType()+" with "+logTargetId, LogLevel.Debug);

                logTargetConfigurations.TryGetValue(logTargetId, out var logTargetConfiguration);

                if (logTargetConfiguration != null)
                {
                    Diagnostics.Write(
                        "For LogTarget:" + logTarget.GetType() + " was applied logtargetconfiguration:" + logTargetConfiguration.GetType());

                    var debugMode = logTargetConfiguration.DebugMode;
                    var debugModeEnabled = false;
                    if (debugMode.Enabled && debugMode.IDs.Length > 0)
                    {
                        foreach (var id in debugMode.IDs)
                        {
                            if (id == udid)
                            {
                                Diagnostics.Write("For LogTarget:" + logTarget.GetType() + " was enabled debugMode");
                                debugModeEnabled = true;
                            }
                        }
                    }

                    logTarget.ApplyConfiguration(logTargetConfiguration);
                    logTarget.SetDebugMode(debugModeEnabled);
                }
                else
                {
                    Diagnostics.Write($"Can not match {logTargetId} with configurations files. LogTargetConfigurationDefault was applied!", LogLevel.Warning);
                    logTarget.ApplyConfiguration(new LogTargetConfigurationDefault(logTarget.GetType().ToString()));
                }
            }

            Diagnostics.Write("end");
        }

        private static IReadOnlyList<ILogTarget> TryDecorateLogTargets(
            IReadOnlyList<LogTarget> originalLogTargets,
            DateTime currentTimeUtc)
        {
            Diagnostics.Write("begin");

            var list = new List<ILogTarget>(originalLogTargets.Count);
            foreach (var originalLogTarget in originalLogTargets)
            {
                if (originalLogTarget.Configuration == null)
                {
                    Diagnostics.Write("originalLogTarget.Configuration == null for "+originalLogTarget.GetType(), LogLevel.Error);
                    continue;
                }

                if (originalLogTarget.Configuration.BatchLogs.Enabled)
                {
                    var decoratedLogTarget = new LogTargetBatchLogsDecoration(
                                                 originalLogTarget.Configuration.BatchLogs, originalLogTarget, currentTimeUtc) as ILogTarget;

                    Diagnostics.Write(
                        originalLogTarget.GetType() + " was decorated by " +
                        decoratedLogTarget.GetType());

                    list.Add(decoratedLogTarget);
                    continue;
                }

                list.Add(originalLogTarget);
            }

            Diagnostics.Write("end");
            return list.AsReadOnly();
        }

        private static List<IScheduledUpdate> TrySubscribeForUpdates(
            IReadOnlyList<ILogTarget> logTargets)
        {
            var list = new List<IScheduledUpdate>(logTargets.Count);
            foreach (var logTarget in logTargets)
            {
                if (logTarget is IScheduledUpdate update)
                {
                    Diagnostics.Write(logTarget.GetType() + " is scheduled for periodical updates with PeriodMs:" + update.PeriodMs);
                    list.Add(update);
                }
            }

            return list;
        }

        private static async Task RunUpdates(IReadOnlyList<IScheduledUpdate> targetUpdates,
                                             uint minUpdatesPeriodMs,
                                             DateTime currentTimeUtc,
                                             CancellationToken cancellationToken)
        {
            Diagnostics.Write(" starting");

            _isRunningUpdates = true;
            var previousTimeStamp = currentTimeUtc;
            var minUpdate = (int) Math.Min(targetUpdates.Min(k => k.PeriodMs), minUpdatesPeriodMs);

            while (_isRunningUpdates)
            {
                await Task.Delay(minUpdate, cancellationToken).ConfigureAwait(true);

                if (cancellationToken.IsCancellationRequested)
                {
                    Diagnostics.Write("isCancellationRequested");
                    return;
                }

                var currentTimeStamp = _utilitySupplier.GetTimeStamp();
                var deltaMs = (uint) (currentTimeStamp.currentTimeUtc - previousTimeStamp).TotalMilliseconds;
                previousTimeStamp = currentTimeStamp.currentTimeUtc;

                //_defaultLogger?.LogDebug("Update: "+deltaMs+", "+currentTimeStamp.timeStampCached);
                foreach (var target in targetUpdates)
                {
                    target.Update(currentTimeStamp.currentTimeUtc, deltaMs);
                }
            }

            Diagnostics.Write(" finished");
        }

        /// <summary>
        ///  Should be called from Unity main thread
        /// </summary>
        /// <param name="logTargets">List of targets to print logs. Usually it makes sense to assign log target configs into logmanagerconfiguration</param>
        /// <param name="disposingToken">As example dispose logger when application exit</param>
        /// <param name="resourceSubFolderThatContainsConfigs">Inside Unity some Resources folder. Example "Logger/Dev/"</param>
        public static void Initialize(IReadOnlyList<LogTarget> logTargets,
                                      CancellationToken disposingToken,
                                      string resourceSubFolderThatContainsConfigs = "")
        {
            if (_isInitialized)
            {
                Diagnostics.Write("LogManager is already initialized! Wrong behaviour usage detected", LogLevel.Warning);
                return;
            }

            try
            {
                Diagnostics.Write("LogManager is initializing!");

                _disposingToken = disposingToken;
                _disposingToken.Register(Dispose);
                _originalLogTargets = logTargets;
                _loggers = new ConcurrentDictionary<string, ILogger>(4, 25);

                var config = Resources.Load<LogManagerConfiguration>(resourceSubFolderThatContainsConfigs + nameof(LogManagerConfiguration));

                _configuration = config;
                _utilitySupplier = new UtilitySupplier(_configuration.MinTimestampPeriodMs);

                _minUpdatesPeriodMs = _configuration.MinUpdatesPeriodMs;
                var dict = ConvertToDictionaryWithKeyNameAndValueConfigSpecificData(config.LogTargetConfigs);
                TryApplyConfigurations(dict, logTargets);

                var currentTimeUtc = _utilitySupplier.GetTimeStamp().currentTimeUtc;
                _decoratedLogTargets = TryDecorateLogTargets(logTargets, currentTimeUtc);

                _targetUpdates = TrySubscribeForUpdates(_decoratedLogTargets);

                Debug.unityLogger.filterLogType = config.DebugUnityLoggerFilterLogType;
                //stacktrace apply configs

                _isInitialized = true;
                var logger = CreateLogger(config.DefaultUnityLogsCategoryName);

                var logSources = new List<ILogSource>(4)
                {
                };

                _logSources = logSources.AsReadOnly();

                if (config.IsActiveUnityDebugLogSource)
                {
                    logSources.Add(new UnityDebugLogSource(logger as ILogConsumer));
                }

                if (config.IsActiveSystemDiagnosticsDebugLogSource)
                {
                    logSources.Add(new SystemDiagnosticsDebugLogSource(logger as ILogConsumer));
                }

                if (config.IsActiveUnityApplicationLogSource)
                {
                    logSources.Add(new UnityApplicationLogSource(logger as ILogConsumer));
                }

                if (config.IsActiveUnobservedTaskExceptionLogSource)
                {
                    logSources.Add(new UnobservedTaskExceptionLogSource(logger as ILogConsumer));
                }

                if (_targetUpdates.Count > 0)
                {
                    RunUpdates(_targetUpdates, _minUpdatesPeriodMs, currentTimeUtc, disposingToken).FireAndForget();
                }

                Diagnostics.Write("LogManager has initialized!");
            }
            catch (Exception ex)
            {
                FallbackLogger.LogError($"During LogManager initialization happened exception {ex.Message}:\n{ex.StackTrace}");
                Dispose();
            }
        }

        public static void Dispose()
        {
            if (_wasDisposed)
            {
                return;
            }

            _wasDisposed = true;
            Diagnostics.Write("is disposing!");

            _isInitialized = false;

            _isRunningUpdates = false;
            _configuration = null;
            _utilitySupplier = null;
            if (_targetUpdates != null)
            {
                _targetUpdates.Clear();
            }

            if (_logSources != null)
            {
                foreach (var l in _logSources)
                {
                    l.Dispose();
                }

                _logSources = null;
            }

            if (_loggers != null)
            {
                foreach (var logger in _loggers)
                {
                    logger.Value.Dispose();
                }

                _loggers.Clear();
                _loggers = null;
            }

            if (_originalLogTargets != null)
            {
                foreach (var logTarget in _originalLogTargets)
                {
                    logTarget.Dispose();
                }

                _originalLogTargets = null;
            }
            
            if (_decoratedLogTargets != null)
            {
                foreach (var logTarget in _decoratedLogTargets)
                {
                    logTarget.Dispose();
                }

                _decoratedLogTargets = null;
            }

            Diagnostics.Write("has disposed!");
            Diagnostics.Cancel();
        }
    }
}
