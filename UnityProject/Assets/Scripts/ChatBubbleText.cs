using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace TikTokLiveGame
{
    internal static class ChatBubbleText
    {
        // Bound work even when a malformed transport sends an enormous comment.
        internal static string Clean(string input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;
            var result = new StringBuilder(256);
            int limit = Math.Min(input.Length, 1024);
            bool space = false;
            for (int i = 0; i < limit; i++)
            {
                char c = input[i];
                if (char.IsWhiteSpace(c)) { space = result.Length > 0; continue; }
                if (char.IsControl(c) || (char.GetUnicodeCategory(c) == UnicodeCategory.Format && c != '\u200d' && c != '\u200c')) continue;
                if (char.IsLowSurrogate(c)) continue;
                if (char.IsHighSurrogate(c) && (i + 1 >= limit || !char.IsLowSurrogate(input[i + 1]))) continue;
                if (space) { result.Append(' '); space = false; }
                result.Append(c);
                if (char.IsHighSurrogate(c)) result.Append(input[++i]);
            }
            string clean = result.ToString().Normalize(NormalizationForm.FormC).Trim();
            // Invisible joiners alone are not messages.
            if (clean.Trim('\u200c', '\u200d', '\ufe0f', '\ufe0e').Length == 0) return string.Empty;
            List<int> ends = ElementEnds(clean);
            if (ends.Count > 120) return clean.Substring(0, ends[119]) + "…";
            return input.Length > limit ? clean + "…" : clean;
        }

        internal static string Fit(string value, float width, Func<string, float> measure)
        {
            if (measure(value) <= width) return value;
            const string dots = "…";
            if (measure(dots) > width) return string.Empty;
            List<int> ends = ElementEnds(value);
            int low = 0, high = ends.Count;
            while (low < high)
            {
                int mid = (low + high + 1) / 2;
                string candidate = value.Substring(0, ends[mid - 1]).TrimEnd() + dots;
                if (measure(candidate) <= width) low = mid; else high = mid - 1;
            }
            return low == 0 ? dots : value.Substring(0, ends[low - 1]).TrimEnd() + dots;
        }

        // Mono's older StringInfo splits some emoji. Keep surrogate pairs,
        // combining accents, flags, skin tones and ZWJ families together.
        internal static List<int> ElementEnds(string text)
        {
            var ends = new List<int>();
            int i = 0;
            while (i < text.Length)
            {
                int first = CodePoint(text, ref i);
                if (Regional(first) && i < text.Length)
                {
                    int next = i;
                    if (Regional(CodePoint(text, ref next))) i = next;
                }
                while (i < text.Length)
                {
                    int next = i;
                    int code = CodePoint(text, ref next);
                    UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(text, i);
                    if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark ||
                        code is >= 0x1f3fb and <= 0x1f3ff)
                        i = next;
                    else if (code == 0x200d && next < text.Length)
                    {
                        i = next;
                        CodePoint(text, ref i);
                    }
                    else break;
                }
                ends.Add(i);
            }
            return ends;
        }

        private static bool Regional(int code) => code is >= 0x1f1e6 and <= 0x1f1ff;
        private static int CodePoint(string text, ref int index)
        {
            int code = char.ConvertToUtf32(text, index);
            index += code > 0xffff ? 2 : 1;
            return code;
        }
    }
}
