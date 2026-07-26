using System.Xml.Linq;

namespace MonetIconGenerator.Core;

/// <summary>
/// Android 二进制 XML 解析器。
/// 翻译自 Python main.py 第 601-681 行。
/// </summary>
public static class BinaryXmlParser
{
    private static ushort U16(byte[] data, int offset)
        => (ushort)(data[offset] | (data[offset + 1] << 8));

    private static uint U32(byte[] data, int offset)
        => (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24));

    private static string? PoolString(string[] strings, uint index)
    {
        if (index == 0xFFFFFFFF || index >= strings.Length) return null;
        return strings[index];
    }

    private static string TypedXmlValue(string[] strings, byte[] data, int attrPos)
    {
        uint rawIdx = U32(data, attrPos + 8);
        var raw = PoolString(strings, rawIdx);
        if (raw != null) return raw;

        byte dataType = data[attrPos + 15];
        int dataValue = (int)U32(data, attrPos + 16);

        return dataType switch
        {
            0x03 => PoolString(strings, (uint)dataValue) ?? "",
            0x12 => dataValue != 0 ? "true" : "false",
            0x10 => dataValue.ToString(),
            0x11 => $"0x{dataValue:x}",
            0x01 => $"@0x{(uint)dataValue:x8}",
            >= 0x1c and <= 0x1f => $"#{(uint)dataValue:x8}",
            _ => dataValue.ToString()
        };
    }

    public static XElement ToXElement(byte[] xmlData)
    {
        // 第一遍：找字符串池
        string[] strings = Array.Empty<string>();
        int pos0 = U16(xmlData, 2);
        while (pos0 < xmlData.Length)
        {
            ushort ct = U16(xmlData, pos0);
            int cs = (int)U32(xmlData, pos0 + 4);
            if (ct == 0x0001)
            {
                strings = ArscParser.ParseStringPoolStatic(xmlData, pos0);
                break;
            }
            pos0 += cs;
        }

        if (strings.Length == 0)
            throw new Exception("二进制 XML 缺少字符串池。");

        // 第二遍：解析元素
        XElement? root = null;
        var stack = new Stack<XElement>();
        int pos = U16(xmlData, 2);

        while (pos < xmlData.Length)
        {
            ushort chunkType = U16(xmlData, pos);
            int headerSize = U16(xmlData, pos + 2);
            int chunkSize = (int)U32(xmlData, pos + 4);
            if (chunkSize <= 0) throw new Exception("二进制 XML 块大小异常。");

            if (chunkType == 0x0102)
            {
                var name = PoolString(strings, U32(xmlData, pos + 20))
                           ?? throw new Exception("二进制 XML 元素名为空。");
                var element = new XElement(name);

                int attrStart = U16(xmlData, pos + 24);
                int attrSize = U16(xmlData, pos + 26);
                int attrCount = U16(xmlData, pos + 28);
                int attrBase = pos + headerSize + attrStart;

                for (int idx = 0; idx < attrCount; idx++)
                {
                    int attrPos = attrBase + idx * attrSize;
                    var attrName = PoolString(strings, U32(xmlData, attrPos + 4));
                    if (attrName != null)
                        element.SetAttributeValue(attrName, TypedXmlValue(strings, xmlData, attrPos));
                }

                if (stack.Count > 0)
                    stack.Peek().Add(element);
                else
                    root = element;
                stack.Push(element);
            }
            else if (chunkType == 0x0103 && stack.Count > 0)
            {
                stack.Pop();
            }
            pos += chunkSize;
        }

        return root ?? throw new Exception("二进制 XML 没有根节点。");
    }
}

// === ArscParser 静态辅助方法（供 BinaryXmlParser 复用字符串池解析）===
public static partial class ArscParser
{
    public static string[] ParseStringPoolStatic(byte[] data, int offset)
    {
        // 委托给主解析类
        return ParseStringPoolHelper(data, offset);
    }

    internal static string[] ParseStringPoolHelper(byte[] data, int offset)
    {
        int headerSize = ArscU16(data, offset + 2);
        int stringCount = (int)ArscU32(data, offset + 8);
        uint flags = ArscU32(data, offset + 16);
        int stringsStart = (int)ArscU32(data, offset + 20);
        bool utf8 = (flags & 0x100) != 0;

        var result = new string[stringCount];
        for (int idx = 0; idx < stringCount; idx++)
        {
            int strOffset = (int)ArscU32(data, offset + headerSize + idx * 4);
            int p = offset + stringsStart + strOffset;
            try
            {
                if (utf8)
                {
                    byte first = data[p++];
                    if ((first & 0x80) != 0) p++;
                    first = data[p++];
                    int byteLen = first;
                    if ((first & 0x80) != 0) { byteLen = ((first & 0x7f) << 8) | data[p]; p++; }
                    result[idx] = System.Text.Encoding.UTF8.GetString(data, p, byteLen);
                }
                else
                {
                    int first = data[p] | (data[p + 1] << 8); p += 2;
                    int charLen = first;
                    if ((first & 0x8000) != 0) { charLen = ((first & 0x7fff) << 16) | (data[p] | (data[p + 1] << 8)); p += 2; }
                    result[idx] = System.Text.Encoding.Unicode.GetString(data, p, charLen * 2);
                }
            }
            catch { result[idx] = ""; }
        }
        return result;
    }

    private static ushort ArscU16(byte[] d, int o) => (ushort)(d[o] | (d[o + 1] << 8));
    private static uint ArscU32(byte[] d, int o) => (uint)(d[o] | (d[o + 1] << 8) | (d[o + 2] << 16) | (d[o + 3] << 24));
}
