namespace BinTuner.Util;

/// <summary>
/// Wraps an XDF "raw -> physical" equation. Inverse (physical -> raw) is derived by
/// sampling the equation at two points and assuming linearity, since that covers the
/// scale/offset equations found in practically every fuel/ignition table; a genuinely
/// non-linear equation would need a real solver, which is out of scope here.
/// </summary>
public class MathEquation
{
    private readonly string _equation;
    private readonly string _varName;
    private readonly double _slope;
    private readonly double _intercept;

    public MathEquation(string equation, string varName = "X")
    {
        _equation = string.IsNullOrWhiteSpace(equation) ? varName : equation;
        _varName = varName;
        double f0 = ExpressionEvaluator.Evaluate(_equation, _varName, 0);
        double f1 = ExpressionEvaluator.Evaluate(_equation, _varName, 1);
        _slope = f1 - f0;
        _intercept = f0;
    }

    public double ToPhysical(double raw) => ExpressionEvaluator.Evaluate(_equation, _varName, raw);

    public double ToRaw(double physical) =>
        _slope == 0 ? _intercept : (physical - _intercept) / _slope;
}
