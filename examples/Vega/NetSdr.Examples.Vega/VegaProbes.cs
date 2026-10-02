using NetSdr.Examples.Vega.Items;
using NetSdr.Identification;

namespace NetSdr.Examples.Vega;

/// <summary>The identification probes of the Vega receiver family.</summary>
public static class VegaProbes
{
    /// <summary>
    /// Creates the probe that unlocks a Vega receiver and reads its firmware version into a <see cref="VegaInfo"/> fact.
    /// A device whose product ID is not <see cref="VegaProtocol.ProductId"/> is left alone.
    /// </summary>
    /// <param name="unlockKey">The vendor key that unlocks the Vega items.</param>
    /// <remarks>
    /// A NAK of the unlock propagates as <see cref="NetSdrNakException"/>, because the key is wrong. A NAK of the
    /// firmware version item is not an error: it means firmware v1.
    /// </remarks>
    public static ProbeAsync Identify(uint unlockKey) => async (client, builder, ct) =>
    {
        if (builder.Current.ProductId != VegaProtocol.ProductId)
        {
            return;
        }

        // A NAK propagates: the key is wrong.
        await client.SetAsync(new VendorUnlock(unlockKey), ct).ConfigureAwait(false);

        try
        {
            var info = await client.GetAsync<VegaFirmwareInfo>(ct).ConfigureAwait(false);
            builder.Set(new VegaInfo(DeviceVersion.FromHundredths(info.Version), Unlocked: true));
        }
        catch (NetSdrNakException)
        {
            // Firmware v1 does not know item 0x8005.
            builder.Set(new VegaInfo(new Version(1, 0), Unlocked: true));
        }
    };
}
