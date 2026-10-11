using System.Globalization;
using System.Text;

namespace DocRedock.Formats.OpenXml.Xlsx;

internal enum FormulaValueKind
{
    Blank,
    Number,
    Text,
    Boolean,
    Error,
    Unknown,
}

internal readonly record struct FormulaValue(FormulaValueKind Kind, string Text = "", double Number = 0);

internal static partial class XlsxConditionalFormula
{
    internal const int MaxExpressionLength = 8192;
    internal const int MaxNestingDepth = 64;
    internal const int MaxTokens = 4096;
    internal const int MaxArguments = 255;
    internal const int MaxRangeItems = 8192;
    internal const int MaxWorkUnits = 250000;

    internal static FormulaValue Evaluate(string expression, Func<string, IReadOnlyList<FormulaValue>?> resolve, int row, int column, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(resolve);
        if (string.IsNullOrWhiteSpace(expression))
            return Evaluator.Unknown;
        if (expression.Length > MaxExpressionLength)
            return Evaluator.Unknown;

        Evaluator? evaluator = null;
        try
        {
            evaluator = new Evaluator(expression, resolve, row, column, token);
            evaluator.Tokenize();
            Node? root = evaluator.ParseExpression();
            if (!evaluator.AtEnd || root is null)
                return Evaluator.Unknown;
            return Evaluator.ToFormula(evaluator.Evaluate(root, 0));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Evaluator.Unknown;
        }
        finally
        {
            evaluator?.Dispose();
        }
    }
}

internal static partial class XlsxConditionalFormula
{
    private enum Tok
    {
        Number,
        Text,
        Name,
        Op,
        LParen,
        RParen,
        Comma,
        Percent,
        Error,
        Invalid,
    }

    private readonly struct Token
    {
        internal readonly Tok Kind;
        internal readonly string Text;
        internal readonly double Number;
        internal readonly int Pos;

        internal Token(Tok kind, string text, double number, int pos)
        {
            Kind = kind;
            Text = text;
            Number = number;
            Pos = pos;
        }
    }

    private sealed class FormulaLimitException : Exception
    {
    }

    private sealed class CancelledException : OperationCanceledException
    {
    }

    private static readonly string[] ErrorLiterals =
    [
        "#NULL!", "#DIV/0!", "#VALUE!", "#REF!", "#NAME?", "#NUM!", "#N/A", "#GETTING_DATA",
    ];

    private static string? MatchError(string s, int i)
    {
        if (s[i] != '#')
            return null;
        for (int e = 0; e < ErrorLiterals.Length; e++)
        {
            string lit = ErrorLiterals[e];
            if (i + lit.Length <= s.Length && string.CompareOrdinal(s, i, lit, 0, lit.Length) == 0)
                return lit;
        }

        return null;
    }

    private static bool IsNameStart(char c) => c == '$' || char.IsLetter(c) || c == '_' || c == '\\' || c == '.' || c > 127;

    private static bool IsNameChar(char c) => c == '$' || char.IsLetterOrDigit(c) || c == '_' || c == '.' || c == '?' || c > 127;

    private static void Tokenize(Evaluator ev)
    {
        string s = ev.Src;
        int n = s.Length;
        List<Token> tokens = new(64);

        void Add(Tok kind, string text, double number, int pos) => tokens.Add(new Token(kind, text, number, pos));

        int i = 0;
        while (i < n)
        {
            ev.Tick();
            char c = s[i];
            if (c == ' ' || c == '\t' || c == '\r' || c == '\n' || c == '\f' || c == '\v')
            {
                i++;
                continue;
            }

            if (c == '"')
            {
                int start = i;
                i++;
                StringBuilder sb = new();
                bool closed = false;
                while (i < n)
                {
                    ev.Tick();
                    if (s[i] == '"')
                    {
                        if (i + 1 < n && s[i + 1] == '"')
                        {
                            sb.Append('"');
                            i += 2;
                            continue;
                        }

                        i++;
                        closed = true;
                        break;
                    }

                    sb.Append(s[i]);
                    i++;
                }

                if (!closed)
                {
                    Add(Tok.Invalid, s.Substring(start), 0, start);
                    break;
                }

                Add(Tok.Text, sb.ToString(), 0, start);
                continue;
            }

            if (c == '\'')
            {
                int start = i;
                i++;
                StringBuilder sb = new();
                bool closed = false;
                while (i < n)
                {
                    ev.Tick();
                    if (s[i] == '\'')
                    {
                        if (i + 1 < n && s[i + 1] == '\'')
                        {
                            sb.Append('\'');
                            i += 2;
                            continue;
                        }

                        i++;
                        closed = true;
                        break;
                    }

                    sb.Append(s[i]);
                    i++;
                }

                if (!closed || i >= n || s[i] != '!')
                {
                    Add(Tok.Invalid, s.Substring(start), 0, start);
                    break;
                }

                i++;
                int bodyStart = i;
                while (i < n && (char.IsLetterOrDigit(s[i]) || s[i] == '$' || s[i] == ':' || s[i] == '.' || s[i] == '_'))
                    i++;
                if (bodyStart == i)
                {
                    Add(Tok.Invalid, s.Substring(start), 0, start);
                    break;
                }

                string raw = s.Substring(start, i - start);
                string normalized = "'" + sb.ToString().Replace("'", "''", StringComparison.Ordinal) + "'!" + s.Substring(bodyStart, i - bodyStart);
                Add(Tok.Name, normalized == raw ? raw : normalized, 0, start);
                continue;
            }

            if (c == '#')
            {
                string? lit = MatchError(s, i);
                if (lit is null)
                {
                    Add(Tok.Invalid, s.Substring(i), 0, i);
                    break;
                }

                Add(Tok.Error, lit, 0, i);
                i += lit.Length;
                continue;
            }

            if (char.IsDigit(c) || (c == '.' && i + 1 < n && char.IsDigit(s[i + 1])))
            {
                int start = i;
                while (i < n && char.IsDigit(s[i]))
                    i++;
                if (i < n && s[i] == '.')
                {
                    i++;
                    while (i < n && char.IsDigit(s[i]))
                        i++;
                }

                if (i < n && (s[i] == 'e' || s[i] == 'E'))
                {
                    int save = i;
                    i++;
                    if (i < n && (s[i] == '+' || s[i] == '-'))
                        i++;
                    if (i < n && char.IsDigit(s[i]))
                    {
                        while (i < n && char.IsDigit(s[i]))
                            i++;
                    }
                    else
                    {
                        i = save;
                    }
                }

                string raw = s.Substring(start, i - start);
                if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) || double.IsNaN(d) || double.IsInfinity(d))
                {
                    Add(Tok.Invalid, raw, 0, start);
                    break;
                }

                Add(Tok.Number, raw, d, start);
                continue;
            }

            if (IsNameStart(c))
            {
                int start = i;
                while (i < n && (IsNameChar(s[i]) || s[i] == '!' || s[i] == ':' || s[i] == '$'))
                {
                    if (s[i] == '!')
                    {
                        int j = i + 1;
                        if (j < n && s[j] == '\'')
                            break;
                    }

                    i++;
                }

                string raw = s.Substring(start, i - start);
                Add(Tok.Name, raw, 0, start);
                continue;
            }

            if (c == '(')
            {
                Add(Tok.LParen, "(", 0, i);
                i++;
                continue;
            }

            if (c == ')')
            {
                Add(Tok.RParen, ")", 0, i);
                i++;
                continue;
            }

            if (c == ',' || c == ';')
            {
                Add(Tok.Comma, ",", 0, i);
                i++;
                continue;
            }

            if (c == '%')
            {
                Add(Tok.Percent, "%", 0, i);
                i++;
                continue;
            }

            if (c == '<' || c == '>' || c == '=' || c == '&' || c == '+' || c == '-' || c == '*' || c == '/' || c == '^')
            {
                if ((c == '<' || c == '>') && i + 1 < n && s[i + 1] == '=')
                {
                    Add(Tok.Op, s.Substring(i, 2), 0, i);
                    i += 2;
                    continue;
                }

                if (c == '<' && i + 1 < n && s[i + 1] == '>')
                {
                    Add(Tok.Op, "<>", 0, i);
                    i += 2;
                    continue;
                }

                Add(Tok.Op, c.ToString(), 0, i);
                i++;
                continue;
            }

            Add(Tok.Invalid, c.ToString(), 0, i);
            break;
        }

        ev.Tokens = tokens;
    }
}

internal static partial class XlsxConditionalFormula
{
    private abstract class Node
    {
    }

    private sealed class LiteralNode(FormulaValue value) : Node
    {
        internal readonly FormulaValue Value = value;
    }

    private sealed class RefNode(string raw) : Node
    {
        internal readonly string Raw = raw;
    }

    private sealed class UnaryNode(string op, Node child) : Node
    {
        internal readonly string Op = op;
        internal readonly Node Child = child;
    }

    private sealed class BinaryNode(string op, Node left, Node right) : Node
    {
        internal readonly string Op = op;
        internal readonly Node Left = left;
        internal readonly Node Right = right;
    }

    private sealed class CallNode(string name, List<Node> args) : Node
    {
        internal readonly string Name = name;
        internal readonly List<Node> Args = args;
    }

}

internal static partial class XlsxConditionalFormula
{
    private readonly struct Value
    {
        internal readonly FormulaValue Scalar;
        internal readonly IReadOnlyList<FormulaValue>? Range;

        private Value(FormulaValue scalar, IReadOnlyList<FormulaValue>? range)
        {
            Scalar = scalar;
            Range = range;
        }

        internal bool IsRange => Range is not null;
        internal FormulaValueKind Kind => Scalar.Kind;

        internal static Value Of(FormulaValue v) => new(v, null);

        internal static Value OfRange(IReadOnlyList<FormulaValue> items) => new(default, items);
    }

    private sealed partial class Evaluator : IDisposable
    {
        internal static readonly FormulaValue Unknown = new(FormulaValueKind.Unknown);
        private static readonly Value UnknownValue = Value.Of(Unknown);
        internal static readonly FormulaValue Blank = new(FormulaValueKind.Blank);
        internal static readonly FormulaValue True = new(FormulaValueKind.Boolean, "TRUE", 1);
        internal static readonly FormulaValue False = new(FormulaValueKind.Boolean, "FALSE", 0);

        private static readonly FormulaValue Div0 = new(FormulaValueKind.Error, "#DIV/0!");
        private static readonly FormulaValue ValueErr = new(FormulaValueKind.Error, "#VALUE!");
        private static readonly FormulaValue NumErr = new(FormulaValueKind.Error, "#NUM!");
        private static readonly FormulaValue NaErr = new(FormulaValueKind.Error, "#N/A");

        private readonly string _src;
        private readonly Func<string, IReadOnlyList<FormulaValue>?> _resolve;
        private readonly int _row;
        private readonly int _column;
        private readonly CancellationToken _token;
        private long _work;
        private int _depth;

        internal string Src => _src;
        internal List<Token> Tokens = new();
        internal int Pos;
        internal Node? Root;

        internal Evaluator(string src, Func<string, IReadOnlyList<FormulaValue>?> resolve, int row, int column, CancellationToken token)
        {
            _src = src.Trim();
            if (_src.StartsWith('=')) _src = _src[1..];
            _resolve = resolve;
            _row = row;
            _column = column;
            _token = token;
        }

        public void Dispose()
        {
            Tokens = new List<Token>();
            Root = null;
        }

        internal void Check()
        {
            if (_token.IsCancellationRequested)
                _token.ThrowIfCancellationRequested();
            if (++_work > MaxWorkUnits)
                throw new FormulaLimitException();
        }

        internal void Tick() => Check();

        private void Enter()
        {
            if (++_depth > MaxNestingDepth)
                throw new FormulaLimitException();
        }

        private void Leave() => _depth--;

        internal void Tokenize()
        {
            XlsxConditionalFormula.Tokenize(this);
            if (Tokens.Count > MaxTokens)
                throw new FormulaLimitException();
        }

        private Token Cur => Pos < Tokens.Count ? Tokens[Pos] : new Token(Tok.Invalid, string.Empty, 0, -1);

        private bool IsOp(string op) => Cur.Kind == Tok.Op && Cur.Text == op;

        internal bool AtEnd => Pos >= Tokens.Count;

        private static Node Literal(FormulaValue v) => new LiteralNode(v);

        internal Node? ParseExpression()
        {
            Node? left = ParseComparison();
            return AtEnd ? left : null;
        }

        private Node ParseComparison()
        {
            Node left = ParseConcat();
            while (IsOp("=") || IsOp("<>") || IsOp("<") || IsOp(">") || IsOp("<=") || IsOp(">="))
            {
                string op = Cur.Text;
                Pos++;
                Node right = ParseConcat();
                left = new BinaryNode(op, left, right);
            }

            return left;
        }

        private Node ParseConcat()
        {
            Node left = ParseAdditive();
            while (IsOp("&"))
            {
                Pos++;
                left = new BinaryNode("&", left, ParseAdditive());
            }

            return left;
        }

        private Node ParseAdditive()
        {
            Node left = ParseMultiplicative();
            while (IsOp("+") || IsOp("-"))
            {
                string op = Cur.Text;
                Pos++;
                left = new BinaryNode(op, left, ParseMultiplicative());
            }

            return left;
        }

        private Node ParseMultiplicative()
        {
            Node left = ParseUnary();
            while (IsOp("*") || IsOp("/"))
            {
                string op = Cur.Text;
                Pos++;
                left = new BinaryNode(op, left, ParseUnary());
            }

            return left;
        }

        private Node ParseUnary()
        {
            Node left;
            if (IsOp("+") || IsOp("-"))
            {
                string op = Cur.Text;
                Pos++;
                Enter();
                Node operand = ParseUnaryOperand();
                Leave();
                left = new UnaryNode(op, operand);
            }
            else
            {
                left = ParseUnaryOperand();
            }

            while (IsOp("^"))
            {
                Pos++;
                Enter();
                Node right = ParseExponentOperand();
                Leave();
                left = new BinaryNode("^", left, right);
            }

            return left;
        }

        // Excel binds unary minus and % tighter than ^, and ^ is left-associative.
        private Node ParseUnaryOperand()
        {
            Node node = ParsePrimary();
            while (Cur.Kind == Tok.Percent)
            {
                Pos++;
                node = new UnaryNode("%", node);
            }

            return node;
        }

        private Node ParseExponentOperand()
        {
            if (IsOp("+") || IsOp("-"))
            {
                string op = Cur.Text;
                Pos++;
                Enter();
                Node node = new UnaryNode(op, ParseUnaryOperand());
                Leave();
                return node;
            }

            return ParseUnaryOperand();
        }

        private Node ParsePrimary()
        {
            Token t = Cur;
            switch (t.Kind)
            {
                case Tok.Number:
                    Pos++;
                    return Literal(new FormulaValue(FormulaValueKind.Number, t.Text, t.Number));
                case Tok.Text:
                    Pos++;
                    return Literal(new FormulaValue(FormulaValueKind.Text, t.Text, 0));
                case Tok.Error:
                    Pos++;
                    return Literal(new FormulaValue(FormulaValueKind.Error, t.Text, 0));
                case Tok.LParen:
                    Pos++;
                    Enter();
                    Node? inner = ParseComparison();
                    if (Cur.Kind != Tok.RParen)
                        throw new FormatException();
                    Pos++;
                    Leave();
                    return inner;
                case Tok.Name:
                    return ParseName(t);
                default:
                    throw new FormatException();
            }
        }

        private Node ParseName(Token t)
        {
            Pos++;
            if (Cur.Kind == Tok.LParen)
            {
                Pos++;
                List<Node> args = new();
                Enter();
                if (Cur.Kind != Tok.RParen)
                {
                    while (true)
                    {
                        Check();
                        args.Add(ParseComparison());
                        if (args.Count > MaxArguments)
                            throw new FormulaLimitException();
                        if (Cur.Kind == Tok.Comma)
                        {
                            Pos++;
                            continue;
                        }

                        break;
                    }
                }

                if (Cur.Kind != Tok.RParen)
                    throw new FormatException();
                Pos++;
                Leave();
                return new CallNode(t.Text, args);
            }

            string upper = t.Text.ToUpperInvariant();
            if (upper == "TRUE")
                return Literal(True);
            if (upper == "FALSE")
                return Literal(False);
            return new RefNode(t.Text);
        }
    }
}
