using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;

public static class RenderLawniconsSvgs
{
    private const int OutputSize = 215;
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    private sealed class SvgStyle
    {
        public string Fill = "#000000";
        public string Stroke = "none";
        public double StrokeWidth = 1.0;
        public double Opacity = 1.0;
        public double FillOpacity = 1.0;
        public double StrokeOpacity = 1.0;
        public PenLineCap LineCap = PenLineCap.Flat;
        public PenLineJoin LineJoin = PenLineJoin.Miter;
        public bool Hidden = false;

        public SvgStyle Clone()
        {
            return new SvgStyle
            {
                Fill = Fill,
                Stroke = Stroke,
                StrokeWidth = StrokeWidth,
                Opacity = Opacity,
                FillOpacity = FillOpacity,
                StrokeOpacity = StrokeOpacity,
                LineCap = LineCap,
                LineJoin = LineJoin,
                Hidden = Hidden
            };
        }
    }

    private sealed class SvgContext
    {
        public readonly Dictionary<string, Geometry> Clips = new Dictionary<string, Geometry>(StringComparer.Ordinal);
    }

    public static void Run(string svgDir, string outDir, string zipPath)
    {
        if (!Directory.Exists(svgDir))
        {
            throw new DirectoryNotFoundException(svgDir);
        }

        if (Directory.Exists(outDir))
        {
            Directory.Delete(outDir, true);
        }
        Directory.CreateDirectory(outDir);

        string zipParent = Path.GetDirectoryName(Path.GetFullPath(zipPath));
        if (!String.IsNullOrEmpty(zipParent))
        {
            Directory.CreateDirectory(zipParent);
        }
        if (File.Exists(zipPath))
        {
            File.Delete(zipPath);
        }

        string[] svgFiles = Directory.GetFiles(svgDir, "*.svg")
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        int index = 0;
        foreach (string svgFile in svgFiles)
        {
            index++;
            string pngPath = Path.Combine(outDir, Path.GetFileNameWithoutExtension(svgFile) + ".png");
            RenderOne(svgFile, pngPath);
            if (index % 100 == 0 || index == svgFiles.Length)
            {
                Console.Write("\rRendering SVG icons {0}/{1}", index, svgFiles.Length);
            }
        }
        Console.WriteLine();

        using (ZipArchive archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            archive.CreateEntry("drawable/");
            foreach (string pngFile in Directory.GetFiles(outDir, "*.png").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                archive.CreateEntryFromFile(pngFile, "drawable/" + Path.GetFileName(pngFile), CompressionLevel.Optimal);
            }
        }
    }

    private static void RenderOne(string svgPath, string pngPath)
    {
        XDocument doc = XDocument.Load(svgPath);
        XElement root = doc.Root;
        if (root == null || LocalName(root) != "svg")
        {
            throw new InvalidDataException("Invalid SVG: " + svgPath);
        }

        Rect viewBox = GetViewBox(root);
        double scale = Math.Min(OutputSize / viewBox.Width, OutputSize / viewBox.Height);
        double dx = (OutputSize - viewBox.Width * scale) / 2.0;
        double dy = (OutputSize - viewBox.Height * scale) / 2.0;
        Matrix matrix = new Matrix(scale, 0, 0, scale, dx - viewBox.X * scale, dy - viewBox.Y * scale);

        DrawingVisual visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.PushTransform(new MatrixTransform(matrix));
            SvgContext context = new SvgContext();
            CollectClipPaths(root, context);
            RenderElement(root, dc, context, new SvgStyle());
            dc.Pop();
        }

        RenderTargetBitmap bitmap = new RenderTargetBitmap(
            OutputSize,
            OutputSize,
            96,
            96,
            PixelFormats.Pbgra32);
        bitmap.Render(visual);

        PngBitmapEncoder encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (FileStream fs = File.Create(pngPath))
        {
            encoder.Save(fs);
        }
    }

    private static void CollectClipPaths(XElement element, SvgContext context)
    {
        foreach (XElement child in element.Elements())
        {
            if (LocalName(child) == "clipPath")
            {
                string id = Attr(child, "id");
                if (!String.IsNullOrEmpty(id))
                {
                    GeometryGroup group = new GeometryGroup();
                    group.FillRule = FillRule.Nonzero;
                    foreach (XElement clipChild in child.Elements())
                    {
                        Geometry geometry = BuildGeometry(clipChild);
                        if (geometry != null)
                        {
                            Transform transform = ParseTransform(Attr(clipChild, "transform"));
                            if (transform != null)
                            {
                                geometry = geometry.Clone();
                                geometry.Transform = transform;
                            }
                            group.Children.Add(geometry);
                        }
                    }
                    context.Clips[id] = group;
                }
            }

            CollectClipPaths(child, context);
        }
    }

    private static void RenderElement(XElement element, DrawingContext dc, SvgContext context, SvgStyle inherited)
    {
        string name = LocalName(element);
        if (name == "defs" || name == "clipPath" || name == "style")
        {
            return;
        }

        SvgStyle style = ApplyStyle(element, inherited);
        if (style.Hidden)
        {
            return;
        }

        int pushed = 0;
        Transform transform = ParseTransform(Attr(element, "transform"));
        if (transform != null)
        {
            dc.PushTransform(transform);
            pushed++;
        }

        Geometry clip = ResolveClip(element, context);
        if (clip != null)
        {
            dc.PushClip(clip);
            pushed++;
        }

        Geometry geometry = BuildGeometry(element);
        if (geometry != null)
        {
            ApplyFillRule(element, geometry);
            Brush fill = BuildBrush(style.Fill, style.Opacity * style.FillOpacity);
            Pen pen = BuildPen(style);
            if (fill != null || pen != null)
            {
                dc.DrawGeometry(fill, pen, geometry);
            }
        }

        foreach (XElement child in element.Elements())
        {
            RenderElement(child, dc, context, style);
        }

        while (pushed-- > 0)
        {
            dc.Pop();
        }
    }

    private static Geometry ResolveClip(XElement element, SvgContext context)
    {
        string value = Attr(element, "clip-path");
        if (String.IsNullOrEmpty(value))
        {
            return null;
        }
        Match match = Regex.Match(value, @"url\(#([^)]+)\)");
        if (!match.Success)
        {
            return null;
        }
        Geometry clip;
        return context.Clips.TryGetValue(match.Groups[1].Value, out clip) ? clip : null;
    }

    private static SvgStyle ApplyStyle(XElement element, SvgStyle inherited)
    {
        SvgStyle style = inherited.Clone();
        Dictionary<string, string> inline = ParseStyleAttribute(Attr(element, "style"));

        ApplyStyleValue(style, "fill", FirstValue(element, inline, "fill"));
        ApplyStyleValue(style, "stroke", FirstValue(element, inline, "stroke"));
        ApplyStyleValue(style, "stroke-width", FirstValue(element, inline, "stroke-width"));
        ApplyStyleValue(style, "opacity", FirstValue(element, inline, "opacity"));
        ApplyStyleValue(style, "fill-opacity", FirstValue(element, inline, "fill-opacity"));
        ApplyStyleValue(style, "stroke-opacity", FirstValue(element, inline, "stroke-opacity"));
        ApplyStyleValue(style, "stroke-linecap", FirstValue(element, inline, "stroke-linecap"));
        ApplyStyleValue(style, "stroke-linejoin", FirstValue(element, inline, "stroke-linejoin"));
        ApplyStyleValue(style, "display", FirstValue(element, inline, "display"));
        ApplyStyleValue(style, "visibility", FirstValue(element, inline, "visibility"));
        return style;
    }

    private static string FirstValue(XElement element, Dictionary<string, string> inline, string name)
    {
        string direct = Attr(element, name);
        if (!String.IsNullOrEmpty(direct))
        {
            return direct;
        }
        string value;
        return inline.TryGetValue(name, out value) ? value : null;
    }

    private static void ApplyStyleValue(SvgStyle style, string name, string value)
    {
        if (String.IsNullOrWhiteSpace(value))
        {
            return;
        }
        value = value.Trim();
        if (name == "fill")
        {
            style.Fill = value;
        }
        else if (name == "stroke")
        {
            style.Stroke = value;
        }
        else if (name == "stroke-width")
        {
            style.StrokeWidth = ParseLength(value, style.StrokeWidth);
        }
        else if (name == "opacity")
        {
            style.Opacity = Clamp01(ParseDouble(value, style.Opacity));
        }
        else if (name == "fill-opacity")
        {
            style.FillOpacity = Clamp01(ParseDouble(value, style.FillOpacity));
        }
        else if (name == "stroke-opacity")
        {
            style.StrokeOpacity = Clamp01(ParseDouble(value, style.StrokeOpacity));
        }
        else if (name == "stroke-linecap")
        {
            if (value == "round") style.LineCap = PenLineCap.Round;
            else if (value == "square") style.LineCap = PenLineCap.Square;
            else style.LineCap = PenLineCap.Flat;
        }
        else if (name == "stroke-linejoin")
        {
            if (value == "round") style.LineJoin = PenLineJoin.Round;
            else if (value == "bevel") style.LineJoin = PenLineJoin.Bevel;
            else style.LineJoin = PenLineJoin.Miter;
        }
        else if (name == "display" || name == "visibility")
        {
            if (value == "none" || value == "hidden")
            {
                style.Hidden = true;
            }
        }
    }

    private static Dictionary<string, string> ParseStyleAttribute(string style)
    {
        Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (String.IsNullOrWhiteSpace(style))
        {
            return result;
        }
        string[] parts = style.Split(';');
        foreach (string part in parts)
        {
            int colon = part.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }
            string key = part.Substring(0, colon).Trim();
            string value = part.Substring(colon + 1).Trim();
            if (key.Length > 0)
            {
                result[key] = value;
            }
        }
        return result;
    }

    private static Geometry BuildGeometry(XElement element)
    {
        string name = LocalName(element);
        if (name == "path")
        {
            string d = Attr(element, "d");
            if (String.IsNullOrWhiteSpace(d))
            {
                return null;
            }
            return Geometry.Parse(d);
        }
        if (name == "circle")
        {
            double cx = ParseLength(Attr(element, "cx"), 0);
            double cy = ParseLength(Attr(element, "cy"), 0);
            double r = ParseLength(Attr(element, "r"), 0);
            return new EllipseGeometry(new Point(cx, cy), r, r);
        }
        if (name == "ellipse")
        {
            double cx = ParseLength(Attr(element, "cx"), 0);
            double cy = ParseLength(Attr(element, "cy"), 0);
            double rx = ParseLength(Attr(element, "rx"), 0);
            double ry = ParseLength(Attr(element, "ry"), 0);
            return new EllipseGeometry(new Point(cx, cy), rx, ry);
        }
        if (name == "rect")
        {
            double x = ParseLength(Attr(element, "x"), 0);
            double y = ParseLength(Attr(element, "y"), 0);
            double w = ParseLength(Attr(element, "width"), 0);
            double h = ParseLength(Attr(element, "height"), 0);
            double rx = ParseLength(Attr(element, "rx"), 0);
            double ry = ParseLength(Attr(element, "ry"), rx);
            return new RectangleGeometry(new Rect(x, y, w, h), rx, ry);
        }
        if (name == "line")
        {
            Point p1 = new Point(ParseLength(Attr(element, "x1"), 0), ParseLength(Attr(element, "y1"), 0));
            Point p2 = new Point(ParseLength(Attr(element, "x2"), 0), ParseLength(Attr(element, "y2"), 0));
            return new LineGeometry(p1, p2);
        }
        if (name == "polyline" || name == "polygon")
        {
            List<Point> points = ParsePoints(Attr(element, "points"));
            if (points.Count == 0)
            {
                return null;
            }
            StreamGeometry geometry = new StreamGeometry();
            using (StreamGeometryContext ctx = geometry.Open())
            {
                ctx.BeginFigure(points[0], false, name == "polygon");
                if (points.Count > 1)
                {
                    ctx.PolyLineTo(points.Skip(1).ToList(), true, false);
                }
            }
            geometry.Freeze();
            return geometry;
        }
        return null;
    }

    private static void ApplyFillRule(XElement element, Geometry geometry)
    {
        string rule = Attr(element, "fill-rule");
        if (String.Equals(rule, "evenodd", StringComparison.OrdinalIgnoreCase))
        {
            PathGeometry pathGeometry = geometry as PathGeometry;
            if (pathGeometry != null)
            {
                pathGeometry.FillRule = FillRule.EvenOdd;
            }
            GeometryGroup group = geometry as GeometryGroup;
            if (group != null)
            {
                group.FillRule = FillRule.EvenOdd;
            }
        }
    }

    private static Brush BuildBrush(string value, double opacity)
    {
        Color? color = ParseColor(value, opacity);
        if (!color.HasValue)
        {
            return null;
        }
        SolidColorBrush brush = new SolidColorBrush(color.Value);
        brush.Freeze();
        return brush;
    }

    private static Pen BuildPen(SvgStyle style)
    {
        Color? color = ParseColor(style.Stroke, style.Opacity * style.StrokeOpacity);
        if (!color.HasValue || style.StrokeWidth <= 0)
        {
            return null;
        }
        SolidColorBrush brush = new SolidColorBrush(color.Value);
        brush.Freeze();
        Pen pen = new Pen(brush, style.StrokeWidth);
        pen.StartLineCap = style.LineCap;
        pen.EndLineCap = style.LineCap;
        pen.LineJoin = style.LineJoin;
        pen.Freeze();
        return pen;
    }

    private static Color? ParseColor(string value, double opacity)
    {
        if (String.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        value = value.Trim();
        if (value == "none" || value.StartsWith("url(", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        if (value == "currentColor")
        {
            value = "#000000";
        }

        Color color;
        if (TryParseRgbFunction(value, out color))
        {
            color.A = (byte)Math.Round(color.A * Clamp01(opacity));
            return color;
        }

        try
        {
            color = (Color)ColorConverter.ConvertFromString(value);
            color.A = (byte)Math.Round(color.A * Clamp01(opacity));
            return color;
        }
        catch
        {
            return null;
        }
    }

    private static bool TryParseRgbFunction(string value, out Color color)
    {
        color = Colors.Black;
        Match match = Regex.Match(value, @"rgba?\(([^)]+)\)", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return false;
        }
        string[] parts = match.Groups[1].Value.Split(',');
        if (parts.Length < 3)
        {
            return false;
        }
        byte r = ParseColorByte(parts[0]);
        byte g = ParseColorByte(parts[1]);
        byte b = ParseColorByte(parts[2]);
        byte a = 255;
        if (parts.Length >= 4)
        {
            a = (byte)Math.Round(255 * Clamp01(ParseDouble(parts[3], 1.0)));
        }
        color = Color.FromArgb(a, r, g, b);
        return true;
    }

    private static byte ParseColorByte(string text)
    {
        text = text.Trim();
        if (text.EndsWith("%", StringComparison.Ordinal))
        {
            return (byte)Math.Round(255 * Clamp01(ParseDouble(text.Substring(0, text.Length - 1), 0) / 100.0));
        }
        return (byte)Math.Max(0, Math.Min(255, Math.Round(ParseDouble(text, 0))));
    }

    private static Transform ParseTransform(string transform)
    {
        if (String.IsNullOrWhiteSpace(transform))
        {
            return null;
        }

        TransformGroup group = new TransformGroup();
        MatchCollection matches = Regex.Matches(transform, @"([a-zA-Z]+)\s*\(([^)]*)\)");
        foreach (Match match in matches)
        {
            string kind = match.Groups[1].Value;
            double[] values = ParseNumbers(match.Groups[2].Value).ToArray();
            if (kind == "matrix" && values.Length >= 6)
            {
                group.Children.Add(new MatrixTransform(values[0], values[1], values[2], values[3], values[4], values[5]));
            }
            else if (kind == "translate")
            {
                double x = values.Length > 0 ? values[0] : 0;
                double y = values.Length > 1 ? values[1] : 0;
                group.Children.Add(new TranslateTransform(x, y));
            }
            else if (kind == "scale")
            {
                double x = values.Length > 0 ? values[0] : 1;
                double y = values.Length > 1 ? values[1] : x;
                group.Children.Add(new ScaleTransform(x, y));
            }
            else if (kind == "rotate")
            {
                double angle = values.Length > 0 ? values[0] : 0;
                if (values.Length >= 3)
                {
                    group.Children.Add(new RotateTransform(angle, values[1], values[2]));
                }
                else
                {
                    group.Children.Add(new RotateTransform(angle));
                }
            }
            else if (kind == "skewX" && values.Length > 0)
            {
                group.Children.Add(new SkewTransform(values[0], 0));
            }
            else if (kind == "skewY" && values.Length > 0)
            {
                group.Children.Add(new SkewTransform(0, values[0]));
            }
        }

        return group.Children.Count == 0 ? null : group;
    }

    private static Rect GetViewBox(XElement root)
    {
        string viewBox = Attr(root, "viewBox");
        double[] values = ParseNumbers(viewBox).ToArray();
        if (values.Length >= 4 && values[2] > 0 && values[3] > 0)
        {
            return new Rect(values[0], values[1], values[2], values[3]);
        }

        double width = ParseLength(Attr(root, "width"), 36);
        double height = ParseLength(Attr(root, "height"), width);
        return new Rect(0, 0, width, height);
    }

    private static List<Point> ParsePoints(string text)
    {
        double[] values = ParseNumbers(text).ToArray();
        List<Point> points = new List<Point>();
        for (int i = 0; i + 1 < values.Length; i += 2)
        {
            points.Add(new Point(values[i], values[i + 1]));
        }
        return points;
    }

    private static IEnumerable<double> ParseNumbers(string text)
    {
        if (String.IsNullOrWhiteSpace(text))
        {
            yield break;
        }
        MatchCollection matches = Regex.Matches(text, @"[-+]?(?:\d*\.\d+|\d+)(?:[eE][-+]?\d+)?");
        foreach (Match match in matches)
        {
            double value;
            if (Double.TryParse(match.Value, NumberStyles.Float, Invariant, out value))
            {
                yield return value;
            }
        }
    }

    private static double ParseLength(string text, double fallback)
    {
        if (String.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }
        Match match = Regex.Match(text.Trim(), @"[-+]?(?:\d*\.\d+|\d+)(?:[eE][-+]?\d+)?");
        if (!match.Success)
        {
            return fallback;
        }
        double value;
        return Double.TryParse(match.Value, NumberStyles.Float, Invariant, out value) ? value : fallback;
    }

    private static double ParseDouble(string text, double fallback)
    {
        if (String.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }
        text = text.Trim();
        if (text.EndsWith("%", StringComparison.Ordinal))
        {
            return ParseDouble(text.Substring(0, text.Length - 1), fallback * 100.0) / 100.0;
        }
        double value;
        return Double.TryParse(text, NumberStyles.Float, Invariant, out value) ? value : fallback;
    }

    private static double Clamp01(double value)
    {
        if (value < 0) return 0;
        if (value > 1) return 1;
        return value;
    }

    private static string Attr(XElement element, string localName)
    {
        XAttribute attr = element.Attributes().FirstOrDefault(a => a.Name.LocalName == localName);
        return attr == null ? null : attr.Value;
    }

    private static string LocalName(XElement element)
    {
        return element.Name.LocalName;
    }
}
