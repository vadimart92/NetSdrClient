namespace NetSdr.Framing;

/// <summary>Message type of a host-to-device frame (header bits 15..13).</summary>
public enum RequestType : byte
{
    Set = 0,
    Get = 1,
    GetRange = 2,
    DataAck = 3,
    Data0 = 4,
    Data1 = 5,
    Data2 = 6,
    Data3 = 7,
}
