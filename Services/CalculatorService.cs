using System.Globalization;

namespace quikbar.Services;

/// <summary>
/// Safe expression evaluator for the launcher. No DataTable, no code exec.
/// Supports + - * / % ^ () unary-minus, constants pi/e, functions
/// sqrt abs sin cos tan log ln exp floor ceil round, implicit multiplication
/// (2(3+4), 2pi), x/×/÷ aliases, thousands separators. Trig in degrees.
/// </summary>
public static class CalculatorService
{
    private const int MaxInputLength = 100;

    public static bool TryEvaluate(string? expr, out string display, out string copyValue)
    {
        display = "";
        copyValue = "";
        if (string.IsNullOrWhiteSpace(expr)) return false;
        var s = expr.Trim();
        if (s.Length == 0 || s.Length > MaxInputLength) return false;

        // Strip optional leading "=" or "calc" prefix users often type.
        if (s.StartsWith("=")) s = s[1..].Trim();
        if (s.StartsWith("calc ", StringComparison.OrdinalIgnoreCase))
            s = s[5..].Trim();
        if (s.Length == 0) return false;

        // Aliases
        s = s.Replace('×', '*').Replace('÷', '/').Replace('−', '-');

        // Must look like math, not an app name: require an operator/paren/identifier
        // plus a digit or parens (so "ln(e)" passes but "code"/"pi"/"123" don't hijack search).
        bool hasDigit = false, hasOp = false, hasParen = false;
        foreach (var ch in s)
        {
            if (char.IsDigit(ch)) hasDigit = true;
            if ("+-*/%^()".Contains(ch)) hasOp = true;
            if (char.IsLetter(ch)) hasOp = true; // pi, sqrt, ...
            if (ch == '(' || ch == ')') hasParen = true;
        }
        if (!hasOp || (!hasDigit && !hasParen)) return false;

        // Allow-list characters: digits, letters (funcs/constants), ops, parens, dot, comma, space.
        foreach (var ch in s)
        {
            if (char.IsDigit(ch) || char.IsLetter(ch)) continue;
            if ("+-*/%^()., \t".IndexOf(ch) >= 0) continue;
            return false;
        }

        try
        {
            var p = new Parser(s);
            double v = p.ParseFull();
            if (double.IsNaN(v) || double.IsInfinity(v)) return false;
            // Round tiny float noise (e.g. 0.1+0.2).
            v = Math.Round(v, 10);
            if (double.IsNaN(v) || double.IsInfinity(v)) return false;
            display = Format(v);
            copyValue = v.ToString("G12", CultureInfo.InvariantCulture);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static string Format(double v)
    {
        if (Math.Abs(v) >= 1e12 || (Math.Abs(v) < 1e-9 && v != 0))
            return v.ToString("G6", CultureInfo.InvariantCulture);
        // Grouped display, up to 10 decimals trimmed.
        return v.ToString("#,##0.##########", CultureInfo.InvariantCulture);
    }

    private sealed class Parser
    {
        private readonly string _s;
        private int _pos;

        public Parser(string s)
        {
            // Remove thousands separators: comma between digits.
            // Keep comma as round() arg separator otherwise.
            _s = s;
            _pos = 0;
        }

        public double ParseFull()
        {
            double v = ParseExpr();
            SkipWs();
            if (_pos != _s.Length) throw new InvalidOperationException("trailing");
            return v;
        }

        // expr := term ((+|-) term)*
        private double ParseExpr()
        {
            double v = ParseTerm();
            while (true)
            {
                SkipWs();
                if (Match('+')) v += ParseTerm();
                else if (Match('-')) v -= ParseTerm();
                else return v;
            }
        }

        // term := factor ((*|/|mod|implicit) factor)*
        private double ParseTerm()
        {
            double v = ParseFactor();
            while (true)
            {
                SkipWs();
                if (_pos >= _s.Length) return v;
                char c = _s[_pos];
                if (c == '*') { _pos++; v *= ParseFactor(); }
                else if (c == '/') { _pos++; double d = ParseFactor(); v /= d; }
                else if (c == '%')
                {
                    // Binary modulo only when followed by value; else postfix percent handled in ParseFactor.
                    if (NextIsValue()) { _pos++; double d = ParseFactor(); v %= d; }
                    else return v;
                }
                else if (c == 'x' || c == 'X')
                {
                    // 'x' as multiply only between values: "2x3", "2 x 3".
                    if (PrevIsValue() && NextIsValueAfter(1)) { _pos++; v *= ParseFactor(); }
                    else return v;
                }
                else if (c == '(' || char.IsDigit(c) || c == '.' || char.IsLetter(c))
                {
                    // Implicit multiplication: 2(3+4), 2pi, (2)(3)
                    v *= ParseFactor();
                }
                else return v;
            }
        }

        // factor := unary (^ factor)?   (right-assoc)
        private double ParseFactor()
        {
            double b = ParseUnary();
            SkipWs();
            if (_pos < _s.Length && _s[_pos] == '^')
            {
                _pos++;
                double e = ParseFactor();
                b = Math.Pow(b, e);
            }
            return b;
        }

        private double ParseUnary()
        {
            SkipWs();
            if (_pos < _s.Length && _s[_pos] == '+') { _pos++; return ParseUnary(); }
            if (_pos < _s.Length && _s[_pos] == '-') { _pos++; return -ParseUnary(); }
            double v = ParsePrimary();
            // Postfix percent: "50%" -> 0.5 (only when NOT binary modulo context)
            while (true)
            {
                int save = _pos;
                SkipWs();
                if (_pos < _s.Length && _s[_pos] == '%' && !NextIsValueAfter(1))
                    _pos++;
                else { _pos = save; break; }
                v /= 100.0;
            }
            return v;
        }

        private double ParsePrimary()
        {
            SkipWs();
            if (_pos >= _s.Length) throw new InvalidOperationException("end");
            char c = _s[_pos];

            if (c == '(')
            {
                _pos++;
                double v = ParseExpr();
                SkipWs();
                Expect(')');
                return v;
            }

            if (char.IsDigit(c) || c == '.')
                return ParseNumber();

            if (char.IsLetter(c))
            {
                string name = ParseName();
                var lower = name.ToLowerInvariant();
                if (lower == "pi") return Math.PI;
                if (lower == "e") return Math.E;
                if (lower == "tau") return Math.Tau;
                // Function call
                SkipWs();
                var args = new List<double>();
                if (_pos < _s.Length && _s[_pos] == '(')
                {
                    _pos++;
                    SkipWs();
                    if (_pos < _s.Length && _s[_pos] != ')')
                    {
                        args.Add(ParseExpr());
                        SkipWs();
                        while (_pos < _s.Length && _s[_pos] == ',')
                        {
                            _pos++;
                            args.Add(ParseExpr());
                            SkipWs();
                        }
                    }
                    Expect(')');
                }
                else
                {
                    // Allow "sqrt 16", "log 100" (single arg, no parens)
                    args.Add(ParseUnary());
                }
                return ApplyFunc(lower, args);
            }

            throw new InvalidOperationException($"unexpected '{c}'");
        }

        private double ParseNumber()
        {
            int start = _pos;
            bool dot = false;
            while (_pos < _s.Length)
            {
                char c = _s[_pos];
                if (char.IsDigit(c)) _pos++;
                else if (c == '.' && !dot) { dot = true; _pos++; }
                else if (c == ',' )
                {
                    // thousands separator only between digits: "1,000"
                    if (_pos > start && _pos + 1 < _s.Length &&
                        char.IsDigit(_s[_pos - 1]) && char.IsDigit(_s[_pos + 1]))
                        _pos++;
                    else break;
                }
                else break;
            }
            var raw = _s.Substring(start, _pos - start).Replace(",", "");
            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                throw new InvalidOperationException("number");
            return v;
        }

        private string ParseName()
        {
            int start = _pos;
            while (_pos < _s.Length && char.IsLetter(_s[_pos])) _pos++;
            return _s.Substring(start, _pos - start);
        }

        private static double ApplyFunc(string name, List<double> a)
        {
            double Deg(double d) => d * Math.PI / 180.0;
            double Rad(double r) => r * 180.0 / Math.PI;
            return name switch
            {
                "sqrt" when a.Count == 1 => a[0] < 0 ? throw new InvalidOperationException("sqrt") : Math.Sqrt(a[0]),
                "abs" when a.Count == 1 => Math.Abs(a[0]),
                "sin" when a.Count == 1 => Math.Sin(Deg(a[0])),
                "cos" when a.Count == 1 => Math.Cos(Deg(a[0])),
                "tan" when a.Count == 1 => Math.Tan(Deg(a[0])),
                "asin" when a.Count == 1 => Rad(Math.Asin(a[0])),
                "acos" when a.Count == 1 => Rad(Math.Acos(a[0])),
                "atan" when a.Count == 1 => Rad(Math.Atan(a[0])),
                "log" when a.Count == 1 => a[0] <= 0 ? throw new InvalidOperationException("log") : Math.Log10(a[0]),
                "ln" when a.Count == 1 => a[0] <= 0 ? throw new InvalidOperationException("ln") : Math.Log(a[0]),
                "exp" when a.Count == 1 => Math.Exp(a[0]),
                "floor" when a.Count == 1 => Math.Floor(a[0]),
                "ceil" when a.Count == 1 => Math.Ceiling(a[0]),
                "round" when a.Count == 1 => Math.Round(a[0]),
                "round" when a.Count == 2 => Math.Round(a[0], (int)a[1]),
                "pow" when a.Count == 2 => Math.Pow(a[0], a[1]),
                "max" when a.Count >= 1 => Max(a),
                "min" when a.Count >= 1 => Min(a),
                _ => throw new InvalidOperationException("func"),
            };
            static double Max(List<double> l) { double m = l[0]; foreach (var x in l) m = Math.Max(m, x); return m; }
            static double Min(List<double> l) { double m = l[0]; foreach (var x in l) m = Math.Min(m, x); return m; }
        }

        private void SkipWs()
        {
            while (_pos < _s.Length && (_s[_pos] == ' ' || _s[_pos] == '\t')) _pos++;
        }

        private bool Match(char c)
        {
            SkipWs();
            if (_pos < _s.Length && _s[_pos] == c) { _pos++; return true; }
            return false;
        }

        private void Expect(char c)
        {
            SkipWs();
            if (_pos >= _s.Length || _s[_pos] != c) throw new InvalidOperationException("paren");
            _pos++;
        }

        private bool NextIsValue()
        {
            int p = _pos + 1;
            while (p < _s.Length && (_s[p] == ' ' || _s[p] == '\t')) p++;
            return p < _s.Length && (char.IsDigit(_s[p]) || _s[p] == '.' || _s[p] == '(' || char.IsLetter(_s[p]) || _s[p] == '-' || _s[p] == '+');
        }

        private bool NextIsValueAfter(int off)
        {
            int p = _pos + off;
            while (p < _s.Length && (_s[p] == ' ' || _s[p] == '\t')) p++;
            return p < _s.Length && (char.IsDigit(_s[p]) || _s[p] == '.' || _s[p] == '(' || char.IsLetter(_s[p]));
        }

        private bool PrevIsValue()
        {
            int p = _pos - 1;
            while (p >= 0 && (_s[p] == ' ' || _s[p] == '\t')) p--;
            return p >= 0 && (char.IsDigit(_s[p]) || _s[p] == ')' || _s[p] == '%' || char.IsLetter(_s[p]));
        }
    }
}
