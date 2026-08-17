// CloudEmuera modification: Microsoft.VisualBasic.Strings.StrConv delegates
// East-Asian width and kana conversion to Windows NLS and throws
// PlatformNotSupportedException on Linux. Keep the pinned Emuera semantics in
// the headless build with a deterministic Unicode implementation.
using System.Collections.Generic;
using System.Text;

namespace MinorShift.Emuera.Runtime.Utils;

internal static class HeadlessStringConverter
{
    private static readonly IReadOnlyDictionary<char, string> NarrowKana = BuildNarrowKana();

    public static string ToNarrow(string value)
    {
        var result = new StringBuilder(value.Length);
        foreach (char character in value)
        {
            if (character == '\u3000')
                result.Append(' ');
            else if (character is >= '\uFF01' and <= '\uFF5E')
                result.Append((char)(character - 0xFEE0));
            else if (NarrowKana.TryGetValue(character, out string narrow))
                result.Append(narrow);
            else
                result.Append(character);
        }
        return result.ToString();
    }

    public static string ToWide(string value)
    {
        var result = new StringBuilder(value.Length);
        for (int index = 0; index < value.Length; index++)
        {
            char character = value[index];
            if (character == ' ')
            {
                result.Append('\u3000');
            }
            else if (character is >= '!' and <= '~')
            {
                result.Append((char)(character + 0xFEE0));
            }
            else if (character is >= '\uFF61' and <= '\uFF9F')
            {
                int length = index + 1 < value.Length && value[index + 1] is '\uFF9E' or '\uFF9F' ? 2 : 1;
                result.Append(value.Substring(index, length).Normalize(NormalizationForm.FormKC));
                index += length - 1;
            }
            else
            {
                result.Append(character);
            }
        }
        return result.ToString();
    }

    public static string ToKatakana(string value)
    {
        var result = new StringBuilder(value.Length);
        foreach (char character in value)
        {
            result.Append(character switch
            {
                >= '\u3041' and <= '\u3096' => (char)(character + 0x60),
                '\u309D' or '\u309E' => (char)(character + 0x60),
                _ => character,
            });
        }
        return result.ToString();
    }

    public static string ToHiragana(string value, bool widenHalfwidth)
    {
        string source = widenHalfwidth ? ToWide(value) : value;
        var result = new StringBuilder(source.Length);
        foreach (char character in source)
        {
            result.Append(character switch
            {
                >= '\u30A1' and <= '\u30F6' => (char)(character - 0x60),
                '\u30FD' or '\u30FE' => (char)(character - 0x60),
                _ => character,
            });
        }
        return result.ToString();
    }

    private static IReadOnlyDictionary<char, string> BuildNarrowKana()
    {
        var result = new Dictionary<char, string>();
        for (char halfwidth = '\uFF61'; halfwidth <= '\uFF9F'; halfwidth++)
            AddNormalized(result, halfwidth.ToString(), halfwidth.ToString());

        for (char halfwidth = '\uFF66'; halfwidth <= '\uFF9D'; halfwidth++)
        {
            AddNormalized(result, string.Concat(halfwidth, '\uFF9E'), string.Concat(halfwidth, '\uFF9E'));
            AddNormalized(result, string.Concat(halfwidth, '\uFF9F'), string.Concat(halfwidth, '\uFF9F'));
        }
        return result;
    }

    private static void AddNormalized(Dictionary<char, string> result, string source, string narrow)
    {
        string normalized = source.Normalize(NormalizationForm.FormKC);
        if (normalized.Length == 1)
            result[normalized[0]] = narrow;
    }
}
