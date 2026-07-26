using System.Text;

namespace MonetIconGenerator.Core;

/// <summary>
/// Android resources.arsc 二进制解析器。
/// 翻译自 Python main.py 第 496-600 行的手工二进制解析。
/// </summary>
public static partial class ArscParser
{
    private static ushort U16(byte[] data, int offset)
        => (ushort)(data[offset] | (data[offset + 1] << 8));

    private static uint U32(byte[] data, int offset)
        => (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24));

    // === String Pool ===
    private static (int len, int pos) ReadUtf8Len(byte[] data, int offset)
    {
        byte first = data[offset++];
        if ((first & 0x80) != 0)
        {
            int len = ((first & 0x7f) << 8) | data[offset];
            return (len, offset + 1);
        }
        return (first, offset);
    }

    private static (int len, int pos) ReadUtf16Len(byte[] data, int offset)
    {
        int first = U16(data, offset);
        offset += 2;
        if ((first & 0x8000) != 0)
        {
            int len = ((first & 0x7fff) << 16) | U16(data, offset);
            return (len, offset + 2);
        }
        return (first, offset);
    }

    private static string[] ParseStringPool(byte[] data, int offset)
    {
        int headerSize = U16(data, offset + 2);
        int stringCount = (int)U32(data, offset + 8);
        uint flags = U32(data, offset + 16);
        int stringsStart = (int)U32(data, offset + 20);
        bool utf8 = (flags & 0x100) != 0;

        var result = new string[stringCount];
        for (int idx = 0; idx < stringCount; idx++)
        {
            int strOffset = (int)U32(data, offset + headerSize + idx * 4);
            int pos = offset + stringsStart + strOffset;
            try
            {
                if (utf8)
                {
                    var (_, p) = ReadUtf8Len(data, pos);
                    pos = p;
                    var (byteLen, p2) = ReadUtf8Len(data, pos);
                    pos = p2;
                    result[idx] = Encoding.UTF8.GetString(data, pos, byteLen);
                }
                else
                {
                    var (charLen, p) = ReadUtf16Len(data, pos);
                    pos = p;
                    result[idx] = Encoding.Unicode.GetString(data, pos, charLen * 2);
                }
            }
            catch
            {
                result[idx] = "";
            }
        }
        return result;
    }

    private static string? PoolString(string[] strings, uint index)
    {
        if (index == 0xFFFFFFFF || index >= strings.Length)
            return null;
        return strings[index];
    }

    // === 主解析入口 ===
    public record ArscEntry(string? Value, int Type, int Data);

    public static Dictionary<(string Type, string Key), ArscEntry> ParseResourceTableValues(byte[] arscData)
    {
        var entries = new Dictionary<(string Type, string Key), ArscEntry>();
        string[] globalStrings = Array.Empty<string>();

        int pos = U16(arscData, 2);
        while (pos < arscData.Length)
        {
            ushort chunkType = U16(arscData, pos);
            int headerSize = U16(arscData, pos + 2);
            int chunkSize = (int)U32(arscData, pos + 4);
            if (chunkSize <= 0) throw new Exception("resources.arsc 块大小异常。");

            if (chunkType == 0x0001)
            {
                globalStrings = ParseStringPool(arscData, pos);
            }
            else if (chunkType == 0x0200)
            {
                uint packageId = U32(arscData, pos + 8);
                var typeStrings = ParseStringPool(arscData, pos + (int)U32(arscData, pos + 268));
                var keyStrings = ParseStringPool(arscData, pos + (int)U32(arscData, pos + 276));

                int sub = pos + headerSize;
                int packageEnd = pos + chunkSize;
                while (sub < packageEnd)
                {
                    ushort subType = U16(arscData, sub);
                    int subHeader = U16(arscData, sub + 2);
                    int subSize = (int)U32(arscData, sub + 4);
                    if (subSize <= 0) throw new Exception("resources.arsc 子块大小异常。");

                    if (subType == 0x0201)
                    {
                        int typeId = arscData[sub + 8];
                        string typeName = (typeId > 0 && typeId <= typeStrings.Length)
                            ? typeStrings[typeId - 1] : typeId.ToString();
                        int entryCount = (int)U32(arscData, sub + 12);
                        int entriesStart = (int)U32(arscData, sub + 16);

                        for (int entryIndex = 0; entryIndex < entryCount; entryIndex++)
                        {
                            uint entryOffset = U32(arscData, sub + subHeader + entryIndex * 4);
                            if (entryOffset == 0xFFFFFFFF) continue;

                            int entryPos = sub + entriesStart + (int)entryOffset;
                            int entrySize = U16(arscData, entryPos);
                            ushort flags = U16(arscData, entryPos + 2);
                            uint keyIndex = U32(arscData, entryPos + 4);

                            if ((flags & 0x0001) != 0 || keyIndex >= keyStrings.Length) continue;

                            int valuePos = entryPos + entrySize;
                            byte dataType = arscData[valuePos + 3];
                            int dataValue = (int)U32(arscData, valuePos + 4);

                            string? stringValue = null;
                            if (dataType == 0x03 && dataValue < globalStrings.Length)
                                stringValue = globalStrings[dataValue];

                            uint resId = (packageId << 24) | ((uint)typeId << 16) | (uint)entryIndex;
                            entries[(typeName, keyStrings[keyIndex])] = new ArscEntry(stringValue, dataType, dataValue);
                        }
                    }
                    sub += subSize;
                }
            }
            pos += chunkSize;
        }
        return entries;
    }
}
