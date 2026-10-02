using System.Runtime.InteropServices;

namespace NetSdr.Items;

/// <summary>Firmware version, item 0x0004. <see cref="Id"/> says which firmware component the version belongs to.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct FirmwareVersion : IControlItem<FirmwareVersion>
{
    public static ushort Code => 0x0004;

    public readonly byte Id;
    public readonly ushort Version;

    public FirmwareVersion(byte id, ushort version)
    {
        Id = id;
        Version = version;
    }

    /// <summary>For an FPGA entry, the configuration ID in the low byte of <see cref="Version"/>.</summary>
    public byte FpgaConfigId => (byte)Version;

    /// <summary>For an FPGA entry, the revision in the high byte of <see cref="Version"/>.</summary>
    public byte FpgaRevision => (byte)(Version >> 8);
}
