using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RustCctv.Configuration;
using RustCctv.Recording;
using RustCctv.Rust;
using RustCctv.Services;

try
{
    var snapshot = args.Contains("--snapshot", StringComparer.Ordinal);
    var builder = Host.CreateApplicationBuilder(args.Where(a => a != "--snapshot").ToArray());
    builder.Logging.ClearProviders();
    builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "yyyy-MM-dd HH:mm:ss "; });
    var options = builder.Configuration.GetSection("Recorder").Get<RecorderOptions>() ?? new();
    var cameraOverride = Environment.GetEnvironmentVariable("RUST_CAMERA_ID");
    if (!string.IsNullOrEmpty(cameraOverride)) options.CameraId = cameraOverride;
    if (!options.IsValid()) throw new RecorderConfigurationException("Invalid Recorder configuration. Check the documented ranges and set CameraId or RUST_CAMERA_ID.");
    var connection = RustConnectionOptions.FromEnvironment();
    builder.Services.AddSingleton(options);
    builder.Services.AddSingleton(connection);
    builder.Services.AddSingleton(new RunMode(snapshot));
    builder.Services.AddSingleton<RecordingPathFactory>();
    builder.Services.AddSingleton<CameraRecorder>();
    builder.Services.AddHostedService<RecorderWorker>();
    builder.Services.Configure<HostOptions>(o =>
        o.ShutdownTimeout = TimeSpan.FromSeconds(options.FinalizeTimeoutSeconds + options.RequestTimeoutSeconds + 30));
    using var host = builder.Build();
    await host.RunAsync();
}
catch (Exception ex)
{
    // Do not print configuration binding errors: their messages can echo supplied values.
    Console.Error.WriteLine(ex is RecorderConfigurationException
        ? ex.Message
        : $"Startup failed ({ex.GetType().Name}). Check appsettings.json and required RUST_* environment variables. See README.md.");
    Environment.ExitCode = 1;
}
