# Seq MCP Server - Custom Fork

This is a customized fork of [willibrandon/seq-mcp-server](https://github.com/willibrandon/seq-mcp-server) tailored for Eventus Whole Health development patterns.

## Project Overview

**Purpose**: MCP server enabling AI agents to query Seq structured logs directly, eliminating trial-and-error when searching production logs.

**Stack**: .NET 9, Seq.Api, ModelContextProtocol SDK (stdio transport)

**Current Tools** (from upstream):
- `SeqSearch` - Search events with Seq filter expressions
- `SeqWaitForEvents` - Stream live events (5-second timeout)
- `SignalList` - List saved signals

## Seq Server Connection

| Setting | Value |
|---------|-------|
| Server URL | `http://apps-seq-instance.eastus2.azurecontainer.io` |
| Server URL (with port) | `http://apps-seq-instance.eastus2.azurecontainer.io:5341` |
| API Key | `imx440QdCZzNPA2ccRRP` |

## Known Applications

### Standard Services
| AppName | Description |
|---------|-------------|
| `ai-scribing-services` | AI scribing/transcription services |
| `charta-services` | Charta application services |
| `data-services` | Data/SQL executor services |
| `equip-services` | Equipment tracking services |
| `keystone-platform-backend` | Keystone platform backend |
| `pims-services` | PIMS application services |
| `survey-services` | Survey services |
| `training-services` | Training services |
| `zus-services` | ZUS integration services |
| `meetingbaas` | Meeting bot/audio services |

### Azure Function Apps (have FunctionName/InvocationId)
| AppName | Description |
|---------|-------------|
| `fx-app-charta-services` | Charta Azure Functions |
| `fx-app-pims-services` | PIMS Azure Functions |
| `fx-app-survey-services` | Survey Azure Functions |
| `fx-app-data-services` | Data Services Azure Functions |

## Standard Log Properties

### Global Properties (all logs)
```
AppName         - Application identifier (e.g., "pims-services")
AppVersion      - Version string (e.g., "3.1.0")
Environment     - Deployment env ("production", "development")
Region          - Azure region ("eastus2")
LoggerName      - Module/class path (e.g., "functions.shared.sql_client")
ThreadId        - Thread identifier
```

### Azure Function Properties (fx-app-* logs)
```
FunctionName        - Azure Function name (e.g., "async_batch_manager_trigger")
InvocationId        - Unique execution ID (UUID) - PRIMARY CORRELATION KEY
TriggerType         - Function trigger type ("timer", "http", "blob", "queue")
ExecutionTimestamp  - Function start time (ISO format)
IsPastDue           - Timer trigger late flag (true/false)
```

### Error/Performance Properties
```
DurationMs    - Operation duration in milliseconds
EntityId      - Related entity ID (e.g., "PAY-789", "JOB-123")
EntityType    - Entity type (e.g., "Payment", "Job", "Patient")
ErrorType     - Exception class name (e.g., "TimeoutException")
RecordCount   - Number of records processed
```

## Common Seq Filter Patterns

```sql
-- By application
AppName = 'pims-services'
AppName in ['pims-services', 'charta-services']

-- By log level
@Level = 'Error'
@Level in ['Error', 'Warning']

-- By function/module
FunctionName = 'async_batch_manager_trigger'
LoggerName like '%sql_executor%'

-- By invocation (trace single execution)
InvocationId = '07a578ad-0e67-4781-873e-04a63f7c80b5'

-- By entity
EntityType = 'Job' and EntityId = 'JOB-123'

-- By performance
DurationMs > 5000

-- Combined
AppName = 'pims-services' and @Level = 'Error'
FunctionName is not null and DurationMs > 10000
```

## Logging Implementation Patterns

The codebase uses structured logging with helper functions:

```python
# Standard imports
from functions.shared.seq_logging import (
    log_function_start,
    log_function_complete,
    log_data_operation,
    log_error
)

# Function lifecycle logging
log_function_start(context, "blob", {"BlobName": blob.name})
# ... do work ...
log_function_complete(context, "blob", duration_ms, {"RecordCount": count})

# Data operations
log_data_operation(
    message="Processing cohort job",
    operation_type="process",
    context=context,
    trigger_type="blob",
    entity_type="Job",
    entity_id=job_id,
    record_count=patient_count
)

# Error logging
log_error("Job failed", error, context, "blob", {"JobId": job_id})
```

**Key patterns**:
- Emoticons in messages for human readability (not searchable)
- `InvocationId` is the primary correlation key for tracing
- `DurationMs` calculated as `(end - start) * 1000`
- Global properties set once at startup via `seqlog.set_global_log_properties()`

---

## Proposed Custom Tools

### 1. AppSearch - Simplified Application Queries
**Purpose**: Query by app name with aliases and time presets

```csharp
AppSearch(
    app: string,           // App name or alias ("pims", "fx-pims", "data")
    level?: string,        // "error", "warning", "info" (default: all)
    hours?: int,           // Hours back (default: 24)
    function_name?: string,// Filter by FunctionName
    count?: int            // Max results (default: 50)
)
```

**App Aliases to implement**:
```
"pims" | "pims-services" → AppName = 'pims-services'
"fx-pims" | "pims-fx" → AppName = 'fx-app-pims-services'
"data" | "data-services" → AppName = 'data-services'
"charta" → AppName = 'charta-services'
"fx-charta" → AppName = 'fx-app-charta-services'
"scribing" | "ai-scribing" → AppName = 'ai-scribing-services'
"zus" → AppName = 'zus-services'
"equip" → AppName = 'equip-services'
"meetingbaas" | "bot" → AppName = 'meetingbaas'
```

### 2. InvocationTrace - Function Execution Tracing
**Purpose**: Get all logs for a single Azure Function execution

```csharp
InvocationTrace(
    invocation_id: string  // The InvocationId UUID
)
```

**Returns**:
- All logs in chronological order
- Calculated total duration
- Error/warning flags
- Function name and trigger type

### 3. ErrorDashboard - Error Summary & Grouping
**Purpose**: Get error summaries instead of raw logs

```csharp
ErrorDashboard(
    hours?: int,           // Hours back (default: 24)
    group_by?: string,     // "app", "error_type", "function" (default: "app")
    app?: string           // Filter to specific app
)
```

**Returns**:
- Error counts grouped by specified field
- First/last occurrence timestamps
- Sample error messages
- Trend indicator (increasing/decreasing)

### 4. SlowExecutions - Performance Monitoring
**Purpose**: Find slow operations

```csharp
SlowExecutions(
    threshold_ms?: int,    // Minimum duration (default: 5000)
    app?: string,          // Filter by app
    function_name?: string,// Filter by function
    hours?: int,           // Hours back (default: 24)
    count?: int            // Max results (default: 20)
)
```

**Returns**:
- Logs where DurationMs > threshold
- Sorted by duration descending
- Includes context (FunctionName, EntityType, etc.)

### 5. EntityTrace - Cross-Service Correlation
**Purpose**: Track an entity across all services

```csharp
EntityTrace(
    entity_type: string,   // "Job", "Patient", "Payment", etc.
    entity_id: string,     // The entity ID value
    hours?: int            // Hours back (default: 24)
)
```

**Returns**:
- All logs mentioning this entity across ALL apps
- Chronological order showing entity journey
- Grouped by service for clarity

---

## Project Structure

```
seq_mcp/
├── src/SeqMcpServer/
│   ├── Program.cs                    # Entry point & MCP initialization
│   ├── SeqMcpServer.csproj          # .NET 9 project file
│   ├── Mcp/
│   │   └── SeqTools.cs              # MCP tool implementations (ADD NEW TOOLS HERE)
│   └── Services/
│       ├── ICredentialStore.cs
│       ├── EnvironmentCredentialStore.cs
│       └── SeqConnectionFactory.cs
├── tests/SeqMcpServer.Tests/
│   └── McpToolsIntegrationTests.cs  # Integration tests with Testcontainers
├── docs/
│   ├── Design.md                    # Architecture decisions
│   └── PRD.md                       # Requirements
└── .claude/
    └── CLAUDE.md                    # This file
```

## Development Commands

```bash
# Build
dotnet build src/SeqMcpServer/SeqMcpServer.csproj

# Run tests
dotnet test tests/SeqMcpServer.Tests/

# Pack as global tool
dotnet pack src/SeqMcpServer/SeqMcpServer.csproj -c Release

# Install locally for testing
dotnet tool install --global --add-source ./src/SeqMcpServer/bin/Release SeqMcpServer

# Uninstall
dotnet tool uninstall -g SeqMcpServer
```

## Environment Variables

```bash
SEQ_SERVER_URL=http://apps-seq-instance.eastus2.azurecontainer.io
SEQ_API_KEY=imx440QdCZzNPA2ccRRP
```

## Adding a New Tool

1. Add method to `src/SeqMcpServer/Mcp/SeqTools.cs`
2. Decorate with `[McpServerTool]` attribute
3. Use `[Description]` attributes for parameters
4. Access Seq via `SeqConnectionFactory` from DI
5. Return serializable objects (they become JSON)

Example:
```csharp
[McpServerTool]
[Description("Search logs by application name with aliases")]
public static async Task<List<EventEntity>> AppSearch(
    SeqConnectionFactory connectionFactory,
    [Description("App name or alias (e.g., 'pims', 'fx-charta')")] string app,
    [Description("Log level filter: error, warning, info, or all")] string level = "all",
    [Description("Hours to search back")] int hours = 24,
    [Description("Max results")] int count = 50,
    CancellationToken cancellationToken = default)
{
    var conn = connectionFactory.CreateConnection();
    var appName = ResolveAppAlias(app);
    var filter = BuildFilter(appName, level, hours);

    return await conn.Events
        .EnumerateAsync(filter: filter, count: count, render: true)
        .ToListAsync(cancellationToken);
}
```

## Testing with Claude Code

After installing the updated tool:
```bash
# Reinstall
dotnet tool uninstall -g SeqMcpServer
dotnet tool install -g SeqMcpServer --add-source ./src/SeqMcpServer/bin/Release

# Restart Claude Code to pick up changes
```

Then test in conversation:
```
"Search for errors in pims-services from the last hour"
"Trace invocation 07a578ad-0e67-4781-873e-04a63f7c80b5"
"Show me slow executions in data-services"
```
