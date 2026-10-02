namespace NetSdr.Items;

/// <summary>Capture mode, the low bits of the capture mode byte of <see cref="ReceiverState"/>.</summary>
public enum CaptureMode : byte
{
    /// <summary>Contiguous capture.</summary>
    Contiguous = 0,

    /// <summary>FIFO capture of a fixed number of blocks.</summary>
    Fifo = 1,

    /// <summary>Hardware-triggered capture.</summary>
    HardwareTriggered = 3,
}
