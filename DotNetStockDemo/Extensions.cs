using Hazelcast;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Hazelcast.Models;

namespace DotNetStockDemo
{
    internal static class Extensions
    {
        /// <summary>
        /// Sets the logger factory
        /// </summary>
        /// <param name="builder">The options builder.</param>
        /// <param name="hazelcastLogLevel">The Hazelcast log level.</param>
        /// <returns>The options builder.</returns>
        public static HazelcastOptionsBuilder WithConsoleLogger(this HazelcastOptionsBuilder builder, LogLevel hazelcastLogLevel = LogLevel.Information)
        {
            return builder
                .WithDefault("Logging:LogLevel:Default", "None")
                .WithDefault("Logging:LogLevel:System", "Information")
                .WithDefault("Logging:LogLevel:Microsoft", "Information")
                .WithDefault("Logging:LogLevel:Hazelcast", hazelcastLogLevel.ToString())
                .With((configuration, options) =>
                {
                    // configure logging factory and add the console provider
                    options.LoggerFactory.Creator = () => LoggerFactory.Create(loggingBuilder =>
                        loggingBuilder
                            .AddConfiguration(configuration.GetSection("logging"))
                            // https://docs.microsoft.com/en-us/dotnet/core/extensions/console-log-formatter
                            .AddSimpleConsole(o =>
                            {
                                o.SingleLine = true;
                                o.TimestampFormat = "hh:mm:ss.fff ";
                            }));
                });
        }

        public static string ToString(this HBigDecimal value, int decimalsCount)
        {
            var text = value.ToString();
            var separator = '.'; //CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator;
            var pos = text.IndexOf('.');
            return pos < 0 ? text : text.Substring(0, Math.Min(text.Length, pos + decimalsCount + 1));
        }
    }
}
