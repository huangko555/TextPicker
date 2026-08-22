namespace TextPicker;

/// <summary>物理像素屏幕坐标点。公共 API 一律用本强类型，坐标源 = GetPhysicalCursorPos（UIA 客户端坐标同为物理像素）。</summary>
public readonly record struct PhysicalScreenPoint(double X, double Y);

/// <summary>物理像素屏幕矩形（LTRB）。</summary>
public readonly record struct PhysicalScreenRect(double Left, double Top, double Right, double Bottom)
{
    public double Width => Right - Left;
    public double Height => Bottom - Top;

    /// <summary>外扩指定边距后的矩形。</summary>
    public PhysicalScreenRect Inflate(double margin) => new(Left - margin, Top - margin, Right + margin, Bottom + margin);

    public bool Contains(PhysicalScreenPoint p) => p.X >= Left && p.X <= Right && p.Y >= Top && p.Y <= Bottom;
}
