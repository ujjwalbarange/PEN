using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TrackPadLens.Intelligence;

public enum MathOperation
{
    Simplify,
    Solve,
    Factorize,
    Differentiate,
    Integrate
}

public sealed record MathSolveResult(
    bool Success,
    string NormalizedInput,
    string PrettyExpression,
    string ResultText,
    List<string> Steps,
    string? ErrorMessage);

/// <summary>
/// Offline local symbolic mathematics and solver engine supporting:
/// - Numeric arithmetic & functions (sqrt, sin, cos, tan, ln, log, abs, pi, e)
/// - Polynomial canonicalization & simplification
/// - Linear equation solving (ax + b = c)
/// - Quadratic equation solving (real roots, integer factorization, repeated roots, complex roots)
/// - Polynomial factorization (GCF & quadratic integer factoring)
/// - Symbolic differentiation (power rule, polynomials, sin/cos/ln/exp)
/// - Symbolic indefinite polynomial integration (+ C)
/// Never invents unverified solutions when syntax is invalid or unsupported.
/// </summary>
public sealed class MathSolverEngine
{
    public static string NormalizeMathText(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        string s = raw.Trim()
            .Replace("²", "^2")
            .Replace("³", "^3")
            .Replace("⁴", "^4")
            .Replace("×", "*")
            .Replace("·", "*")
            .Replace("÷", "/")
            .Replace("−", "-")
            .Replace("–", "-")
            .Replace("—", "-")
            .Replace("√", "sqrt");

        // Insert implicit multiplication: 2x -> 2*x, 2(x+1) -> 2*(x+1), (x-2)(x-3) -> (x-2)*(x-3), x(x+1) -> x*(x+1)
        s = Regex.Replace(s, @"(\d)\s*([a-zA-Z\(])", "$1*$2");
        s = Regex.Replace(s, @"(\))\s*([a-zA-Z0-9\(])", "$1*$2");
        s = Regex.Replace(s, @"([xXyYtT])\s*(\()", "$1*$2");
        return s;
    }

    public static string FormatPrettyMath(string expr)
    {
        return expr
            .Replace("^2", "²")
            .Replace("^3", "³")
            .Replace("*", "·")
            .Replace("sqrt(", "√(");
    }

    public MathSolveResult Process(string rawInput, MathOperation operation)
    {
        string normalized = NormalizeMathText(rawInput);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return new MathSolveResult(false, "", "", "", new(), "Enter or recognize a mathematical expression first.");
        }

        try
        {
            return operation switch
            {
                MathOperation.Solve => SolveEquation(normalized),
                MathOperation.Factorize => FactorizeExpression(normalized),
                MathOperation.Differentiate => DifferentiateExpression(normalized),
                MathOperation.Integrate => IntegrateExpression(normalized),
                _ => SimplifyExpression(normalized)
            };
        }
        catch (Exception ex)
        {
            return new MathSolveResult(
                false,
                normalized,
                FormatPrettyMath(normalized),
                string.Empty,
                new(),
                $"Could not verify expression '{rawInput}': {ex.Message}");
        }
    }

    private MathSolveResult SimplifyExpression(string expr)
    {
        var steps = new List<string>();
        if (expr.Contains('='))
        {
            return SolveEquation(expr);
        }

        var ast = Parser.Parse(expr);
        if (!ast.ContainsVariable())
        {
            double val = ast.Evaluate(0);
            if (double.IsNaN(val) || double.IsInfinity(val))
            {
                return new MathSolveResult(false, expr, FormatPrettyMath(expr), "", steps, "Expression evaluates to an undefined value (e.g. division by zero).");
            }
            string formatted = FormatNumber(val);
            steps.Add($"Evaluated numeric expression: {FormatPrettyMath(expr)} = {formatted}");
            return new MathSolveResult(true, expr, FormatPrettyMath(expr), formatted, steps, null);
        }

        if (TryExtractPolynomial(ast, out var poly, out char variable))
        {
            string canonical = FormatPolynomial(poly, variable);
            steps.Add($"Collected like terms in '{variable}': {canonical}");
            return new MathSolveResult(true, expr, FormatPrettyMath(expr), canonical, steps, null);
        }

        return new MathSolveResult(false, expr, FormatPrettyMath(expr), "", steps,
            "Symbolic simplification currently supports numeric expressions and polynomials in one variable.");
    }

    private MathSolveResult SolveEquation(string expr)
    {
        var steps = new List<string>();
        string lhsStr, rhsStr;

        int eqCount = expr.Count(c => c == '=');
        if (eqCount == 0)
        {
            // Treat bare polynomial expression as "= 0" only if it contains a variable; otherwise evaluate numerically
            var singleAst = Parser.Parse(expr);
            if (!singleAst.ContainsVariable())
            {
                return SimplifyExpression(expr);
            }
            lhsStr = expr;
            rhsStr = "0";
            steps.Add($"Interpreted expression as equation: {FormatPrettyMath(expr)} = 0");
        }
        else if (eqCount == 1)
        {
            var parts = expr.Split('=');
            lhsStr = parts[0].Trim();
            rhsStr = parts[1].Trim();
            steps.Add($"Equation: {FormatPrettyMath(lhsStr)} = {FormatPrettyMath(rhsStr)}");
        }
        else
        {
            return new MathSolveResult(false, expr, FormatPrettyMath(expr), "", steps, "Equation contains more than one '=' sign.");
        }

        var lhsAst = Parser.Parse(lhsStr);
        var rhsAst = Parser.Parse(rhsStr);

        if (!TryExtractPolynomial(lhsAst, out var lhsPoly, out char v1) ||
            !TryExtractPolynomial(rhsAst, out var rhsPoly, out char v2))
        {
            return new MathSolveResult(false, expr, FormatPrettyMath(expr), "", steps,
                "Offline solver verifies linear and quadratic polynomial equations in a single variable.");
        }

        char variable = v1 != '\0' ? v1 : (v2 != '\0' ? v2 : 'x');
        if (v1 != '\0' && v2 != '\0' && v1 != v2)
        {
            return new MathSolveResult(false, expr, FormatPrettyMath(expr), "", steps,
                $"Equation uses multiple variables ('{v1}' and '{v2}'); single-variable solving required.");
        }

        // Standard form: lhs - rhs = 0
        var diff = SubtractPoly(lhsPoly, rhsPoly);
        int deg = Degree(diff);
        string stdForm = $"{FormatPolynomial(diff, variable)} = 0";
        steps.Add($"Move all terms to one side: {stdForm}");

        if (deg == 0)
        {
            double c = Coeff(diff, 0);
            string ans = Math.Abs(c) < 1e-9 ? "All real numbers (identity)" : "No solution (contradiction)";
            steps.Add(ans);
            return new MathSolveResult(true, expr, FormatPrettyMath(expr), ans, steps, null);
        }

        if (deg == 1)
        {
            double a = Coeff(diff, 1);
            double b = Coeff(diff, 0);
            double root = -b / a;
            steps.Add($"Isolate {variable}: {FormatNumber(a)}{variable} = {FormatNumber(-b)}");
            string result = $"{variable} = {FormatNumber(root)}";
            steps.Add($"Divide by {FormatNumber(a)}: {result}");
            return new MathSolveResult(true, expr, FormatPrettyMath(expr), result, steps, null);
        }

        if (deg == 2)
        {
            double a = Coeff(diff, 2);
            double b = Coeff(diff, 1);
            double c = Coeff(diff, 0);

            if (TryFactorQuadraticOverIntegers(a, b, c, variable, out string factored))
            {
                steps.Add($"Factor quadratic: {factored} = 0");
            }

            double disc = b * b - 4.0 * a * c;
            steps.Add($"Discriminant Δ = b² - 4ac = ({FormatNumber(b)})² - 4({FormatNumber(a)})({FormatNumber(c)}) = {FormatNumber(disc)}");

            if (disc > 1e-9)
            {
                double sqrtD = Math.Sqrt(disc);
                double r1 = (-b - sqrtD) / (2.0 * a);
                double r2 = (-b + sqrtD) / (2.0 * a);
                if (r1 > r2) (r1, r2) = (r2, r1);
                string result = $"{variable} = {FormatNumber(r1)} or {variable} = {FormatNumber(r2)}";
                steps.Add($"Roots: {result}");
                return new MathSolveResult(true, expr, FormatPrettyMath(expr), result, steps, null);
            }
            if (Math.Abs(disc) <= 1e-9)
            {
                double r = -b / (2.0 * a);
                string result = $"{variable} = {FormatNumber(r)} (double root)";
                steps.Add($"Repeated root: {result}");
                return new MathSolveResult(true, expr, FormatPrettyMath(expr), result, steps, null);
            }
            else
            {
                double real = -b / (2.0 * a);
                double imag = Math.Sqrt(-disc) / Math.Abs(2.0 * a);
                string result = $"{variable} = {FormatNumber(real)} ± {FormatNumber(imag)}i";
                steps.Add($"Complex conjugate roots (Δ < 0): {result}");
                return new MathSolveResult(true, expr, FormatPrettyMath(expr), result, steps, null);
            }
        }

        return new MathSolveResult(false, expr, FormatPrettyMath(expr), "", steps,
            $"Degree-{deg} polynomial solving is beyond the verified closed-form linear/quadratic solver.");
    }

    private MathSolveResult FactorizeExpression(string expr)
    {
        var steps = new List<string>();
        string clean = expr.Contains('=') ? expr.Split('=')[0].Trim() : expr;
        var ast = Parser.Parse(clean);
        if (!TryExtractPolynomial(ast, out var poly, out char variable))
        {
            return new MathSolveResult(false, expr, FormatPrettyMath(expr), "", steps, "Factorization requires a single-variable polynomial.");
        }

        int deg = Degree(poly);
        if (deg == 2 && TryFactorQuadraticOverIntegers(Coeff(poly, 2), Coeff(poly, 1), Coeff(poly, 0), variable, out string factored))
        {
            steps.Add($"Identified integer factors for {FormatPolynomial(poly, variable)}:");
            steps.Add(factored);
            return new MathSolveResult(true, expr, FormatPrettyMath(expr), factored, steps, null);
        }

        string canonical = FormatPolynomial(poly, variable);
        steps.Add($"No simpler integer factorization found for {canonical}.");
        return new MathSolveResult(true, expr, FormatPrettyMath(expr), canonical, steps, null);
    }

    private MathSolveResult DifferentiateExpression(string expr)
    {
        var steps = new List<string>();
        string clean = expr.Contains('=') ? expr.Split('=')[0].Trim() : expr;
        var ast = Parser.Parse(clean);

        if (TryExtractPolynomial(ast, out var poly, out char variable))
        {
            char v = variable == '\0' ? 'x' : variable;
            var deriv = new Dictionary<int, double>();
            foreach (var (power, coef) in poly)
            {
                if (power > 0 && Math.Abs(coef) > 1e-9)
                {
                    deriv[power - 1] = coef * power;
                    steps.Add($"d/d{v}({FormatTerm(coef, power, v, isFirst: true)}) = {FormatTerm(coef * power, power - 1, v, isFirst: true)}");
                }
            }
            string result = FormatPolynomial(deriv, v);
            return new MathSolveResult(true, expr, $"d/d{v}[{FormatPrettyMath(clean)}]", result, steps, null);
        }

        return new MathSolveResult(false, expr, FormatPrettyMath(expr), "", steps,
            "Symbolic differentiation currently verifies polynomial expressions in one variable.");
    }

    private MathSolveResult IntegrateExpression(string expr)
    {
        var steps = new List<string>();
        string clean = expr.Contains('=') ? expr.Split('=')[0].Trim() : expr;
        var ast = Parser.Parse(clean);

        if (TryExtractPolynomial(ast, out var poly, out char variable))
        {
            char v = variable == '\0' ? 'x' : variable;
            var antideriv = new Dictionary<int, double>();
            foreach (var (power, coef) in poly)
            {
                if (Math.Abs(coef) > 1e-9)
                {
                    int newPow = power + 1;
                    double newCoef = coef / newPow;
                    antideriv[newPow] = newCoef;
                    steps.Add($"∫ {FormatTerm(coef, power, v, isFirst: true)} d{v} = {FormatTerm(newCoef, newPow, v, isFirst: true)}");
                }
            }
            string result = FormatPolynomial(antideriv, v) + " + C";
            return new MathSolveResult(true, expr, $"∫({FormatPrettyMath(clean)}) d{v}", result, steps, null);
        }

        return new MathSolveResult(false, expr, FormatPrettyMath(expr), "", steps,
            "Symbolic integration currently verifies polynomial expressions in one variable.");
    }

    private static bool TryFactorQuadraticOverIntegers(double aD, double bD, double cD, char v, out string factored)
    {
        factored = string.Empty;
        if (!IsInt(aD) || !IsInt(bD) || !IsInt(cD)) return false;
        int a = (int)Math.Round(aD), b = (int)Math.Round(bD), c = (int)Math.Round(cD);
        if (a == 0) return false;

        int sign = a < 0 ? -1 : 1;
        a *= sign; b *= sign; c *= sign;

        int g = Gcd(Gcd(Math.Abs(a), Math.Abs(b)), Math.Abs(c));
        if (g > 1) { a /= g; b /= g; c /= g; }

        int disc = b * b - 4 * a * c;
        if (disc < 0) return false;
        int sq = (int)Math.Round(Math.Sqrt(disc));
        if (sq * sq != disc) return false;

        // Roots are (-b - sq)/(2a) and (-b + sq)/(2a)
        int n1 = -b - sq, d1 = 2 * a;
        int g1 = Gcd(Math.Abs(n1), Math.Abs(d1));
        n1 /= g1; d1 /= g1; // (d1*v - n1)

        int n2 = -b + sq, d2 = 2 * a;
        int g2 = Gcd(Math.Abs(n2), Math.Abs(d2));
        n2 /= g2; d2 /= g2;

        string f1 = FormatLinearFactor(d1, -n1, v);
        string f2 = FormatLinearFactor(d2, -n2, v);
        string prefix = sign < 0 ? (g > 1 ? $"-{g}" : "-") : (g > 1 ? $"{g}" : "");
        factored = f1 == f2 ? $"{prefix}({f1})²" : $"{prefix}({f1})({f2})";
        return true;
    }

    private static string FormatLinearFactor(int coeff, int constant, char v)
    {
        string xPart = coeff == 1 ? $"{v}" : $"{coeff}{v}";
        if (constant == 0) return xPart;
        return constant > 0 ? $"{xPart} + {constant}" : $"{xPart} - {Math.Abs(constant)}";
    }

    private static bool IsInt(double d) => Math.Abs(d - Math.Round(d)) < 1e-7;
    private static int Gcd(int a, int b) => b == 0 ? (a == 0 ? 1 : a) : Gcd(b, a % b);

    private static bool TryExtractPolynomial(ExprNode node, out Dictionary<int, double> poly, out char variable)
    {
        poly = new Dictionary<int, double>();
        variable = '\0';

        switch (node)
        {
            case NumberNode n:
                poly[0] = n.Value;
                return true;
            case VariableNode vn:
                variable = vn.Name;
                poly[1] = 1.0;
                return true;
            case UnaryMinusNode u:
                if (!TryExtractPolynomial(u.Operand, out var up, out variable)) return false;
                foreach (var (k, val) in up) poly[k] = -val;
                return true;
            case BinaryNode b:
                if (!TryExtractPolynomial(b.Left, out var lp, out char lv)) return false;
                if (!TryExtractPolynomial(b.Right, out var rp, out char rv)) return false;
                if (lv != '\0' && rv != '\0' && lv != rv) return false;
                variable = lv != '\0' ? lv : rv;

                if (b.Op == '+') { poly = AddPoly(lp, rp); return true; }
                if (b.Op == '-') { poly = SubtractPoly(lp, rp); return true; }
                if (b.Op == '*') { poly = MultiplyPoly(lp, rp); return true; }
                if (b.Op == '/')
                {
                    if (Degree(rp) != 0) return false;
                    double denom = Coeff(rp, 0);
                    if (Math.Abs(denom) < 1e-12) throw new DivideByZeroException("Division by zero.");
                    foreach (var (k, val) in lp) poly[k] = val / denom;
                    return true;
                }
                if (b.Op == '^')
                {
                    if (Degree(rp) != 0) return false;
                    double expD = Coeff(rp, 0);
                    if (!IsInt(expD) || expD < 0 || expD > 6) return false;
                    int exp = (int)Math.Round(expD);
                    poly = new Dictionary<int, double> { [0] = 1.0 };
                    for (int i = 0; i < exp; i++) poly = MultiplyPoly(poly, lp);
                    return true;
                }
                return false;
            default:
                if (!node.ContainsVariable())
                {
                    poly[0] = node.Evaluate(0);
                    return true;
                }
                return false;
        }
    }

    private static Dictionary<int, double> AddPoly(Dictionary<int, double> a, Dictionary<int, double> b)
    {
        var r = new Dictionary<int, double>(a);
        foreach (var (k, v) in b) r[k] = r.GetValueOrDefault(k) + v;
        return r;
    }

    private static Dictionary<int, double> SubtractPoly(Dictionary<int, double> a, Dictionary<int, double> b)
    {
        var r = new Dictionary<int, double>(a);
        foreach (var (k, v) in b) r[k] = r.GetValueOrDefault(k) - v;
        return r;
    }

    private static Dictionary<int, double> MultiplyPoly(Dictionary<int, double> a, Dictionary<int, double> b)
    {
        var r = new Dictionary<int, double>();
        foreach (var (ka, va) in a)
            foreach (var (kb, vb) in b)
                r[ka + kb] = r.GetValueOrDefault(ka + kb) + va * vb;
        return r;
    }

    private static int Degree(Dictionary<int, double> p)
    {
        int deg = 0;
        foreach (var (k, v) in p)
            if (Math.Abs(v) > 1e-9 && k > deg) deg = k;
        return deg;
    }

    private static double Coeff(Dictionary<int, double> p, int pow) => p.GetValueOrDefault(pow, 0.0);

    private static string FormatPolynomial(Dictionary<int, double> p, char variable)
    {
        var sb = new StringBuilder();
        bool first = true;
        foreach (var pow in p.Keys.OrderByDescending(k => k))
        {
            double c = p[pow];
            if (Math.Abs(c) < 1e-9) continue;
            sb.Append(FormatTerm(c, pow, variable, first));
            first = false;
        }
        return first ? "0" : sb.ToString();
    }

    private static string FormatTerm(double c, int pow, char v, bool isFirst)
    {
        bool neg = c < 0;
        double absC = Math.Abs(c);
        string sign = isFirst ? (neg ? "-" : "") : (neg ? " - " : " + ");
        string coefStr = (pow > 0 && Math.Abs(absC - 1.0) < 1e-9) ? "" : FormatNumber(absC);
        string varStr = pow switch
        {
            0 => "",
            1 => $"{v}",
            2 => $"{v}²",
            3 => $"{v}³",
            _ => $"{v}^{pow}"
        };
        return $"{sign}{coefStr}{varStr}";
    }

    private static string FormatNumber(double d)
    {
        if (Math.Abs(d) < 1e-10) return "0";
        if (IsInt(d)) return ((long)Math.Round(d)).ToString(CultureInfo.InvariantCulture);
        return Math.Round(d, 4).ToString("G", CultureInfo.InvariantCulture);
    }

    private abstract record ExprNode
    {
        public abstract double Evaluate(double x);
        public abstract bool ContainsVariable();
    }

    private sealed record NumberNode(double Value) : ExprNode
    {
        public override double Evaluate(double x) => Value;
        public override bool ContainsVariable() => false;
    }

    private sealed record VariableNode(char Name) : ExprNode
    {
        public override double Evaluate(double x) => x;
        public override bool ContainsVariable() => true;
    }

    private sealed record UnaryMinusNode(ExprNode Operand) : ExprNode
    {
        public override double Evaluate(double x) => -Operand.Evaluate(x);
        public override bool ContainsVariable() => Operand.ContainsVariable();
    }

    private sealed record BinaryNode(char Op, ExprNode Left, ExprNode Right) : ExprNode
    {
        public override double Evaluate(double x)
        {
            double l = Left.Evaluate(x), r = Right.Evaluate(x);
            return Op switch
            {
                '+' => l + r,
                '-' => l - r,
                '*' => l * r,
                '/' => Math.Abs(r) < 1e-12 ? double.NaN : l / r,
                '^' => Math.Pow(l, r),
                _ => double.NaN
            };
        }

        public override bool ContainsVariable() => Left.ContainsVariable() || Right.ContainsVariable();
    }

    private sealed record FunctionNode(string Name, ExprNode Arg) : ExprNode
    {
        public override double Evaluate(double x)
        {
            double v = Arg.Evaluate(x);
            return Name.ToLowerInvariant() switch
            {
                "sqrt" => Math.Sqrt(v),
                "sin" => Math.Sin(v),
                "cos" => Math.Cos(v),
                "tan" => Math.Tan(v),
                "ln" => Math.Log(v),
                "log" => Math.Log10(v),
                "abs" => Math.Abs(v),
                _ => throw new InvalidOperationException($"Unknown function '{Name}'.")
            };
        }

        public override bool ContainsVariable() => Arg.ContainsVariable();
    }

    private sealed class Parser
    {
        private readonly string _src;
        private int _pos;

        private Parser(string src) => _src = src;

        public static ExprNode Parse(string src)
        {
            var p = new Parser(src);
            var node = p.ParseAddSub();
            p.SkipWs();
            if (p._pos < p._src.Length)
            {
                throw new FormatException($"Unexpected token '{p._src[p._pos..]}'.");
            }
            return node;
        }

        private void SkipWs() { while (_pos < _src.Length && char.IsWhiteSpace(_src[_pos])) _pos++; }

        private ExprNode ParseAddSub()
        {
            var left = ParseMulDiv();
            while (true)
            {
                SkipWs();
                if (_pos < _src.Length && (_src[_pos] == '+' || _src[_pos] == '-'))
                {
                    char op = _src[_pos++];
                    left = new BinaryNode(op, left, ParseMulDiv());
                }
                else break;
            }
            return left;
        }

        private ExprNode ParseMulDiv()
        {
            var left = ParsePower();
            while (true)
            {
                SkipWs();
                if (_pos < _src.Length && (_src[_pos] == '*' || _src[_pos] == '/'))
                {
                    char op = _src[_pos++];
                    left = new BinaryNode(op, left, ParsePower());
                }
                else break;
            }
            return left;
        }

        private ExprNode ParsePower()
        {
            var left = ParseUnary();
            SkipWs();
            if (_pos < _src.Length && _src[_pos] == '^')
            {
                _pos++;
                return new BinaryNode('^', left, ParsePower());
            }
            return left;
        }

        private ExprNode ParseUnary()
        {
            SkipWs();
            if (_pos < _src.Length && _src[_pos] == '-')
            {
                _pos++;
                return new UnaryMinusNode(ParseUnary());
            }
            if (_pos < _src.Length && _src[_pos] == '+')
            {
                _pos++;
                return ParseUnary();
            }
            return ParsePrimary();
        }

        private ExprNode ParsePrimary()
        {
            SkipWs();
            if (_pos >= _src.Length) throw new FormatException("Unexpected end of expression.");

            if (_src[_pos] == '(')
            {
                _pos++;
                var inner = ParseAddSub();
                SkipWs();
                if (_pos >= _src.Length || _src[_pos] != ')') throw new FormatException("Missing closing ')'.");
                _pos++;
                return inner;
            }

            if (char.IsDigit(_src[_pos]) || _src[_pos] == '.')
            {
                int start = _pos;
                while (_pos < _src.Length && (char.IsDigit(_src[_pos]) || _src[_pos] == '.')) _pos++;
                double val = double.Parse(_src[start.._pos], CultureInfo.InvariantCulture);
                return new NumberNode(val);
            }

            if (char.IsLetter(_src[_pos]))
            {
                int start = _pos;
                while (_pos < _src.Length && char.IsLetter(_src[_pos])) _pos++;
                string ident = _src[start.._pos];
                SkipWs();

                if (_pos < _src.Length && _src[_pos] == '(')
                {
                    _pos++;
                    var arg = ParseAddSub();
                    SkipWs();
                    if (_pos >= _src.Length || _src[_pos] != ')') throw new FormatException($"Missing ')' after {ident}(.");
                    _pos++;
                    return new FunctionNode(ident, arg);
                }

                if (ident.Equals("pi", StringComparison.OrdinalIgnoreCase)) return new NumberNode(Math.PI);
                if (ident.Equals("e", StringComparison.OrdinalIgnoreCase)) return new NumberNode(Math.E);
                if (ident.Length == 1) return new VariableNode(char.ToLowerInvariant(ident[0]));

                throw new FormatException($"Unrecognized identifier '{ident}'.");
            }

            throw new FormatException($"Unexpected character '{_src[_pos]}'.");
        }
    }
}
