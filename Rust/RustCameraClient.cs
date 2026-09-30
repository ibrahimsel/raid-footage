using System.Diagnostics;
using RustCctv.Configuration;
using RustPlusApi;
using RustPlusApi.Camera;
using RustPlusApi.Data;
using RustPlusApi.Data.Events;

namespace RustCctv.Rust;

public sealed class CameraSessionException(string reason) : Exception(reason);

public sealed class RustCameraClient : IAsyncDisposable
{
    private readonly RustPlus client;
    private readonly RecorderOptions options;
    private readonly object rendererLock = new();
    private readonly TaskCompletionSource firstFrame = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<string> failed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CameraController? controller;
    private CameraRenderer? renderer;
    private long lastFrameAt;
    private int width;
    private int height;
    private byte[]? lastPng;
    private bool dirty = true;
    private bool disposed;

    public RustCameraClient(RustConnectionOptions connection, RecorderOptions options)
    {
        this.options = options;
        // Do not enable protocol diagnostics; they may contain credential-bearing requests.
        client = new RustPlus(connection.ToConnection(), new RustPlusSocketOptions
        {
            RequestTimeout = TimeSpan.FromSeconds(options.RequestTimeoutSeconds),
            TeardownTimeout = TimeSpan.FromSeconds(3)
        });
        client.Disconnected += OnDisconnected;
        client.ErrorOccurred += OnError;
    }

    public async Task ConnectAsync(CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(options.RequestTimeoutSeconds));
        await client.ConnectAsync(deadline.Token);
        var result = await CameraController.SubscribeAsync(client, options.CameraId,
            cancellationToken: deadline.Token);
        if (!result.IsSuccess || result.Data is null)
            throw new CameraSessionException($"Camera subscription refused ({result.Error?.Code}).");
        controller = result.Data;
        if (controller.Info.Width is <= 0 or > 2048 || controller.Info.Height is <= 0 or > 2048)
            throw new CameraSessionException("Unsupported camera dimensions.");
        width = controller.Info.Width;
        height = controller.Info.Height;
        renderer = new CameraRenderer(width, height);
        controller.OnKeepAliveFailed += OnKeepAliveFailed;
        controller.OnFrameReceived += OnFrame;

        using var firstDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        firstDeadline.CancelAfter(TimeSpan.FromSeconds(options.FrameTimeoutSeconds));
        await Task.WhenAny(firstFrame.Task, failed.Task).WaitAsync(firstDeadline.Token);
        EnsureHealthy();
    }

    private void OnDisconnected(object? sender, EventArgs e) => failed.TrySetResult("Rust+ disconnected.");
    private void OnError(object? sender, Exception e) => failed.TrySetResult("Rust+ transport failed.");
    private void OnKeepAliveFailed(object? sender, ErrorMessage e) =>
        failed.TrySetResult($"Camera subscription renewal failed ({e.Code}).");

    private void OnFrame(object? sender, CameraRaysEventArg frame)
    {
        try
        {
            lock (rendererLock)
            {
                if (disposed || renderer is null) return;
                renderer.AddRays(frame);
                dirty = true;
                Interlocked.Exchange(ref lastFrameAt, Stopwatch.GetTimestamp());
            }
            firstFrame.TrySetResult();
        }
        catch (Exception)
        {
            // Never throw from the socket's callback or log server-supplied payloads.
            failed.TrySetResult("Camera ray decoding failed.");
        }
    }

    public void EnsureHealthy()
    {
        if (failed.Task.IsCompletedSuccessfully) throw new CameraSessionException(failed.Task.Result);
        if (!client.IsConnected) throw new CameraSessionException("Rust+ disconnected.");
        if (controller is not null && (controller.Info.Width != width || controller.Info.Height != height))
            throw new CameraSessionException("Camera dimensions changed; starting a new recording session.");
        if (Stopwatch.GetElapsedTime(Interlocked.Read(ref lastFrameAt)).TotalSeconds > options.FrameTimeoutSeconds)
            throw new CameraSessionException("Camera rays stopped arriving.");
    }

    public byte[] Render()
    {
        EnsureHealthy();
        lock (rendererLock)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (dirty || lastPng is null)
            {
                lastPng = renderer!.Render();
                dirty = false;
            }
            return lastPng;
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (rendererLock)
        {
            if (disposed) return;
            disposed = true;
        }
        client.Disconnected -= OnDisconnected;
        client.ErrorOccurred -= OnError;
        try
        {
            if (controller is not null)
            {
                controller.OnFrameReceived -= OnFrame;
                controller.OnKeepAliveFailed -= OnKeepAliveFailed;
                await controller.DisposeAsync();
            }
        }
        finally { await client.DisposeAsync(); }
    }
}
