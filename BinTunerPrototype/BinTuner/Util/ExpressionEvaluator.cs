using System.Globalization;

namespace BinTuner.Util;

/// <summary>
/// Tiny recursive-descent evaluator for the simple algebraic equations XDF files
/// embed (e.g. "X*0.75-48", "(X/4)+2"). Supports + - * / ^, parentheses, unary
/// minus, and a single case-insensitive variable name.
/// </summary>
public static class ExpressionEvaluator
{
    public static double Evaluate(string expression, string variableName, double variableValue)
    {
        var tokens = Tokenize(expression);
        var parser = new Parser(tokens, variableName, variableValue);
        double result = parser.ParseExpression();
        if (!parser.AtEnd)
            throw new FormatException($"Unexpected trailing tokens in expression '{expression}'");
        return result;
    }

    private enum TokType { Number, Identifier, Plus, Minus, Star, Slash, Caret, LParen, RParen, End }

    private record struct Token(TokType Type, string Text);

    private static List<Token> Tokenize(string expr)
    {
        var tokens = new List<Token>();
        int i = 0;
        while (i < expr.Length)
        {
            char c = expr[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            switch (c)
            {
                case '+': tokens.Add(new Token(TokType.Plus, "+")); i++; continue;
                case '-': tokens.Add(new Token(TokType.Minus, "-")); i++; continue;
                case '*': tokens.Add(new Token(TokType.Star, "*")); i++; continue;
                case '/': tokens.Add(new Token(TokType.Slash, "/")); i++; continue;
                case '^': tokens.Add(new Token(TokType.Caret, "^")); i++; continue;
                case '(': tokens.Add(new Token(TokType.LParen, "(")); i++; continue;
                case ')': tokens.Add(new Token(TokType.RParen, ")")); i++; continue;
            }
            if (char.IsDigit(c) || c == '.')
            {
                int start = i;
                while (i < expr.Length && (char.IsDigit(expr[i]) || expr[i] == '.')) i++;
                tokens.Add(new Token(TokType.Number, expr[start..i]));
                continue;
            }
            if (char.IsLetter(c) || c == '_')
            {
                int start = i;
                while (i < expr.Length && (char.IsLetterOrDigit(expr[i]) || expr[i] == '_')) i++;
                tokens.Add(new Token(TokType.Identifier, expr[start..i]));
                continue;
            }
            throw new FormatException($"Unexpected character '{c}' in expression '{expr}'");
        }
        tokens.Add(new Token(TokType.End, ""));
        return tokens;
    }

    private class Parser
    {
        private readonly List<Token> _tokens;
        private readonly string _varName;
        private readonly double _varValue;
        private int _pos;

        public Parser(List<Token> tokens, string varName, double varValue)
        {
            _tokens = tokens;
            _varName = varName;
            _varValue = varValue;
        }

        public bool AtEnd => _tokens[_pos].Type == TokType.End;
        private Token Current => _tokens[_pos];

        public double ParseExpression()
        {
            double value = ParseTerm();
            while (Current.Type is TokType.Plus or TokType.Minus)
            {
                bool add = Current.Type == TokType.Plus;
                _pos++;
                double rhs = ParseTerm();
                value = add ? value + rhs : value - rhs;
            }
            return value;
        }

        private double ParseTerm()
        {
            double value = ParsePower();
            while (Current.Type is TokType.Star or TokType.Slash)
            {
                bool mul = Current.Type == TokType.Star;
                _pos++;
                double rhs = ParsePower();
                value = mul ? value * rhs : value / rhs;
            }
            return value;
        }

        private double ParsePower()
        {
            double value = ParseUnary();
            if (Current.Type == TokType.Caret)
            {
                _pos++;
                double exponent = ParsePower();
                value = Math.Pow(value, exponent);
            }
            return value;
        }

        private double ParseUnary()
        {
            if (Current.Type == TokType.Minus)
            {
                _pos++;
                return -ParseUnary();
            }
            if (Current.Type == TokType.Plus)
            {
                _pos++;
                return ParseUnary();
            }
            return ParsePrimary();
        }

        private double ParsePrimary()
        {
            if (Current.Type == TokType.Number)
            {
                double v = double.Parse(Current.Text, CultureInfo.InvariantCulture);
                _pos++;
                return v;
            }
            if (Current.Type == TokType.Identifier)
            {
                string name = Current.Text;
                _pos++;
                if (!string.Equals(name, _varName, StringComparison.OrdinalIgnoreCase))
                    throw new FormatException($"Unknown identifier '{name}' in expression");
                return _varValue;
            }
            if (Current.Type == TokType.LParen)
            {
                _pos++;
                double v = ParseExpression();
                if (Current.Type != TokType.RParen)
                    throw new FormatException("Missing closing parenthesis");
                _pos++;
                return v;
            }
            throw new FormatException("Unexpected token in expression");
        }
    }
}
