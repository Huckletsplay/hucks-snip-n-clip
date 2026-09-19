using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace QSnipAndClip
{
    internal static class CaptureFilename
    {
        internal const string DefaultTemplate = "{label}_{kind}_{date}_{hour}-{minute}-{second}-{milliseconds}";

        internal static string Render(string label, string template, string kind, DateTime started, long counter)
        {
            label = (label ?? "").Trim();
            if (label.Length == 0) label = "HucksSnipNClip";
            if (String.IsNullOrWhiteSpace(template)) template = DefaultTemplate;
            Dictionary<string, string> values = new Dictionary<string, string> {
                { "label", label }, { "kind", kind },
                { "date", started.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) },
                { "hour", started.ToString("HH", CultureInfo.InvariantCulture) },
                { "minute", started.ToString("mm", CultureInfo.InvariantCulture) },
                { "second", started.ToString("ss", CultureInfo.InvariantCulture) },
                { "milliseconds", started.ToString("fff", CultureInfo.InvariantCulture) },
                { "counter", Math.Max(1, counter).ToString("000", CultureInfo.InvariantCulture) }
            };
            StringBuilder output = new StringBuilder();
            for (int i = 0; i < template.Length; i++)
            {
                if (template[i] == '}') throw new ArgumentException("Unmatched } in filename template.");
                if (template[i] != '{') { output.Append(template[i]); continue; }
                int end = template.IndexOf('}', i + 1);
                if (end < 0) throw new ArgumentException("A filename placeholder is missing its closing }.");
                string token = template.Substring(i + 1, end - i - 1);
                string value;
                if (!values.TryGetValue(token, out value)) throw new ArgumentException("Unsupported placeholder: {" + token + "}.");
                output.Append(value); i = end;
            }
            string stem = output.ToString();
            ValidateStem(stem);
            return stem;
        }

        internal static void ValidateStem(string stem)
        {
            if (String.IsNullOrEmpty(stem) || stem.StartsWith(".") || stem.EndsWith(".") || stem.EndsWith(" "))
                throw new ArgumentException("A filename cannot be empty, begin with a period, or end with a space or period.");
            foreach (char c in stem)
                if (c < 32 || c == 127 || "<>:\"/\\|?*".IndexOf(c) >= 0)
                    throw new ArgumentException("The filename contains a character Windows cannot use.");
            if (Regex.IsMatch(stem.Split('.')[0], "^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase))
                throw new ArgumentException("That filename is reserved by Windows.");
            if (Encoding.UTF8.GetByteCount(stem) > 220) throw new ArgumentException("The filename is too long; shorten its label or template.");
        }
    }
}
