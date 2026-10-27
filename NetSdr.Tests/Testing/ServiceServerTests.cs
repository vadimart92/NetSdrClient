using System.Net;
using System.Net.Sockets;
using System.Text;
using NetSdr.Control;
using NetSdr.Items;
using NetSdr.Testing;

namespace NetSdr.Tests.Testing;

public class ServiceServerTests
{
    private static readonly TimeSpan BootTime = TimeSpan.FromMilliseconds(300);

    /// <summary>A receiver and its service, both started.</summary>
    private static async Task<(NetSdrTestServer Receiver, NetSdrServiceServer Service)> StartAsync(Action<NetSdrServiceServer>? setup = null)
    {
        var receiver = new NetSdrTestServer();
        var service = new NetSdrServiceServer(receiver);
        setup?.Invoke(service);
        await receiver.StartAsync();
        await service.StartAsync();
        return (receiver, service);
    }

    [Test]
    public async Task Handler_ReadsFields_AndRepliesWithJson()
    {
        var (receiver, service) = await StartAsync(s => s.On("add", ctx =>
            ctx.ReplyAsync(new { Sum = ctx.Request.Get<int>("a") + ctx.Request.Get<int>("b") })));
        await using var _ = receiver;
        await using var __ = service;
        await using var line = await LineClient.ConnectAsync(service.Port);

        await line.SendAsync("""{"cmd":"add","a":2,"b":"3"}""");
        Assert.That(await line.ReadLineAsync(), Is.EqualTo("""{"sum":5}"""));
        var request = service.Requests.Sole();
        Assert.That((request.Command, request.Has("a"), request.Has("c")), Is.EqualTo(("add", true, false)));
    }

    [Test]
    public async Task OneConnection_ManyRequests_InOrder_CrLfAndBlankLines()
    {
        var (receiver, service) = await StartAsync(s => s.On("echo", ctx => ctx.ReplyAsync(new { N = ctx.Request.Get<int>("n") })));
        await using var _ = receiver;
        await using var __ = service;
        await using var line = await LineClient.ConnectAsync(service.Port);

        await line.SendRawAsync("{\"cmd\":\"echo\",\"n\":1}\r\n\n  \n{\"cmd\":\"echo\",\"n\":2}\n");
        Assert.That(await line.ReadLineAsync(), Is.EqualTo("""{"n":1}"""));
        Assert.That(await line.ReadLineAsync(), Is.EqualTo("""{"n":2}"""));
        Assert.That(service.Requests, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task Unhandled_DefaultsToErrors_AndCanBeReplaced()
    {
        var (receiver, service) = await StartAsync();
        await using var _ = receiver;
        await using var __ = service;
        await using var line = await LineClient.ConnectAsync(service.Port);

        await line.SendAsync("""{"cmd":"nope"}""");
        Assert.That(await line.ReadLineAsync(), Is.EqualTo("""{"error":"unknown command"}"""));
        await line.SendAsync("not json");
        Assert.That(await line.ReadLineAsync(), Is.EqualTo("""{"error":"bad request"}"""));
        await line.SendAsync("""{"cmd":7}""");
        Assert.That(await line.ReadLineAsync(), Is.EqualTo("""{"error":"bad request"}"""));
        Assert.That(service.Requests.Select(r => r.Json is null), Is.EqualTo(new[] { false, true, false }));

        service.OnUnhandled(ctx => ctx.ReplyRawAsync("ERR\n"));
        await line.SendAsync("""{"cmd":"nope"}""");
        Assert.That(await line.ReadLineAsync(), Is.EqualTo("ERR"));
    }

    [Test]
    public async Task CommandField_CanBeChanged()
    {
        var (receiver, service) = await StartAsync(s =>
        {
            s.CommandField = "op";
            s.On("ping", ctx => ctx.ReplyAsync(new { Pong = true }));
        });
        await using var _ = receiver;
        await using var __ = service;
        await using var line = await LineClient.ConnectAsync(service.Port);

        await line.SendAsync("""{"op":"ping"}""");
        Assert.That(await line.ReadLineAsync(), Is.EqualTo("""{"pong":true}"""));
    }

    [Test]
    public async Task Handler_WithoutReply_WritesNothing_AndTheConnectionGoesOn()
    {
        var (receiver, service) = await StartAsync(s =>
        {
            s.On("quiet", _ => Task.CompletedTask);
            s.On("ping", ctx => ctx.ReplyAsync(new { Pong = true }));
        });
        await using var _ = receiver;
        await using var __ = service;
        await using var line = await LineClient.ConnectAsync(service.Port);

        await line.SendAsync("""{"cmd":"quiet"}""");
        await line.SendAsync("""{"cmd":"ping"}""");
        Assert.That(await line.ReadLineAsync(), Is.EqualTo("""{"pong":true}"""));
    }

    [Test]
    public async Task Close_AfterHalfALine_TheClientReadsItThenTheEnd()
    {
        var (receiver, service) = await StartAsync(s => s.On("reboot", async ctx =>
        {
            await ctx.ReplyRawAsync("""{"status":""");
            ctx.Close();
        }));
        await using var _ = receiver;
        await using var __ = service;
        await using var line = await LineClient.ConnectAsync(service.Port);

        await line.SendAsync("""{"cmd":"reboot"}""");
        Assert.That(await line.ReadToEndAsync(), Is.EqualTo("""{"status":"""));
    }

    [Test]
    public async Task Abort_ResetsTheConnection()
    {
        var (receiver, service) = await StartAsync(s => s.On("reboot", ctx =>
        {
            ctx.Abort();
            return Task.CompletedTask;
        }));
        await using var _ = receiver;
        await using var __ = service;
        await using var line = await LineClient.ConnectAsync(service.Port);

        await line.SendAsync("""{"cmd":"reboot"}""");
        await Assert.ThatAsync(() => line.ReadToEndAsync(), Throws.InstanceOf<IOException>());
    }

    [Test]
    public async Task FailingHandler_IsRecorded_ClosesItsConnection_AndTheServiceGoesOn()
    {
        var failure = new InvalidOperationException("firmware bug");
        var (receiver, service) = await StartAsync(s =>
        {
            s.On("crash", _ => throw failure);
            s.On("ping", ctx => ctx.ReplyAsync(new { Pong = true }));
        });
        await using var _ = receiver;
        await using var __ = service;

        await using (var first = await LineClient.ConnectAsync(service.Port))
        {
            await first.SendAsync("""{"cmd":"crash"}""");
            Assert.That(await first.ReadToEndAsync(), Is.Empty);
        }

        Assert.That(service.HandlerErrors.Sole(), Is.SameAs(failure));
        await using var second = await LineClient.ConnectAsync(service.Port);
        await second.SendAsync("""{"cmd":"ping"}""");
        Assert.That(await second.ReadLineAsync(), Is.EqualTo("""{"pong":true}"""));
    }

    [Test]
    public async Task Reboot_DropsTheClient_RefusesDuringTheBoot_AndComesBackFresh()
    {
        var (receiver, service) = await StartAsync(s => s.On("reboot", async ctx =>
        {
            if (ctx.Request.Get<string>("key") != "secret")
            {
                await ctx.ReplyAsync(new { Status = "bad_key" });
                return;
            }

            await ctx.ReplyAsync(new { Status = "ok" });
            await ctx.RebootAsync(BootTime, clearState: true, onBoot: r => r.Preload(new ProductId(7)));
        }));
        await using var _ = receiver;
        await using var __ = service;
        receiver.Preload(new ProductId(1));
        await using var client = new NetSdrControlClient();
        await client.ConnectAsync(new IPEndPoint(IPAddress.Loopback, receiver.Port));
        await client.SetAsync(new AfGain(0, 7));
        await using var line = await LineClient.ConnectAsync(service.Port);

        await line.SendAsync("""{"cmd":"reboot","key":"wrong"}""");
        Assert.That(await line.ReadLineAsync(), Is.EqualTo("""{"status":"bad_key"}"""));
        Assert.That(service.IsBooting, Is.False);

        await line.SendAsync("""{"cmd":"reboot","key":"secret"}""");
        Assert.That(await line.ReadLineAsync(), Is.EqualTo("""{"status":"ok"}"""));
        await Eventually.ThatAsync(() => !client.IsConnected);
        Assert.That((service.IsBooting, receiver.Availability), Is.EqualTo((true, ServerAvailability.CloseOnAccept)));

        await Eventually.ThatAsync(() => !service.IsBooting);
        Assert.That(receiver.Availability, Is.EqualTo(ServerAvailability.Normal));
        await using var after = new NetSdrControlClient();
        await after.ConnectAsync(new IPEndPoint(IPAddress.Loopback, receiver.Port));
        Assert.That((await after.GetAsync<ProductId>().WaitAsync(Limits.Test)).Value, Is.EqualTo(7u));
        await Assert.ThatAsync(() => after.GetAsync<AfGain, byte>(0).WaitAsync(Limits.Test), Throws.TypeOf<NetSdrNakException>());
    }

    [Test]
    public async Task Reboot_WhileBooting_ReturnsFalse_AndLeavesTheRunningOne()
    {
        var (receiver, service) = await StartAsync();
        await using var _ = receiver;
        await using var __ = service;

        Task<bool> first = service.RebootAsync(BootTime);
        Assert.That(await service.RebootAsync(TimeSpan.Zero), Is.False);
        Assert.That(service.IsBooting, Is.True);
        Assert.That(await first.WaitAsync(Limits.Test), Is.True);
        Assert.That(service.IsBooting, Is.False);
    }

    [Test]
    public async Task Reboot_KeepsTheAvailabilityOnBootSets()
    {
        var (receiver, service) = await StartAsync();
        await using var _ = receiver;
        await using var __ = service;

        await service.RebootAsync(TimeSpan.Zero, onBoot: r => r.Availability = ServerAvailability.Silent).WaitAsync(Limits.Test);
        Assert.That(receiver.Availability, Is.EqualTo(ServerAvailability.Silent));
    }

    [Test]
    public async Task Reboot_FailingOnBoot_IsRecorded_AndTheReceiverComesBack()
    {
        var failure = new InvalidOperationException("bad preload");
        var (receiver, service) = await StartAsync();
        await using var _ = receiver;
        await using var __ = service;

        Assert.That(await service.RebootAsync(TimeSpan.Zero, onBoot: _ => throw failure).WaitAsync(Limits.Test), Is.True);
        Assert.That(service.HandlerErrors.Sole(), Is.SameAs(failure));
        Assert.That(receiver.Availability, Is.EqualTo(ServerAvailability.Normal));
    }

    [Test]
    public async Task Reboot_GoesOnWhenTheAskingConnectionCloses()
    {
        var (receiver, service) = await StartAsync(s => s.On("reboot", async ctx =>
        {
            await ctx.ReplyAsync(new { Status = "ok" });
            ctx.Close();
            await ctx.RebootAsync(BootTime);
        }));
        await using var _ = receiver;
        await using var __ = service;
        await using var line = await LineClient.ConnectAsync(service.Port);

        await line.SendAsync("""{"cmd":"reboot"}""");
        Assert.That(await line.ReadToEndAsync(), Is.EqualTo("{\"status\":\"ok\"}\n"));
        await Eventually.ThatAsync(() => service.IsBooting);
        await Eventually.ThatAsync(() => !service.IsBooting);
        Assert.That(receiver.Availability, Is.EqualTo(ServerAvailability.Normal));
    }

    [Test]
    public async Task Dispose_DuringABoot_BringsTheReceiverBack()
    {
        var receiver = new NetSdrTestServer();
        await using var _ = receiver;
        await receiver.StartAsync();
        var service = new NetSdrServiceServer(receiver);
        await service.StartAsync();
        Task<bool> boot = service.RebootAsync(TimeSpan.FromMinutes(1));
        await Eventually.ThatAsync(() => receiver.Availability == ServerAvailability.CloseOnAccept);

        await service.DisposeAsync().AsTask().WaitAsync(Limits.Test);
        Assert.That(await boot.WaitAsync(Limits.Test), Is.True);
        Assert.That(receiver.Availability, Is.EqualTo(ServerAvailability.Normal));
        Assert.That(() => service.RebootAsync(TimeSpan.Zero), Throws.TypeOf<ObjectDisposedException>());
    }

    [Test]
    public async Task Dispose_ClosesOpenConnections()
    {
        var (receiver, service) = await StartAsync(s => s.On("ping", ctx => ctx.ReplyAsync(new { Pong = true })));
        await using var _ = receiver;
        await using var line = await LineClient.ConnectAsync(service.Port);
        await line.SendAsync("""{"cmd":"ping"}""");
        Assert.That(await line.ReadLineAsync(), Is.EqualTo("""{"pong":true}"""));

        await service.DisposeAsync().AsTask().WaitAsync(Limits.Test);
        Assert.That(await line.ReadToEndAsync(), Is.Empty);
    }

    /// <summary>A client of the service that sends and reads lines of text.</summary>
    private sealed class LineClient : IAsyncDisposable
    {
        private readonly TcpClient _tcp;
        private readonly NetworkStream _stream;
        private readonly StreamReader _reader;

        private LineClient(TcpClient tcp)
        {
            _tcp = tcp;
            _stream = tcp.GetStream();
            _reader = new StreamReader(_stream, Encoding.UTF8);
        }

        public static async Task<LineClient> ConnectAsync(int port)
        {
            var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, port);
            return new LineClient(tcp);
        }

        public Task SendAsync(string line) => SendRawAsync(line + "\n");

        public async Task SendRawAsync(string text) => await _stream.WriteAsync(Encoding.UTF8.GetBytes(text));

        public async Task<string?> ReadLineAsync() =>
            await _reader.ReadLineAsync(new CancellationTokenSource(Limits.Test).Token);

        public async Task<string> ReadToEndAsync() =>
            await _reader.ReadToEndAsync(new CancellationTokenSource(Limits.Test).Token);

        public ValueTask DisposeAsync()
        {
            _reader.Dispose();
            _tcp.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
