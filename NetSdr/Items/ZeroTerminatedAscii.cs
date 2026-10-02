using System.Text;

namespace NetSdr.Items;

/// <summary>The ASCII-with-terminating-zero string encoding shared by the text control items.</summary>
internal static class ZeroTerminatedAscii
{
    /// <summary>Decodes the bytes up to the first zero, or to the end when there is none.</summary>
    public static string Read(ReadOnlySpan<byte> source)
    {
        int end = source.IndexOf((byte)0);
        return Encoding.ASCII.GetString(end < 0 ? source : source[..end]);
    }

    /// <summary>Number of bytes <see cref="Write"/> produces: the text plus the terminating zero.</summary>
    public static int GetSize(string value) => value.Length + 1;

    /// <exception cref="ArgumentException"><paramref name="value"/> holds a character above 0x7F.</exception>
    public static void Write(string value, Span<byte> destination)
    {
        if (!Ascii.IsValid(value))
        {
            throw new ArgumentException("The text holds a character that is not ASCII.", nameof(value));
        }

        Encoding.ASCII.GetBytes(value, destination);
        destination[value.Length] = 0;
    }
}
