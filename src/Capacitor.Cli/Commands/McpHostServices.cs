using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Core.Telemetry;

namespace Capacitor.Cli.Commands;

/// <summary>What <see cref="McpServerHost"/> needs from the command's container.</summary>
sealed record McpHostServices(
    ConfigRoot Config,
    ProfileContext Profiles,
    TokenStore Tokens,
    ICapacitorHttpClient Http,
    TelemetryStartup Startup,
    TimeProvider Time);
