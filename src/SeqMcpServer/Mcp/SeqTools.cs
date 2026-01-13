using ModelContextProtocol.Server;
using Seq.Api.Model.Events;
using Seq.Api.Model.Signals;
using SeqMcpServer.Services;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace SeqMcpServer.Mcp;

/// <summary>
/// MCP tools for interacting with Seq structured logging server.
/// </summary>
[McpServerToolType]
public static class SeqTools
{
    /// <summary>
    /// Application name aliases for common services.
    /// Maps short names to full AppName values used in Seq logs.
    /// </summary>
    private static readonly Dictionary<string, string> AppAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        // Standard Services
        ["pims"] = "pims-services",
        ["pims-services"] = "pims-services",
        ["charta"] = "charta-services",
        ["charta-services"] = "charta-services",
        ["data"] = "data-services",
        ["data-services"] = "data-services",
        ["scribing"] = "ai-scribing-services",
        ["ai-scribing"] = "ai-scribing-services",
        ["ai-scribing-services"] = "ai-scribing-services",
        ["zus"] = "zus-services",
        ["zus-services"] = "zus-services",
        ["equip"] = "equip-services",
        ["equip-services"] = "equip-services",
        ["survey"] = "survey-services",
        ["survey-services"] = "survey-services",
        ["training"] = "training-services",
        ["training-services"] = "training-services",
        ["meetingbaas"] = "meetingbaas",
        ["bot"] = "meetingbaas",
        ["keystone"] = "keystone-platform-backend",
        ["keystone-platform-backend"] = "keystone-platform-backend",

        // Azure Function Apps
        ["fx-pims"] = "fx-app-pims-services",
        ["pims-fx"] = "fx-app-pims-services",
        ["fx-app-pims-services"] = "fx-app-pims-services",
        ["fx-charta"] = "fx-app-charta-services",
        ["charta-fx"] = "fx-app-charta-services",
        ["fx-app-charta-services"] = "fx-app-charta-services",
        ["fx-survey"] = "fx-app-survey-services",
        ["survey-fx"] = "fx-app-survey-services",
        ["fx-app-survey-services"] = "fx-app-survey-services",
        ["fx-data"] = "fx-app-data-services",
        ["data-fx"] = "fx-app-data-services",
        ["fx-app-data-services"] = "fx-app-data-services",
    };

    /// <summary>
    /// Resolves an app alias to its full AppName value.
    /// If the alias is not found, returns the original value unchanged.
    /// </summary>
    private static string ResolveAppName(string alias) =>
        AppAliases.TryGetValue(alias, out var resolved) ? resolved : alias;

    /// <summary>
    /// Escapes single quotes in a value for use in Seq filter expressions.
    /// </summary>
    private static string EscapeFilterValue(string value) =>
        value.Replace("'", "''");

    /// <summary>
    /// Search historical events in Seq with the specified filter.
    /// </summary>
    /// <param name="fac">Factory for creating Seq connections</param>
    /// <param name="filter">Seq filter expression (e.g., "@Level = 'Error'")</param>
    /// <param name="count">Maximum number of events to return (1-1000)</param>
    /// <param name="workspace">Optional workspace identifier for multi-tenant scenarios</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>List of matching events</returns>
    [McpServerTool, Description("Search Seq events with filters, returning up to the specified count")]
    public static async Task<List<EventEntity>> SeqSearch(
        SeqConnectionFactory fac,
        [Required] string filter,
        [Range(1, 1000)] int count = 100,
        string? workspace = null,
        CancellationToken ct = default)
    {
        try
        {
            var conn = fac.Create(workspace);
            var events = new List<EventEntity>();
            await foreach (var evt in conn.Events.EnumerateAsync(
                filter: filter,
                count: count,
                render: true,
                cancellationToken: ct).WithCancellation(ct))
            {
                events.Add(evt);
            }
            return events;
        }
        catch (OperationCanceledException)
        {
            // Return empty list on cancellation
            return [];
        }
        catch (Exception)
        {
            // Re-throw to let MCP handle the error
            throw;
        }
    }

    /// <summary>
    /// Wait for and capture live events from Seq's event stream.
    /// </summary>
    /// <remarks>
    /// This method connects to Seq's live event stream and captures events as they arrive,
    /// up to the specified count or until the 5-second timeout expires. Due to MCP protocol
    /// limitations, events are returned as a complete snapshot rather than streamed incrementally.
    /// The method may return an empty list if no events matching the filter arrive within the timeout period.
    /// </remarks>
    /// <param name="fac">Factory for creating Seq connections</param>
    /// <param name="filter">Optional Seq filter expression to apply to the stream</param>
    /// <param name="count">Maximum number of events to capture (1-100)</param>
    /// <param name="workspace">Optional workspace identifier for multi-tenant scenarios</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Snapshot of events captured during the wait period (may be empty)</returns>
    [McpServerTool, Description("Wait for and capture live events from Seq (times out after 5 seconds, returns captured events as a snapshot)")]
    public static async Task<List<EventEntity>> SeqWaitForEvents(
        SeqConnectionFactory fac,
        string? filter = null,
        [Range(1, 100)] int count = 10,
        string? workspace = null,
        CancellationToken ct = default)
    {
        try
        {
            var conn = fac.Create(workspace);
            var events = new List<EventEntity>();
            
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            
            await foreach (var evt in conn.Events.StreamAsync(
                unsavedSignal: null,
                signal: null,
                filter: filter ?? string.Empty,
                cancellationToken: ct).WithCancellation(combinedCts.Token))
            {
                events.Add(evt);
                if (events.Count >= count) break;
            }
            
            return events;
        }
        catch (OperationCanceledException)
        {
            // Return what we have on timeout/cancellation
            return [];
        }
        catch (Exception)
        {
            // Re-throw to let MCP handle the error
            throw;
        }
    }

    /// <summary>
    /// List available signals (saved searches) in Seq.
    /// </summary>
    /// <remarks>
    /// Signals in Seq are saved searches that can be used to quickly access commonly used filters.
    /// This method returns only shared signals (read-only access).
    /// </remarks>
    /// <param name="fac">Factory for creating Seq connections</param>
    /// <param name="workspace">Optional workspace identifier for multi-tenant scenarios</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>List of available signals</returns>
    [McpServerTool, Description("List available signals in Seq (read-only access to shared signals)")]
    public static async Task<List<SignalEntity>> SignalList(
        SeqConnectionFactory fac,
        string? workspace = null,
        CancellationToken ct = default)
    {
        try
        {
            var conn = fac.Create(workspace);
            return await conn.Signals.ListAsync(shared: true, cancellationToken: ct);
        }
        catch (OperationCanceledException)
        {
            // Return empty list on cancellation
            return [];
        }
        catch (Exception)
        {
            // Re-throw to let MCP handle the error
            throw;
        }
    }

    /// <summary>
    /// Search logs by application name with alias support and time window.
    /// </summary>
    /// <remarks>
    /// Supports common aliases like "pims" for "pims-services", "scribing" for "ai-scribing-services",
    /// and "fx-pims" for "fx-app-pims-services". See AppAliases dictionary for full list.
    /// </remarks>
    /// <param name="fac">Factory for creating Seq connections</param>
    /// <param name="app">App name or alias (e.g., 'pims', 'fx-charta', 'scribing')</param>
    /// <param name="level">Log level filter: 'error', 'warning', 'info', or 'all'</param>
    /// <param name="hours">Hours to search back (default: 24)</param>
    /// <param name="functionName">Optional filter by Azure Function name</param>
    /// <param name="count">Maximum number of results (1-500)</param>
    /// <param name="workspace">Optional workspace identifier for multi-tenant scenarios</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>List of matching events</returns>
    [McpServerTool, Description("Search logs by application name with alias support and time window")]
    public static async Task<List<EventEntity>> AppSearch(
        SeqConnectionFactory fac,
        [Description("App name or alias (e.g., 'pims', 'fx-charta', 'scribing')")] string app,
        [Description("Log level: 'error', 'warning', 'info', or 'all'")] string level = "all",
        [Description("Hours to search back (default: 24)")] [Range(1, 8760)] int hours = 24,
        [Description("Filter by Azure Function name")] string? functionName = null,
        [Description("Max results (1-500)")] [Range(1, 500)] int count = 50,
        string? workspace = null,
        CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(app))
                throw new ArgumentException("App name cannot be empty", nameof(app));

            var conn = fac.Create(workspace);
            var appName = EscapeFilterValue(ResolveAppName(app));
            var filterParts = new List<string> { $"AppName = '{appName}'" };

            if (!string.Equals(level, "all", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(level))
            {
                // Normalize level: first char uppercase, rest lowercase (e.g., "error" -> "Error")
                var normalizedLevel = level.Length == 1
                    ? char.ToUpper(level[0]).ToString()
                    : char.ToUpper(level[0]) + level[1..].ToLower();
                filterParts.Add($"@Level = '{normalizedLevel}'");
            }

            if (!string.IsNullOrEmpty(functionName))
                filterParts.Add($"FunctionName = '{EscapeFilterValue(functionName)}'");

            filterParts.Add($"@Timestamp > Now() - {hours}h");

            var filter = string.Join(" and ", filterParts);

            var events = new List<EventEntity>();
            await foreach (var evt in conn.Events.EnumerateAsync(
                filter: filter,
                count: count,
                render: true,
                cancellationToken: ct).WithCancellation(ct))
            {
                events.Add(evt);
            }
            return events;
        }
        catch (OperationCanceledException)
        {
            return [];
        }
        catch (Exception)
        {
            throw;
        }
    }

    /// <summary>
    /// Trace all logs for a specific Azure Function invocation.
    /// </summary>
    /// <remarks>
    /// Returns all log events associated with a specific InvocationId.
    /// Useful for debugging Azure Function executions end-to-end.
    /// </remarks>
    /// <param name="fac">Factory for creating Seq connections</param>
    /// <param name="invocationId">The InvocationId UUID from Azure Functions</param>
    /// <param name="workspace">Optional workspace identifier for multi-tenant scenarios</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>List of all events for the invocation</returns>
    [McpServerTool, Description("Trace all logs for a specific Azure Function invocation")]
    public static async Task<List<EventEntity>> InvocationTrace(
        SeqConnectionFactory fac,
        [Description("The InvocationId UUID from Azure Functions")] string invocationId,
        string? workspace = null,
        CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(invocationId))
                throw new ArgumentException("Invocation ID cannot be empty", nameof(invocationId));

            var conn = fac.Create(workspace);
            var filter = $"InvocationId = '{EscapeFilterValue(invocationId)}'";

            var events = new List<EventEntity>();
            await foreach (var evt in conn.Events.EnumerateAsync(
                filter: filter,
                count: 1000, // Effectively unlimited - typical invocations have <100 logs
                render: true,
                cancellationToken: ct).WithCancellation(ct))
            {
                events.Add(evt);
            }
            return events;
        }
        catch (OperationCanceledException)
        {
            return [];
        }
        catch (Exception)
        {
            throw;
        }
    }

    /// <summary>
    /// Find slow operations exceeding a duration threshold.
    /// </summary>
    /// <remarks>
    /// Searches for log events where DurationMs exceeds the specified threshold.
    /// Results are returned by time (newest first). Seq doesn't support ORDER BY.
    /// </remarks>
    /// <param name="fac">Factory for creating Seq connections</param>
    /// <param name="thresholdMs">Minimum duration in milliseconds (default: 5000)</param>
    /// <param name="app">Optional filter by app name or alias</param>
    /// <param name="functionName">Optional filter by function name</param>
    /// <param name="hours">Hours to search back (default: 24)</param>
    /// <param name="count">Maximum number of results (1-100)</param>
    /// <param name="workspace">Optional workspace identifier for multi-tenant scenarios</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>List of slow execution events</returns>
    [McpServerTool, Description("Find slow operations exceeding duration threshold")]
    public static async Task<List<EventEntity>> SlowExecutions(
        SeqConnectionFactory fac,
        [Description("Minimum duration in milliseconds (default: 5000)")] [Range(1, int.MaxValue)] int thresholdMs = 5000,
        [Description("Filter by app name or alias")] string? app = null,
        [Description("Filter by function name")] string? functionName = null,
        [Description("Hours to search back (default: 24)")] [Range(1, 8760)] int hours = 24,
        [Description("Max results (1-100)")] [Range(1, 100)] int count = 20,
        string? workspace = null,
        CancellationToken ct = default)
    {
        try
        {
            var conn = fac.Create(workspace);
            var filterParts = new List<string> { $"DurationMs > {thresholdMs}" };

            if (!string.IsNullOrEmpty(app))
                filterParts.Add($"AppName = '{EscapeFilterValue(ResolveAppName(app))}'");

            if (!string.IsNullOrEmpty(functionName))
                filterParts.Add($"FunctionName = '{EscapeFilterValue(functionName)}'");

            filterParts.Add($"@Timestamp > Now() - {hours}h");

            var filter = string.Join(" and ", filterParts);

            var events = new List<EventEntity>();
            await foreach (var evt in conn.Events.EnumerateAsync(
                filter: filter,
                count: count,
                render: true,
                cancellationToken: ct).WithCancellation(ct))
            {
                events.Add(evt);
            }
            return events;
        }
        catch (OperationCanceledException)
        {
            return [];
        }
        catch (Exception)
        {
            throw;
        }
    }

    /// <summary>
    /// Track an entity across all services.
    /// </summary>
    /// <remarks>
    /// Searches for all log events mentioning a specific entity type and ID.
    /// Useful for debugging cross-service workflows involving specific entities.
    /// </remarks>
    /// <param name="fac">Factory for creating Seq connections</param>
    /// <param name="entityType">Entity type (e.g., 'Job', 'Patient', 'Payment')</param>
    /// <param name="entityId">Entity ID value</param>
    /// <param name="hours">Hours to search back (default: 24)</param>
    /// <param name="count">Maximum number of results (1-200)</param>
    /// <param name="workspace">Optional workspace identifier for multi-tenant scenarios</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>List of events mentioning the entity</returns>
    [McpServerTool, Description("Track an entity across all services")]
    public static async Task<List<EventEntity>> EntityTrace(
        SeqConnectionFactory fac,
        [Description("Entity type (e.g., 'Job', 'Patient', 'Payment')")] string entityType,
        [Description("Entity ID value")] string entityId,
        [Description("Hours to search back (default: 24)")] [Range(1, 8760)] int hours = 24,
        [Description("Max results (1-200)")] [Range(1, 200)] int count = 100,
        string? workspace = null,
        CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(entityType))
                throw new ArgumentException("Entity type cannot be empty", nameof(entityType));
            if (string.IsNullOrWhiteSpace(entityId))
                throw new ArgumentException("Entity ID cannot be empty", nameof(entityId));

            var conn = fac.Create(workspace);
            var filter = $"EntityType = '{EscapeFilterValue(entityType)}' and EntityId = '{EscapeFilterValue(entityId)}' and @Timestamp > Now() - {hours}h";

            var events = new List<EventEntity>();
            await foreach (var evt in conn.Events.EnumerateAsync(
                filter: filter,
                count: count,
                render: true,
                cancellationToken: ct).WithCancellation(ct))
            {
                events.Add(evt);
            }
            return events;
        }
        catch (OperationCanceledException)
        {
            return [];
        }
        catch (Exception)
        {
            throw;
        }
    }

    /// <summary>
    /// Get error events for analysis.
    /// </summary>
    /// <remarks>
    /// Returns error-level log events, optionally filtered by app.
    /// The AI agent can analyze and group the returned events.
    /// </remarks>
    /// <param name="fac">Factory for creating Seq connections</param>
    /// <param name="hours">Hours to search back (default: 24)</param>
    /// <param name="app">Optional filter to specific app name or alias</param>
    /// <param name="count">Maximum number of results (1-500)</param>
    /// <param name="workspace">Optional workspace identifier for multi-tenant scenarios</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>List of error events</returns>
    [McpServerTool, Description("Get error events for analysis (AI will summarize and group)")]
    public static async Task<List<EventEntity>> ErrorDashboard(
        SeqConnectionFactory fac,
        [Description("Hours to search back (default: 24)")] [Range(1, 8760)] int hours = 24,
        [Description("Filter to specific app (optional)")] string? app = null,
        [Description("Max results (1-500)")] [Range(1, 500)] int count = 200,
        string? workspace = null,
        CancellationToken ct = default)
    {
        try
        {
            var conn = fac.Create(workspace);
            var filterParts = new List<string> { "@Level = 'Error'" };

            if (!string.IsNullOrEmpty(app))
                filterParts.Add($"AppName = '{EscapeFilterValue(ResolveAppName(app))}'");

            filterParts.Add($"@Timestamp > Now() - {hours}h");

            var filter = string.Join(" and ", filterParts);

            var events = new List<EventEntity>();
            await foreach (var evt in conn.Events.EnumerateAsync(
                filter: filter,
                count: count,
                render: true,
                cancellationToken: ct).WithCancellation(ct))
            {
                events.Add(evt);
            }
            return events;
        }
        catch (OperationCanceledException)
        {
            return [];
        }
        catch (Exception)
        {
            throw;
        }
    }
}
