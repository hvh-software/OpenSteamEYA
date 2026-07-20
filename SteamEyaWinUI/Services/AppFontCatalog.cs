using System.Buffers.Binary;
using System.Text;

namespace SteamEyaWinUI.Services;

internal sealed record AppFontOption(string DisplayName, string Source);

/// <summary>读取随应用发布在 Assets\Fonts 下的 OpenType/TrueType 字体。</summary>
internal static class AppFontCatalog
{
    private const string RelativeFontFolder = "Assets/Fonts";

    public static string FontFolderPath => Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts");

    public static IReadOnlyList<AppFontOption> Load()
    {
        var directory = FontFolderPath;
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var fonts = new List<AppFontOption>();
        foreach (var path in Directory.EnumerateFiles(directory)
                     .Where(path => Path.GetExtension(path) is ".ttf" or ".otf")
                     .OrderBy(path => path, StringComparer.CurrentCultureIgnoreCase))
        {
            var familyName = TryReadFamilyName(path) ?? Path.GetFileNameWithoutExtension(path);
            var fileName = Uri.EscapeDataString(Path.GetFileName(path));
            fonts.Add(new AppFontOption(
                familyName,
                $"ms-appx:///{RelativeFontFolder}/{fileName}#{familyName}"));
        }

        return fonts
            .DistinctBy(font => font.Source, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? TryReadFamilyName(string path)
    {
        try
        {
            var data = File.ReadAllBytes(path);
            if (data.Length < 12)
            {
                return null;
            }

            var tableCount = ReadUInt16(data, 4);
            var nameTableOffset = -1;
            var nameTableLength = 0;
            for (var index = 0; index < tableCount; index++)
            {
                var recordOffset = 12 + index * 16;
                if (recordOffset + 16 > data.Length)
                {
                    return null;
                }

                if (Encoding.ASCII.GetString(data, recordOffset, 4) != "name")
                {
                    continue;
                }

                nameTableOffset = checked((int)ReadUInt32(data, recordOffset + 8));
                nameTableLength = checked((int)ReadUInt32(data, recordOffset + 12));
                break;
            }

            if (nameTableOffset < 0 || nameTableOffset + 6 > data.Length ||
                nameTableOffset + nameTableLength > data.Length)
            {
                return null;
            }

            var nameCount = ReadUInt16(data, nameTableOffset + 2);
            var stringsOffset = nameTableOffset + ReadUInt16(data, nameTableOffset + 4);
            string? family = null;

            for (var index = 0; index < nameCount; index++)
            {
                var recordOffset = nameTableOffset + 6 + index * 12;
                if (recordOffset + 12 > nameTableOffset + nameTableLength)
                {
                    break;
                }

                var platformId = ReadUInt16(data, recordOffset);
                var nameId = ReadUInt16(data, recordOffset + 6);
                if (nameId is not (1 or 16))
                {
                    continue;
                }

                var length = ReadUInt16(data, recordOffset + 8);
                var offset = stringsOffset + ReadUInt16(data, recordOffset + 10);
                if (offset < 0 || offset + length > data.Length)
                {
                    continue;
                }

                var value = DecodeName(data.AsSpan(offset, length), platformId);
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                family = value.Trim();
                if (nameId == 16)
                {
                    return family;
                }
            }

            return family;
        }
        catch
        {
            return null;
        }
    }

    private static string? DecodeName(ReadOnlySpan<byte> bytes, ushort platformId)
    {
        if (platformId is 0 or 3)
        {
            if (bytes.Length % 2 != 0)
            {
                return null;
            }

            var chars = new char[bytes.Length / 2];
            for (var index = 0; index < chars.Length; index++)
            {
                chars[index] = (char)BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(index * 2, 2));
            }

            return new string(chars);
        }

        return platformId == 1 ? Encoding.ASCII.GetString(bytes) : null;
    }

    private static ushort ReadUInt16(byte[] data, int offset) =>
        BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset, 2));

    private static uint ReadUInt32(byte[] data, int offset) =>
        BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4));
}