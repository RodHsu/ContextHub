namespace Memory.Infrastructure;

/// <summary>Retains the pinned character map while disabling SentencePiece whitespace policy.
/// The declared tokenizer.json pipeline applies that policy before vocabulary encoding.</summary>
public static class E5SentencePieceModel
{
    public static byte[] ForDeclaredNormalizer(byte[] model, byte[] expectedCharacterMap)
    {
        if (model.Length > 64 * 1024 * 1024 || expectedCharacterMap.Length == 0)
            throw InvalidModel();
        using var output = new MemoryStream(model.Length + 16);
        var found = false;
        foreach (var field in Fields(model, 0, model.Length))
        {
            if (field.Number != 3)
            {
                output.Write(model, field.Start, field.Length);
                continue;
            }
            if (found || field.Wire != 2) throw InvalidModel();
            found = true;
            using var normalizer = new MemoryStream();
            var foundMap = false;
            var flags = new HashSet<int>();
            foreach (var setting in Fields(model, field.DataStart, field.DataLength))
            {
                if (setting.Number == 2)
                {
                    if (foundMap || setting.Wire != 2 ||
                        !model.AsSpan(setting.DataStart, setting.DataLength).SequenceEqual(expectedCharacterMap))
                        throw InvalidModel();
                    foundMap = true;
                }
                if (setting.Number is >= 3 and <= 5)
                {
                    var offset = setting.DataStart;
                    if (setting.Wire != 0 || !flags.Add(setting.Number) || ReadVarint(model, ref offset, setting.DataStart + setting.DataLength) > 1)
                        throw InvalidModel();
                    continue;
                }
                normalizer.Write(model, setting.Start, setting.Length);
            }
            if (!foundMap) throw InvalidModel();
            // NormalizerSpec fields 3/4/5: add_dummy_prefix, remove_extra_whitespaces,
            // escape_whitespaces. Character-map and unknown fields are unchanged.
            foreach (var flag in new[] { 3, 4, 5 })
            {
                WriteVarint(normalizer, (ulong)(flag << 3));
                WriteVarint(normalizer, 0);
            }
            WriteVarint(output, 26); // ModelProto.normalizer_spec, field 3, wire 2.
            WriteVarint(output, (ulong)normalizer.Length);
            normalizer.Position = 0;
            normalizer.CopyTo(output);
        }
        if (!found) throw InvalidModel();
        return output.ToArray();
    }

    private static IEnumerable<Field> Fields(byte[] bytes, int offset, int length)
    {
        var end = checked(offset + length);
        while (offset < end)
        {
            var start = offset;
            var tag = ReadVarint(bytes, ref offset, end);
            if (tag > uint.MaxValue || tag >> 3 == 0) throw InvalidModel();
            var wire = (int)(tag & 7);
            var dataStart = offset;
            int dataLength;
            if (wire == 0)
            {
                _ = ReadVarint(bytes, ref offset, end);
                dataLength = offset - dataStart;
            }
            else
            {
                dataLength = wire switch
                {
                    1 => 8,
                    2 => checked((int)ReadVarint(bytes, ref offset, end)),
                    5 => 4,
                    _ => throw InvalidModel()
                };
                dataStart = offset;
                if (dataLength > end - offset) throw InvalidModel();
                offset += dataLength;
            }
            yield return new((int)(tag >> 3), wire, start, offset - start, dataStart, dataLength);
        }
    }

    private static ulong ReadVarint(byte[] bytes, ref int offset, int end)
    {
        ulong value = 0;
        for (var shift = 0; shift < 64; shift += 7)
        {
            if (offset >= end) throw InvalidModel();
            var current = bytes[offset++];
            if (shift == 63 && current > 1) throw InvalidModel();
            value |= (ulong)(current & 127) << shift;
            if (current < 128) return value;
        }
        throw InvalidModel();
    }

    private static void WriteVarint(Stream output, ulong value)
    {
        while (value >= 128) { output.WriteByte((byte)(value | 128)); value >>= 7; }
        output.WriteByte((byte)value);
    }

    private static InvalidOperationException InvalidModel() => new("E5 SentencePiece model does not match its declared normalization contract.");
    private readonly record struct Field(int Number, int Wire, int Start, int Length, int DataStart, int DataLength);
}
