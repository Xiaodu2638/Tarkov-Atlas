namespace TarkovAtlas.Models;

public readonly record struct GamePoint(double X, double Z)
{
    public static GamePoint operator +(GamePoint a, GamePoint b) => new(a.X + b.X, a.Z + b.Z);
    public static GamePoint operator -(GamePoint a, GamePoint b) => new(a.X - b.X, a.Z - b.Z);
    public double Length => Math.Sqrt(X * X + Z * Z);
}

public sealed class ScreenshotInfo
{
    public string FilePath { get; set; } = "";
    public string FileName { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public double Qx { get; set; }
    public double Qy { get; set; }
    public double Qz { get; set; }
    public double Qw { get; set; }
    public double HeadingDegrees { get; set; }
    public string MapKey { get; set; } = "";
    public DateTime CapturedAt { get; set; }
    public DateTime FileTime { get; set; }
    public bool MapWasGuessed { get; set; }

    public string DisplayName => $"{X:0.0}, {Y:0.0}, {Z:0.0}";
    public string TimeText => CapturedAt == default ? FileTime.ToString("MM-dd HH:mm") : CapturedAt.ToString("MM-dd HH:mm");
    public string Tooltip => $"{FileName}\nX {X:0.###}  Y {Y:0.###}  Z {Z:0.###}\n朝向 {HeadingDegrees:0}°";
}
