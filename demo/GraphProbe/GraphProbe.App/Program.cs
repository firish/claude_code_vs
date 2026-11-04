using GraphProbe.Core;
using Serilog;

Log.Logger = new LoggerConfiguration().WriteTo.Console().CreateLogger();
Log.Information("{Json}", new Envelope("greeting", "hello").ToJson());
Log.CloseAndFlush();
