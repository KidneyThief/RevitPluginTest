using System.Globalization;
using System.Text;
using Autodesk.Revit.DB;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace RevitPluginTest
{
    // Parses a typed console command in the form "FunctionName(arg1, arg2, ...)".
    // Parentheses are required even with no arguments: "FunctionName()".
    // Arguments may be a quoted string, true/false, an int, a double, an
    // "XYZ(x, y, z)" point literal, a "Color(r, g, b [, a])" literal
    // (0-255, a defaults to 255), a "Color.Name" from WPF's named palette
    // (e.g. Color.Red), or fall back to a bare string.
    public static class CommandText
    {
        public static bool TryParse(string input, out string functionName, out object?[] args, out string? error)
        {
            functionName = string.Empty;
            args = Array.Empty<object?>();
            error = null;

            var text = input.Trim();
            var openParen = text.IndexOf('(');

            if (openParen <= 0 || !text.EndsWith(")"))
            {
                error = "Expected a function call like Name(arg1, arg2) - parentheses are required.";
                return false;
            }

            functionName = text.Substring(0, openParen).Trim();

            if (functionName.Length == 0)
            {
                error = "Missing function name.";
                return false;
            }

            var argsText = text.Substring(openParen + 1, text.Length - openParen - 2).Trim();

            if (argsText.Length == 0)
            {
                return true;
            }

            var tokens = SplitArgs(argsText);
            var parsed = new object?[tokens.Count];

            for (var i = 0; i < tokens.Count; i++)
            {
                var token = tokens[i].Trim();

                if (!TryParseArg(token, out parsed[i]))
                {
                    error = $"Could not parse argument '{token}'.";
                    return false;
                }
            }

            args = parsed;
            return true;
        }

        // Splits on top-level commas only - a comma inside a quoted string or
        // inside a nested "Name(...)" literal (e.g. XYZ(0, 5, 10)) doesn't split.
        private static List<string> SplitArgs(string argsText)
        {
            var tokens = new List<string>();
            var current = new StringBuilder();
            var inQuotes = false;
            var quoteChar = '"';
            var parenDepth = 0;

            foreach (var c in argsText)
            {
                if (inQuotes)
                {
                    current.Append(c);

                    if (c == quoteChar)
                    {
                        inQuotes = false;
                    }
                }
                else if (c is '"' or '\'')
                {
                    quoteChar = c;
                    inQuotes = true;
                    current.Append(c);
                }
                else if (c == '(')
                {
                    parenDepth++;
                    current.Append(c);
                }
                else if (c == ')')
                {
                    parenDepth--;
                    current.Append(c);
                }
                else if (c == ',' && parenDepth == 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
                else
                {
                    current.Append(c);
                }
            }

            tokens.Add(current.ToString());

            return tokens;
        }

        private static bool TryParseArg(string token, out object? value)
        {
            if (token.Length == 0)
            {
                value = null;
                return false;
            }

            if (token.StartsWith("XYZ(", StringComparison.OrdinalIgnoreCase) && token.EndsWith(")"))
            {
                return TryParseXyz(token, out value);
            }

            if (token.StartsWith("Color(", StringComparison.OrdinalIgnoreCase) && token.EndsWith(")"))
            {
                return TryParseRgbaColor(token, out value);
            }

            if (token.StartsWith("Color.", StringComparison.OrdinalIgnoreCase))
            {
                return TryParseNamedColor(token, out value);
            }

            if (token.Length >= 2 && (token[0] == '"' || token[0] == '\'') && token[^1] == token[0])
            {
                value = token.Substring(1, token.Length - 2);
                return true;
            }

            if (bool.TryParse(token, out var boolValue))
            {
                value = boolValue;
                return true;
            }

            if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intValue))
            {
                value = intValue;
                return true;
            }

            if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var doubleValue))
            {
                value = doubleValue;
                return true;
            }

            // Fall back to treating an unquoted bare token as a string literal.
            value = token;
            return true;
        }

        private static bool TryParseXyz(string token, out object? value)
        {
            value = null;

            var inner = ExtractParenContent(token, "XYZ(".Length);
            var components = SplitArgs(inner);

            if (components.Count != 3)
            {
                return false;
            }

            if (!double.TryParse(components[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ||
                !double.TryParse(components[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var y) ||
                !double.TryParse(components[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
            {
                return false;
            }

            value = new XYZ(x, y, z);
            return true;
        }

        private static bool TryParseRgbaColor(string token, out object? value)
        {
            value = null;

            var inner = ExtractParenContent(token, "Color(".Length);
            var components = SplitArgs(inner);

            if (components.Count != 3 && components.Count != 4)
            {
                return false;
            }

            if (!TryParseByte(components[0], out var r) ||
                !TryParseByte(components[1], out var g) ||
                !TryParseByte(components[2], out var b))
            {
                return false;
            }

            var a = (byte)255;

            if (components.Count == 4 && !TryParseByte(components[3], out a))
            {
                return false;
            }

            value = Color.FromArgb(a, r, g, b);
            return true;
        }

        private static bool TryParseByte(string token, out byte value)
        {
            value = 0;

            if (!int.TryParse(token.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var intValue) ||
                intValue is < 0 or > 255)
            {
                return false;
            }

            value = (byte)intValue;
            return true;
        }

        // Delegates to WPF's own name->Color resolver - the same mechanism
        // XAML uses to parse color names like "Red" or "CornflowerBlue".
        private static bool TryParseNamedColor(string token, out object? value)
        {
            value = null;

            var name = token.Substring("Color.".Length);

            try
            {
                if (ColorConverter.ConvertFromString(name) is Color color)
                {
                    value = color;
                    return true;
                }
            }
            catch (FormatException)
            {
            }

            return false;
        }

        private static string ExtractParenContent(string token, int prefixLength)
        {
            return token.Substring(prefixLength, token.Length - prefixLength - 1);
        }
    }
}
