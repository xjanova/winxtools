using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NetX.App.Controls;

/// <summary>
/// The classic flat defrag map: the volume from start (top left) to end
/// (bottom right) as small cells, coloured like the 3D view.
/// </summary>
public sealed class ClusterMapView : FrameworkElement
{
    private const int Cell = 6;
    private const int Gap = 1;

    private WriteableBitmap? _bitmap;
    private uint[] _pixels = Array.Empty<uint>();
    private int _columns, _rows;

    private float[] _used = Array.Empty<float>();
    private float[]? _fragmented, _metadata, _unmovable;
    private float[] _activity = Array.Empty<float>();
    private byte[]? _health;

    public ClusterMapView()
    {
        SnapsToDevicePixels = true;
        SizeChanged += (s, e) => Rebuild();
    }

    public void SetLayers(float[] used, float[]? fragmented, float[]? metadata, float[]? unmovable)
    {
        _used = used;
        _fragmented = fragmented?.Length == used.Length ? fragmented : null;
        _metadata = metadata?.Length == used.Length ? metadata : null;
        _unmovable = unmovable?.Length == used.Length ? unmovable : null;
        if (_activity.Length != used.Length) _activity = new float[used.Length];
        Redraw();
    }

    public void Clear()
    {
        _used = Array.Empty<float>();
        _fragmented = _metadata = _unmovable = null;
        _activity = Array.Empty<float>();
        _health = null;
        Redraw();
    }

    /// <summary>Shows a surface scan (worst read per cell) instead of the allocation map; call again to refresh.</summary>
    public void SetHealth(byte[] health)
    {
        _health = health;
        _used = Array.Empty<float>();
        if (_activity.Length != health.Length) _activity = new float[health.Length];
        Redraw();
    }

    /// <summary>Highlights blocks that just changed; the highlight fades on the next redraws.</summary>
    public void Flash(IEnumerable<int> blocks)
    {
        foreach (var b in blocks)
            if ((uint)b < (uint)_activity.Length) _activity[b] = 1f;
        Redraw();
        for (int i = 0; i < _activity.Length; i++) _activity[i] *= 0.35f;
    }

    private void Rebuild()
    {
        int width = Math.Max(1, (int)ActualWidth), height = Math.Max(1, (int)ActualHeight);
        _columns = Math.Max(1, (width + Gap) / (Cell + Gap));
        _rows = Math.Max(1, (height + Gap) / (Cell + Gap));
        int pw = _columns * (Cell + Gap), ph = _rows * (Cell + Gap);
        _bitmap = new WriteableBitmap(pw, ph, 96, 96, PixelFormats.Bgra32, null);
        _pixels = new uint[pw * ph];
        Redraw();
        InvalidateVisual();
    }

    private void Redraw()
    {
        if (_bitmap == null) return;
        int pw = _bitmap.PixelWidth, ph = _bitmap.PixelHeight;
        Array.Fill(_pixels, 0x00000000u);

        int cells = _columns * _rows;
        int blocks = _health?.Length ?? _used.Length;
        for (int c = 0; c < cells; c++)
        {
            uint color;
            if (blocks == 0)
            {
                color = 0xFF101B30u;
            }
            else
            {
                int from = (int)((long)c * blocks / cells);
                int to = Math.Max(from + 1, (int)((long)(c + 1) * blocks / cells));
                if (from >= blocks) break;
                if (_health != null)
                {
                    byte worst = 0;
                    float glow = 0;
                    for (int b = from; b < to && b < blocks; b++)
                    {
                        if (_health[b] > worst) worst = _health[b];
                        glow = Math.Max(glow, _activity[b]);
                    }
                    color = MapPalette.HealthColor(worst, glow);
                    goto paint;
                }
                float used = 0, frag = 0, meta = 0, unmov = 0, act = 0;
                for (int b = from; b < to && b < blocks; b++)
                {
                    used += _used[b];
                    frag += _fragmented?[b] ?? 0;
                    meta = Math.Max(meta, _metadata?[b] ?? 0);
                    unmov = Math.Max(unmov, _unmovable?[b] ?? 0);
                    act = Math.Max(act, _activity[b]);
                }
                int n = Math.Min(to, blocks) - from;
                color = MapPalette.Color(used / n, frag / n, meta, unmov, act);
            }

            paint:
            int x0 = (c % _columns) * (Cell + Gap), y0 = (c / _columns) * (Cell + Gap);
            for (int y = 0; y < Cell; y++)
            {
                int row = (y0 + y) * pw + x0;
                for (int x = 0; x < Cell; x++) _pixels[row + x] = color;
            }
        }
        _bitmap.WritePixels(new Int32Rect(0, 0, pw, ph), _pixels, pw * 4, 0);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_bitmap != null)
            dc.DrawImage(_bitmap, new Rect(0, 0, _bitmap.PixelWidth, _bitmap.PixelHeight));
    }
}
