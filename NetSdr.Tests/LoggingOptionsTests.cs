using NetSdr.Control;
using NetSdr.Identification;

namespace NetSdr.Tests;

public class LoggingOptionsTests
{
    [Theory]
    [InlineData("NetSdrControlClient")]
    [InlineData("DeviceCatalog")]
    [InlineData("DeviceIdentity.ReadAsync")]
    public Task LoggerFactory_Null_Throws(string component) =>
        Assert.ThrowsAsync<ArgumentNullException>(() => CreateAsync(component, nullLoggerFactory: true));

    [Theory]
    [InlineData("NetSdrControlClient")]
    public Task TimeProvider_Null_Throws(string component) =>
        Assert.ThrowsAsync<ArgumentNullException>(() => CreateAsync(component, nullLoggerFactory: false));

    // Exactly one of the two options is null; a synchronous throw is caught by ThrowsAsync like a faulted task.
    static Task CreateAsync(string component, bool nullLoggerFactory) => component switch
    {
        "NetSdrControlClient" => Run(() => new NetSdrControlClient(nullLoggerFactory
            ? new NetSdrControlClientOptions { LoggerFactory = null! }
            : new NetSdrControlClientOptions { TimeProvider = null! })),
        "DeviceCatalog" => Run(() => new DeviceCatalog<object>(new IdentificationOptions { LoggerFactory = null! })),
        "DeviceIdentity.ReadAsync" => DeviceIdentity.ReadAsync(new NetSdrControlClient(), new IdentificationOptions { LoggerFactory = null! }),
        _ => throw new ArgumentOutOfRangeException(nameof(component)),
    };

    static Task Run(Func<object> create)
    {
        create();
        return Task.CompletedTask;
    }
}
