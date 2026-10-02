namespace NetSdr.Framing;

/// <summary>Message type of a device-to-host frame (header bits 15..13).</summary>
public enum ReplyType : byte
{
    Response = 0,
    Unsolicited = 1,
    RangeResponse = 2,
    DataAck = 3,
    Data0 = 4,
    Data1 = 5,
    Data2 = 6,
    Data3 = 7,
}
