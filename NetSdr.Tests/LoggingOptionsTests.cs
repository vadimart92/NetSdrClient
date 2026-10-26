using System.Net;
using NetSdr.Control;
using NetSdr.Data;
using NetSdr.Identification;

namespace NetSdr.Tests;

public class LoggingOptionsTests
{
    [TestCase("NetSdrControlClient")]
    [TestCase("DeviceCatalog")]
    [TestCase("DeviceIdentity.ReadAsync")]
    [TestCase("NetSdrDataReceiver")]
    [TestCase("ResilientControlClient")]
    public Task LoggerFactory_Null_Throws(string component) =>
        Assert.ThatAsync(() => CreateAsync(component, nullLoggerFactory: true), Throws.TypeOf<ArgumentNullException>());

    [TestCase("NetSdrControlClient")]
    [TestCase("NetSdrDataReceiver")]
    [TestCase("ResilientControlClient")]
    public Task TimeProvider_Null_Throws(string component) =>
        Assert.ThatAsync(() => CreateAsync(component, nullLoggerFactory: false), Throws.TypeOf<ArgumentNullException>());

    // Exactly one of the two options is null; a synchronous throw is caught by ThatAsync like a faulted task.
    private static Task CreateAsync(string component, bool nullLoggerFactory) => component switch
    {
        "NetSdrControlClient" => Run(() => new NetSdrControlClient(nullLoggerFactory
            ? new NetSdrControlClientOptions { LoggerFactory = null! }
            : new NetSdrControlClientOptions { TimeProvider = null! })),
        "DeviceCatalog" => Run(() => new DeviceCatalog<object>(new IdentificationOptions { LoggerFactory = null! })),
        "DeviceIdentity.ReadAsync" => DeviceIdentity.ReadAsync(new NetSdrControlClient(), new IdentificationOptions { LoggerFactory = null! }),
        "NetSdrDataReceiver" => Run(() => new NetSdrDataReceiver((in DataPacketInfo _, ReadOnlySpan<byte> _) => { }, nullLoggerFactory
            ? new DataReceiverOptions { LoggerFactory = null! }
            : new DataReceiverOptions { TimeProvider = null! })),
        "ResilientControlClient" => Run(() => ResilientControlClient.ConnectAsync(new IPEndPoint(IPAddress.Loopback, 1), nullLoggerFactory
            ? new ResilientControlClientOptions { LoggerFactory = null! }
            : new ResilientControlClientOptions { TimeProvider = null! })),
        _ => throw new ArgumentOutOfRangeException(nameof(component)),
    };

    private static Task Run(Func<object> create)
    {
        create();
        return Task.CompletedTask;
    }
}
