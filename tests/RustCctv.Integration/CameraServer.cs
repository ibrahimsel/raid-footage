using System.Net;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using ProtoBuf;
using RustPlusContracts;

namespace RustCctv.Integration;

// Local synthetic wire-protocol server. Never connects to a real Rust account.
internal sealed class CameraServer : IAsyncDisposable
{
    private readonly WebApplication app;
    private WebSocket? active;
    private int subscriptions;
    private int connections;
    public volatile bool SendFrames = true;
    public volatile bool RejectRenewals;
    public int Subscriptions => Volatile.Read(ref subscriptions);
    public int Connections => Volatile.Read(ref connections);
    public int Port => new Uri(app.Urls.Single()).Port;

    public CameraServer()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
        app = builder.Build();
        app.UseWebSockets();
        app.Run(HandleAsync);
    }

    public Task StartAsync() => app.StartAsync();
    public void DropConnection() => active?.Abort();

    private async Task HandleAsync(HttpContext context)
    {
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        active = socket;
        Interlocked.Increment(ref connections);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        using var gate = new SemaphoreSlim(1);
        var subscribed = 0;
        var count = 0;
        var frames = PumpFramesAsync();
        try
        {
            var buffer = new byte[8192];
            while (socket.State == WebSocketState.Open)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult received;
                do
                {
                    received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
                    if (received.MessageType == WebSocketMessageType.Close)
                    {
                        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", cts.Token);
                        return;
                    }
                    message.Write(buffer, 0, received.Count);
                } while (!received.EndOfMessage);
                message.Position = 0;
                var request = Serializer.Deserialize<AppRequest>(message);
                var response = new AppResponse { Seq = request.Seq };
                if (request.CameraSubscribe is not null)
                {
                    Interlocked.Increment(ref subscriptions);
                    count++;
                    if (RejectRenewals && count > 1) response.Error = new AppError { Error = "not_found" };
                    else
                    {
                        response.CameraSubscribeInfo = new AppCameraInfo
                        { Width = 64, Height = 48, NearPlane = 0.1f, FarPlane = 1000f };
                        Volatile.Write(ref subscribed, 1);
                    }
                }
                else
                {
                    response.Success = new AppSuccess();
                    if (request.CameraUnsubscribe is not null) Volatile.Write(ref subscribed, 0);
                }
                await SendAsync(new AppMessage { Response = response });
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException) { }
        finally
        {
            Interlocked.CompareExchange(ref active, null, socket);
            cts.Cancel();
            try { await frames; }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException) { }
        }

        async Task SendAsync(AppMessage message)
        {
            using var data = new MemoryStream();
            Serializer.Serialize(data, message);
            await gate.WaitAsync(cts.Token);
            try { await socket.SendAsync(data.ToArray(), WebSocketMessageType.Binary, true, cts.Token); }
            finally { gate.Release(); }
        }

        async Task PumpFramesAsync()
        {
            // Full sky rays give the real renderer a complete, valid reconstruction.
            var rays = Enumerable.Range(0, 64 * 48).SelectMany(_ => new byte[] { 255, 255, 192, 0 }).ToArray();
            while (!cts.IsCancellationRequested)
            {
                await Task.Delay(125, cts.Token);
                if (Volatile.Read(ref subscribed) == 0 || !SendFrames) continue;
                await SendAsync(new AppMessage
                {
                    Broadcast = new AppBroadcast { CameraRays = new AppCameraRays { RayData = rays, SampleOffset = 0 } }
                });
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        active?.Abort();
        await app.StopAsync();
        await app.DisposeAsync();
    }
}
