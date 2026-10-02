using NetSdr.Control;
using NetSdr.Framing;
using NetSdr.Items;

namespace NetSdr.Identification;

/// <summary>Ready-made probes for <see cref="IdentificationOptions.Probes"/>.</summary>
public static class Probes
{
    /// <summary>
    /// A probe that gets the control item <typeparamref name="T"/> and stores it as a fact of type <typeparamref name="T"/>.
    /// A NAK stores nothing and marks the item code of <typeparamref name="T"/> unsupported; any other failure is thrown.
    /// </summary>
    public static ProbeAsync Item<T>() where T : struct, IControlItem<T> =>
        async (client, builder, ct) =>
        {
            if (await TryGetAsync<T>(client, builder, ct).ConfigureAwait(false) is { } item)
            {
                builder.Set(item);
            }
        };

    /// <summary>
    /// A probe that gets the control item <typeparamref name="T"/> identified by <paramref name="key"/> (for example a channel
    /// number) and stores it as a fact of type <typeparamref name="T"/>. A NAK stores nothing and marks the item code
    /// of <typeparamref name="T"/> unsupported; any other failure is thrown.
    /// </summary>
    public static ProbeAsync Item<T, TKey>(TKey key)
        where T : struct, IControlItem<T> where TKey : unmanaged =>
        async (client, builder, ct) =>
        {
            if (await TryGetAsync<T, TKey>(client, builder, key, ct).ConfigureAwait(false) is { } item)
            {
                builder.Set(item);
            }
        };

    /// <summary>Gets <typeparamref name="T"/>; a NAK gives <see langword="null"/> and marks the item code unsupported.</summary>
    internal static Task<T?> TryGetAsync<T>(NetSdrControlClient client, DeviceIdentityBuilder builder, CancellationToken ct)
        where T : struct, IControlItem<T> =>
        NakToUnsupportedAsync(client.GetAsync<T>(ct), builder, T.Code);

    /// <summary>Gets <typeparamref name="T"/> for <paramref name="key"/>; a NAK gives <see langword="null"/> and marks the item code unsupported.</summary>
    internal static Task<T?> TryGetAsync<T, TKey>(
        NetSdrControlClient client, DeviceIdentityBuilder builder, TKey key, CancellationToken ct)
        where T : struct, IControlItem<T> where TKey : unmanaged =>
        NakToUnsupportedAsync(client.GetAsync<T, TKey>(key, ct), builder, T.Code);

    // Only a NAK is the device's own answer; a timeout, a lost connection or an unreadable reply is a problem of the
    // link or a mismatch with the device, and has to reach the caller.
    private static async Task<T?> NakToUnsupportedAsync<T>(Task<T> request, DeviceIdentityBuilder builder, ushort code)
        where T : struct
    {
        try
        {
            return await request.ConfigureAwait(false);
        }
        catch (NetSdrNakException)
        {
            builder.MarkUnsupported(code);
            return null;
        }
    }
}

/// <summary>The probes for the standard items 0x0001 to 0x000A, which fill the standard fields of the identity.</summary>
internal static class StandardProbes
{
    // The IDs of the components 0x0004 reports.
    private const byte BootId = 0;
    private const byte FirmwareId = 1;
    private const byte HardwareId = 2;
    private const byte FpgaId = 3;
    private const int ComponentCount = 4;

    // A component is reported as its ID byte and a 16-bit version.
    private const int FirmwareEntrySize = sizeof(byte) + sizeof(ushort);

    internal static async Task RunAsync(NetSdrControlClient client, DeviceIdentityBuilder builder, CancellationToken ct)
    {
        if (await Probes.TryGetAsync<TargetName>(client, builder, ct).ConfigureAwait(false) is { } name)
        {
            builder.Name = name.Value;
        }

        if (await Probes.TryGetAsync<SerialNumber>(client, builder, ct).ConfigureAwait(false) is { } serial)
        {
            builder.SerialNumber = serial.Value;
        }

        if (await Probes.TryGetAsync<InterfaceVersion>(client, builder, ct).ConfigureAwait(false) is { } iface)
        {
            builder.InterfaceVersion = DeviceVersion.FromHundredths(iface.Version);
        }

        await ReadFirmwareAsync(client, builder, ct).ConfigureAwait(false);

        if (await Probes.TryGetAsync<ProductId>(client, builder, ct).ConfigureAwait(false) is { } product)
        {
            builder.ProductId = product.Value;
        }

        if (await Probes.TryGetAsync<Options>(client, builder, ct).ConfigureAwait(false) is { } options)
        {
            builder.Options = options;
        }
    }

    // Item 0x0004 is asked for one component at a time. A device may know some components and not others, so
    // the code is unsupported only when it refused all of them.
    private static async Task ReadFirmwareAsync(NetSdrControlClient client, DeviceIdentityBuilder builder, CancellationToken ct)
    {
        int rejected = 0;
        for (byte id = BootId; id <= FpgaId; id++)
        {
            ControlItemMessage reply;
            try
            {
                reply = await client.SendAsync(RequestType.Get, FirmwareVersion.Code, new[] { id }, ct).ConfigureAwait(false);
            }
            catch (NetSdrNakException)
            {
                rejected++;
                continue;
            }

            // An answer without a version means the device has no such component. Unlike a NAK it does not
            // count against the code.
            if (reply.Payload.Length < FirmwareEntrySize)
            {
                continue;
            }

            FirmwareVersion entry = reply.As<FirmwareVersion>();
            switch (id)
            {
                case BootId:
                    builder.BootVersion = DeviceVersion.FromHundredths(entry.Version);
                    break;
                case FirmwareId:
                    builder.FirmwareVersion = DeviceVersion.FromHundredths(entry.Version);
                    break;
                case HardwareId:
                    builder.HardwareVersion = DeviceVersion.FromHundredths(entry.Version);
                    break;
                case FpgaId:
                    builder.Fpga = new FpgaInfo(entry.FpgaConfigId, entry.FpgaRevision);
                    break;
            }
        }

        if (rejected == ComponentCount)
        {
            builder.MarkUnsupported(FirmwareVersion.Code);
        }
    }
}
