namespace NetSdr.Items;

/// <summary>RF filter choice for <see cref="RfFilter"/>. Band edges are in MHz.</summary>
public enum RfFilterSelection : byte
{
    Auto = 0,
    Band0To1_8 = 1,
    Band1_8To2_8 = 2,
    Band2_8To4 = 3,
    Band4To5_5 = 4,
    Band5_5To7 = 5,
    Band7To10 = 6,
    Band10To14 = 7,
    Band14To20 = 8,
    Band20To28 = 9,
    Band28To34 = 10,
    Bypass = 11,
    NoPass = 12,
    DownConverter = 13,
}
