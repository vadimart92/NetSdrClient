namespace NetSdr.Identification;

/// <summary>The FPGA entry of the firmware version item (0x0004, ID 3).</summary>
/// <param name="ConfigId">The FPGA configuration ID.</param>
/// <param name="Revision">The FPGA revision.</param>
public readonly record struct FpgaInfo(byte ConfigId, byte Revision);
