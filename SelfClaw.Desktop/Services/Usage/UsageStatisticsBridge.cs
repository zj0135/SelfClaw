using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;

namespace SelfClaw.Desktop.Services.Usage;

/// <summary>
/// WebView bridge for the About page usage dashboard. It only parses the wire request and shapes the
/// wire response: every number comes from <see cref="IUsageOverviewReader"/>, which stays unaware of
/// WPF and of the entry assembly.
/// </summary>
internal sealed class UsageStatisticsBridge
{
    private const string OverviewType = "usage/get-overview";
    private const string SevenDays = "7d";
    private const string ThirtyDays = "30d";
    private const int BuildHashLength = 7;

    private readonly IUsageOverviewReader _reader;

    public UsageStatisticsBridge(IUsageOverviewReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        _reader = reader;
    }

    public async Task<object?> TryHandleAsync(
        string type,
        JsonElement payload,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(type, OverviewType, StringComparison.Ordinal))
        {
            return null;
        }

        var requestId = ReadOptionalString(payload, "requestId");
        var overview = await _reader
            .ReadAsync(new UsageOverviewQuery(ReadRange(payload), ReadTimeZone(payload)), cancellationToken)
            .ConfigureAwait(false);

        return new
        {
            type = "usage-overview",
            requestId,
            generatedAt = overview.GeneratedAt.ToUnixTimeMilliseconds(),
            timeZone = overview.TimeZone,
            range = RangeWire(overview.Range),
            app = ReadAppInfo(),
            lifetime = overview.Lifetime,
            rangeData = new
            {
                daily = overview.RangeDaily,
                models = overview.RangeModels,
                tools = overview.RangeTools
            }
        };
    }

    private static UsageRange ReadRange(JsonElement payload)
        => ReadOptionalString(payload, "range") switch
        {
            null or SevenDays => UsageRange.SevenDays,
            ThirtyDays => UsageRange.ThirtyDays,
            var value => throw new ArgumentException($"Unsupported usage range '{value}'.")
        };

    // The zone id arrives from the browser and its spelling differs per platform; a name the host
    // cannot resolve is not worth failing the whole dashboard over, so it falls back to the local zone.
    private static TimeZoneInfo ReadTimeZone(JsonElement payload)
    {
        var id = ReadOptionalString(payload, "timeZone");
        if (id is null)
        {
            return TimeZoneInfo.Local;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return TimeZoneInfo.Local;
        }
    }

    private static UsageAppInfo ReadAppInfo()
    {
        var assembly = Assembly.GetEntryAssembly();
        var version = assembly?.GetName().Version;
        var informationalVersion = assembly?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var patch = version is null ? 0 : Math.Max(0, version.Build);
        return new UsageAppInfo(
            version is null ? "0.0.0" : $"{version.Major}.{version.Minor}.{patch}",
            ReadBuild(informationalVersion),
            RuntimeInformation.FrameworkDescription,
            RuntimeInformation.OSDescription);
    }

    private static string? ReadBuild(string? informationalVersion)
    {
        if (informationalVersion is not { } value)
        {
            return null;
        }

        var separator = value.IndexOf('+');
        if (separator < 0 || separator == value.Length - 1)
        {
            return null;
        }

        var hash = value[(separator + 1)..];
        return hash.Length > BuildHashLength ? hash[..BuildHashLength] : hash;
    }

    private static string RangeWire(UsageRange range)
        => range == UsageRange.ThirtyDays ? ThirtyDays : SevenDays;

    private static string? ReadOptionalString(JsonElement payload, string propertyName)
    {
        if (!payload.TryGetProperty(propertyName, out var element) || element.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var value = element.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
