using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RevitAddinManager.Mcp;

var builder = Host.CreateApplicationBuilder(args);
// stdout carries the MCP protocol, so every log line must go to stderr.
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Services.AddSingleton<RevitBridgeClient>();
builder.Services.AddSingleton<ScriptCompiler>();
builder.Services.AddSingleton<ApiCatalog>();
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
