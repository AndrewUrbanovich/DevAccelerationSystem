using UnityEngine;

namespace TheBestLogger
{
    internal static class UnityLogExtension
    {
        public static LogLevel ConvertToTheBestLoggerLogLevel(this LogType unityLogType)
        {
            return unityLogType switch
            {
                LogType.Exception => LogLevel.Exception,
                LogType.Error => LogLevel.Error,
                LogType.Assert => LogLevel.Error,
                LogType.Warning => LogLevel.Warning,
                LogType.Log => LogLevel.Debug,
                _ => LogLevel.Info
            };
        }

        public static LogType ConvertToUnityLogType(this LogLevel logLevel)
        {
            return logLevel switch
            {
                LogLevel.Exception => LogType.Exception,
                LogLevel.Error => LogType.Error ,
                LogLevel.Warning => LogType.Warning,
                LogLevel.Debug => LogType.Log,
                LogLevel.Info => LogType.Log,
                _ => LogType.Log
            };
        }
    }
}