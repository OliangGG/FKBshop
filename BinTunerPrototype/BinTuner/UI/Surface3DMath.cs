namespace BinTuner.UI;

/// <summary>A 3D point in world space. No dependency on System.Drawing, so this whole file is
/// pure math and can be unit-tested without a Windows GUI (unlike Surface3DControl's OnPaint).</summary>
public readonly struct Vec3
{
    public readonly double X, Y, Z;
    public Vec3(double x, double y, double z) { X = x; Y = y; Z = z; }

    public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);
}

/// <summary>
/// Manual 3D-to-2D pipeline for the TunerPro-style rotatable mesh graph: normalize each axis
/// independently into a common range, rotate around the vertical (azimuth) and horizontal
/// (elevation) axes, then orthographically project to screen space. Kept free of Graphics/Color
/// so the transform math itself is testable on any platform.
/// </summary>
public static class Surface3DMath
{
    /// <summary>Maps a value into [-1, 1] given its data range — used for the two floor axes (row/col index or breakpoint).</summary>
    public static double NormalizeCentered(double value, double min, double max) =>
        min == max ? 0.0 : ((value - min) / (max - min)) * 2.0 - 1.0;

    /// <summary>Maps a value into [0, 1] given its data range — used for the height (table value) axis.</summary>
    public static double NormalizeHeight(double value, double min, double max) =>
        min == max ? 0.0 : (value - min) / (max - min);

    /// <summary>
    /// Rotates a world-space point around the vertical (Y) axis by <paramref name="azimuthDeg"/>,
    /// then around the horizontal (X) axis by <paramref name="elevationDeg"/> — the standard
    /// "orbit camera" pair used by 3D chart/CAD viewers (drag left/right = azimuth, up/down = elevation).
    /// Both rotations are orthonormal, so the result always has the same length as the input.
    /// </summary>
    public static Vec3 Rotate(Vec3 p, double azimuthDeg, double elevationDeg)
    {
        double az = azimuthDeg * Math.PI / 180.0;
        double el = elevationDeg * Math.PI / 180.0;

        // Rotate around Y (spin the floor plane left/right)
        double cosA = Math.Cos(az), sinA = Math.Sin(az);
        double x1 = p.X * cosA + p.Z * sinA;
        double z1 = -p.X * sinA + p.Z * cosA;
        double y1 = p.Y;

        // Rotate around X (tilt to look down at the floor)
        double cosE = Math.Cos(el), sinE = Math.Sin(el);
        double y2 = y1 * cosE - z1 * sinE;
        double z2 = y1 * sinE + z1 * cosE;

        return new Vec3(x1, y2, z2);
    }

    /// <summary>
    /// Orthographic projection of an already-rotated point to unscaled, untranslated 2D screen
    /// offsets (screen Y is flipped so world "up" renders visually up). The caller multiplies by
    /// a pixel scale and adds the control's center point.
    /// </summary>
    public static (double sx, double sy) Project(Vec3 rotated) => (rotated.X, -rotated.Y);
}
