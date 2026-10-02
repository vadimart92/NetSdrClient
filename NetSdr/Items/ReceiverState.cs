using System.Runtime.InteropServices;

namespace NetSdr.Items;

/// <summary>Receiver state, item 0x0018: starts and stops data capture and selects the sample format.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct ReceiverState : IControlItem<ReceiverState>
{
    /// <summary>Value of <see cref="Run"/> while the receiver is stopped.</summary>
    public const byte Idle = 0x01;

    /// <summary>Value of <see cref="Run"/> while the receiver is capturing.</summary>
    public const byte Running = 0x02;

    private const byte ComplexFlag = 0x80;
    private const byte Bits24Flag = 0x80;

    public static ushort Code => 0x0018;

    public readonly byte DataType;
    public readonly byte Run;
    public readonly byte CaptureMode;
    public readonly byte FifoBlocks;

    public ReceiverState(byte dataType, byte run, byte captureMode, byte fifoBlocks)
    {
        DataType = dataType;
        Run = run;
        CaptureMode = captureMode;
        FifoBlocks = fifoBlocks;
    }

    /// <summary>The state that stops capture.</summary>
    public static ReceiverState Stop => new(0, Idle, 0, 0);

    /// <summary>True when <see cref="Run"/> is <see cref="Running"/>.</summary>
    public bool IsRunning => Run == Running;

    /// <summary>True when the data type asks for complex (I/Q) samples.</summary>
    public bool IsComplex => (DataType & ComplexFlag) != 0;

    /// <summary>True when the capture mode asks for 24-bit samples.</summary>
    public bool Is24Bit => (CaptureMode & Bits24Flag) != 0;

    /// <summary>The state that starts capture.</summary>
    /// <param name="complex">Complex (I/Q) samples when true, real samples otherwise.</param>
    /// <param name="bits24">24-bit samples when true, 16-bit otherwise.</param>
    /// <param name="mode">Capture mode.</param>
    /// <param name="fifoBlocks">Number of blocks for <see cref="NetSdr.Items.CaptureMode.Fifo"/> capture.</param>
    public static ReceiverState Start(
        bool complex,
        bool bits24,
        NetSdr.Items.CaptureMode mode = NetSdr.Items.CaptureMode.Contiguous,
        byte fifoBlocks = 0) =>
        new(
            (byte)(complex ? ComplexFlag : 0),
            Running,
            (byte)((bits24 ? Bits24Flag : 0) | (byte)mode),
            fifoBlocks);
}
