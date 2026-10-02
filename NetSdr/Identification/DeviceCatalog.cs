using System.Net;
using NetSdr.Control;

namespace NetSdr.Identification;

/// <summary>
/// A registry of the kinds of device an application supports. It connects to a device, reads its
/// <see cref="DeviceIdentity"/> and creates the device object of the first registration that matches the identity.
/// </summary>
/// <typeparam name="TDevice">
/// The application's base type of its devices, usually an interface. Every registration produces it, so the result
/// needs no cast.
/// </typeparam>
/// <remarks>
/// <see cref="Register(string, Func{DeviceIdentity, bool}, Func{NetSdrControlClient, DeviceIdentity, TDevice})"/> and
/// <c>Default</c> are meant for the start of the application and are not thread safe: do not call them while
/// <see cref="ConnectAsync(string, int, CancellationToken)"/> or <see cref="AttachAsync"/> is running. Those two can be
/// called concurrently, each call has its own client. The catalog holds on to nothing after a device was created.
/// </remarks>
public sealed class DeviceCatalog<TDevice> where TDevice : class
{
    private readonly IdentificationOptions? _identification;
    private readonly NetSdrControlClientOptions? _clientOptions;
    private readonly List<Registration> _registrations = new();
    private Func<NetSdrControlClient, DeviceIdentity, CancellationToken, Task<TDevice>>? _default;

    /// <param name="identification">
    /// What to read from the device before matching; <see langword="null"/> reads the standard items only. The probes
    /// of every registration belong here: a probe that only suits some devices checks
    /// <see cref="DeviceIdentityBuilder.Current"/> and returns.
    /// </param>
    /// <param name="clientOptions">Settings of the clients <see cref="ConnectAsync(string, int, CancellationToken)"/> creates.</param>
    public DeviceCatalog(IdentificationOptions? identification = null, NetSdrControlClientOptions? clientOptions = null)
    {
        _identification = identification;
        _clientOptions = clientOptions;
    }

    /// <summary>The names of the registrations, in the order they were registered.</summary>
    public IReadOnlyList<string> Registrations => _registrations.Select(r => r.Name).ToArray();

    /// <summary>Whether a default was set with <c>Default</c>.</summary>
    public bool HasDefault => _default is not null;

    /// <summary>
    /// Registers a kind of device. The registration order is the priority: the first registration whose
    /// <paramref name="matches"/> returns <see langword="true"/> wins, so register the more specific rules first.
    /// </summary>
    /// <param name="name">Shown in <see cref="DeviceNotRecognizedException.Candidates"/>.</param>
    /// <param name="matches">Decides from the whole identity, including the facts of probes. An exception it throws is not caught.</param>
    /// <param name="create">Creates the device; from then on the device owns the client.</param>
    public DeviceCatalog<TDevice> Register(
        string name, Func<DeviceIdentity, bool> matches, Func<NetSdrControlClient, DeviceIdentity, TDevice> create)
    {
        ArgumentNullException.ThrowIfNull(create);
        return Register(name, matches, (client, identity, _) => Task.FromResult(create(client, identity)));
    }

    /// <summary>
    /// Registers a kind of device that is created asynchronously, for example after a few more requests.
    /// </summary>
    /// <param name="createAsync">Creates the device; it gets the token of the <c>ConnectAsync</c> or <c>AttachAsync</c> call.</param>
    /// <inheritdoc cref="Register(string, Func{DeviceIdentity, bool}, Func{NetSdrControlClient, DeviceIdentity, TDevice})"/>
    public DeviceCatalog<TDevice> Register(
        string name,
        Func<DeviceIdentity, bool> matches,
        Func<NetSdrControlClient, DeviceIdentity, CancellationToken, Task<TDevice>> createAsync)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(matches);
        ArgumentNullException.ThrowIfNull(createAsync);

        _registrations.Add(new Registration(name, matches, createAsync));
        return this;
    }

    /// <summary>
    /// Sets what is created for a device no registration matches. There is one default: setting it again replaces the
    /// previous one. It takes no part in the matching and is not among the candidates.
    /// </summary>
    public DeviceCatalog<TDevice> Default(Func<NetSdrControlClient, DeviceIdentity, TDevice> create)
    {
        ArgumentNullException.ThrowIfNull(create);
        return Default((client, identity, _) => Task.FromResult(create(client, identity)));
    }

    /// <summary>Sets the default that is created asynchronously; it gets the token of the <c>ConnectAsync</c> or <c>AttachAsync</c> call.</summary>
    /// <inheritdoc cref="Default(Func{NetSdrControlClient, DeviceIdentity, TDevice})"/>
    public DeviceCatalog<TDevice> Default(
        Func<NetSdrControlClient, DeviceIdentity, CancellationToken, Task<TDevice>> createAsync)
    {
        ArgumentNullException.ThrowIfNull(createAsync);

        _default = createAsync;
        return this;
    }

    /// <summary>
    /// Connects to the device, identifies it and creates the device object. The method owns the client until the
    /// device object has been returned: when anything fails, including a cancellation, the client is closed first.
    /// </summary>
    /// <param name="host">Host name or IP address of the device.</param>
    /// <param name="port">TCP port of the control channel; the device listens on 50000 by default.</param>
    /// <param name="ct">Cancels the connection, the identification and an asynchronous factory.</param>
    /// <exception cref="DeviceNotRecognizedException">No registration matches and there is no default.</exception>
    public Task<TDevice> ConnectAsync(string host, int port = 50000, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);
        return ConnectCoreAsync(client => client.ConnectAsync(host, port, ct), ct);
    }

    /// <inheritdoc cref="ConnectAsync(string, int, CancellationToken)"/>
    public Task<TDevice> ConnectAsync(IPEndPoint endPoint, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(endPoint);
        return ConnectCoreAsync(client => client.ConnectAsync(endPoint, ct), ct);
    }

    /// <summary>
    /// Identifies the device behind a client that is already connected and creates the device object. Unlike
    /// <c>ConnectAsync</c> it never closes the client, whatever fails, and the caller stays its owner until a device
    /// object has been returned.
    /// </summary>
    /// <exception cref="DeviceNotRecognizedException">No registration matches and there is no default.</exception>
    public Task<TDevice> AttachAsync(NetSdrControlClient client, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        return CreateAsync(client, ct);
    }

    private async Task<TDevice> ConnectCoreAsync(Func<NetSdrControlClient, Task> connect, CancellationToken ct)
    {
        var client = new NetSdrControlClient(_clientOptions);
        try
        {
            await connect(client).ConfigureAwait(false);
            return await CreateAsync(client, ct).ConfigureAwait(false);
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task<TDevice> CreateAsync(NetSdrControlClient client, CancellationToken ct)
    {
        DeviceIdentity identity = await DeviceIdentity.ReadAsync(client, _identification, ct).ConfigureAwait(false);

        foreach (Registration registration in _registrations)
        {
            if (registration.Matches(identity))
            {
                return await registration.CreateAsync(client, identity, ct).ConfigureAwait(false);
            }
        }

        if (_default is { } createDefault)
        {
            return await createDefault(client, identity, ct).ConfigureAwait(false);
        }

        throw new DeviceNotRecognizedException(identity, Registrations);
    }

    private readonly record struct Registration(
        string Name,
        Func<DeviceIdentity, bool> Matches,
        Func<NetSdrControlClient, DeviceIdentity, CancellationToken, Task<TDevice>> CreateAsync);
}
