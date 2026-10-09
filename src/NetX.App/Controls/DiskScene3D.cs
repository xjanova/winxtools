using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;

namespace NetX.App.Controls;

public enum DiskSceneLayout
{
    /// <summary>A hard disk: spinning platter, actuator arm, read/write head.</summary>
    Platter,
    /// <summary>An SSD: a circuit board with the volume drawn as a grid of flash blocks.</summary>
    Board
}

/// <summary>
/// Live 3D view of a drive. The platter (or SSD board) is painted with the
/// volume's real allocation map; changed blocks glow, data that moved flies
/// from where it was to where it went, and the head seeks to the area being
/// worked on. Drag to orbit, mouse wheel to zoom, double-click to reset.
/// </summary>
public sealed class DiskScene3D : Grid
{
    // World layout (arbitrary units; the casing is about 6.6 × 8.5).
    private const double PlatterRadius = 2.85;
    private static readonly Point3D PlatterCenter = new(0, 0.12, -0.45);
    private static readonly Point3D ArmPivot = new(2.4, 0.2, 3.55);
    private const double ArmLength = 4.15;
    private const double ParkRadius = PlatterRadius + 0.32;

    private const int PlatterTextureSize = 768;
    private const int BoardTextureWidth = 1024, BoardTextureHeight = 512;

    private readonly Viewport3D _viewport = new() { ClipToBounds = true, IsHitTestVisible = false };
    private readonly PerspectiveCamera _camera = new() { FieldOfView = 38, NearPlaneDistance = 0.5, FarPlaneDistance = 200 };
    private readonly Model3DGroup _world = new();
    private readonly Model3DGroup _deviceGroup = new();
    private readonly Model3DGroup _spinGroup = new();
    private readonly AxisAngleRotation3D _spinRotation = new(new Vector3D(0, 1, 0), 0);
    private readonly Model3DGroup _armGroup = new();
    private readonly AxisAngleRotation3D _armRotation = new(new Vector3D(0, 1, 0), 0);
    private readonly PointLight _headLight = new(Color.FromRgb(0x5c, 0xd8, 0xff), new Point3D(0, 0.6, 0))
    {
        Range = 2.6, ConstantAttenuation = 0.55, LinearAttenuation = 0.55, QuadraticAttenuation = 0.25
    };
    private readonly Model3DGroup _particleGroup = new();
    private readonly List<Particle> _particles = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Random _random = new();

    private DiskSceneLayout _layout = DiskSceneLayout.Platter;
    private WriteableBitmap? _texture;
    private uint[] _pixels = Array.Empty<uint>();
    private int[] _pixelBlock = Array.Empty<int>();
    private bool[] _pixelGap = Array.Empty<bool>();
    private uint[] _staticColor = Array.Empty<uint>();
    private int _textureWidth, _textureHeight;

    private int _blockCount = 1;
    private float[] _used = Array.Empty<float>();
    private float[]? _fragmented, _metadata, _unmovable;
    private float[] _activity = Array.Empty<float>();
    private byte[]? _health;
    private uint[] _blockColor = Array.Empty<uint>(), _blockColorGap = Array.Empty<uint>();
    private bool _colorsDirty = true, _textureDirty = true;

    private PlatterGeometry _geometry = new(0, 1);
    private GridGeometry _grid = new(1, 1, 1);
    private Point3D _boardOrigin;
    private double _boardWidth, _boardDepth;

    private double _spinAngle;
    private double _headRadius = ParkRadius, _headVelocity, _headTarget = ParkRadius;
    private readonly Queue<double> _seekQueue = new();
    private double _nextSeekAt;
    private double? _sweep;

    private double _yaw = -26, _pitch = 50, _distance = 16.5;
    private Point? _dragFrom;
    private double _lastInteraction = -100, _lastFrame, _lastTextureFrame;
    private bool _rendering;

    public DiskScene3D()
    {
        Background = Brushes.Transparent; // the whole area takes the mouse
        ClipToBounds = true;
        Children.Add(_viewport);
        _viewport.Camera = _camera;

        var lights = new Model3DGroup();
        lights.Children.Add(new AmbientLight(Color.FromRgb(0x46, 0x50, 0x64)));
        lights.Children.Add(new DirectionalLight(Color.FromRgb(0xd8, 0xe2, 0xff), new Vector3D(-0.35, -1, -0.55)));
        lights.Children.Add(new DirectionalLight(Color.FromRgb(0x2c, 0x4c, 0x9c), new Vector3D(0.75, -0.35, 0.65)));
        _world.Children.Add(lights);
        _world.Children.Add(_headLight);
        _world.Children.Add(BuildFloor());
        _world.Children.Add(_deviceGroup);
        _viewport.Children.Add(new ModelVisual3D { Content = _world });

        MouseLeftButtonDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnMouseUp;
        LostMouseCapture += (s, e) => _dragFrom = null;
        MouseWheel += OnMouseWheel;
        Loaded += (s, e) => StartRendering();
        Unloaded += (s, e) => StopRendering();
        IsVisibleChanged += (s, e) => { if (IsVisible) StartRendering(); else StopRendering(); };

        Configure(DiskSceneLayout.Platter, 0, 1, 2048);
        UpdateCamera();
    }

    #region Public API

    /// <summary>True while the drive is being worked on: faster spin, brighter head.</summary>
    public bool Busy { get; set; }

    public DiskSceneLayout Layout => _layout;

    /// <summary>Number of blocks the model was built for (layers must have exactly this many).</summary>
    public int BlockCount => _blockCount;

    /// <summary>
    /// Builds the model. <paramref name="spanStart"/>…<paramref name="spanEnd"/> is
    /// where the volume sits on its disk (0 = first sector, outer edge of the platter).
    /// </summary>
    public void Configure(DiskSceneLayout layout, double spanStart, double spanEnd, int blockCount)
    {
        _layout = layout;
        _blockCount = Math.Max(1, blockCount);
        _used = new float[_blockCount];
        _fragmented = _metadata = _unmovable = null;
        _activity = new float[_blockCount];
        _health = null;
        _blockColor = new uint[_blockCount];
        _blockColorGap = new uint[_blockCount];
        _particles.Clear();
        _particleGroup.Children.Clear();
        _spinGroup.Children.Clear();
        _seekQueue.Clear();
        _sweep = null;
        _headTarget = ParkRadius;

        spanStart = Math.Clamp(spanStart, 0, 1);
        spanEnd = Math.Clamp(spanEnd, spanStart + 0.0001, 1);
        _geometry = new PlatterGeometry(spanStart, spanEnd);

        _deviceGroup.Children.Clear();
        if (layout == DiskSceneLayout.Platter) BuildHardDisk();
        else BuildBoard();
        ResetView();

        _colorsDirty = _textureDirty = true;
        RefreshTexture();
    }

    /// <summary>Paints the allocation map. Arrays are indexed by block; any layer may be null.</summary>
    public void SetLayers(float[] used, float[]? fragmented, float[]? metadata, float[]? unmovable)
    {
        if (used.Length != _blockCount) return;
        _used = used;
        _fragmented = fragmented?.Length == _blockCount ? fragmented : null;
        _metadata = metadata?.Length == _blockCount ? metadata : null;
        _unmovable = unmovable?.Length == _blockCount ? unmovable : null;
        _colorsDirty = true;
        if (!_rendering) RefreshTexture(); // not animating (hidden page): paint now
    }

    /// <summary>
    /// Paints how well each block read (a surface scan) instead of the
    /// allocation map; null switches back. The array is read on every frame, so
    /// a scan can keep filling it in.
    /// </summary>
    public void SetHealth(byte[]? health)
    {
        if (health != null && health.Length != _blockCount) return;
        _health = health;
        _colorsDirty = true;
        if (!_rendering) RefreshTexture();
    }

    /// <summary>The health array changed in place (scan progress).</summary>
    public void InvalidateColors()
    {
        _colorsDirty = true;
        if (!_rendering) RefreshTexture();
    }

    /// <summary>
    /// Shows data that moved: each source block sends a particle to a target
    /// block, both glow, and the head seeks over them.
    /// </summary>
    public void Pulse(IReadOnlyList<int> sources, IReadOnlyList<int> targets)
    {
        foreach (var b in sources) Flash(b, 0.75f);
        foreach (var b in targets) Flash(b, 1f);

        if (_layout == DiskSceneLayout.Platter)
        {
            int pairs = Math.Min(Math.Min(sources.Count, targets.Count), 18);
            for (int i = 0; i < pairs; i++)
                SpawnParticle(sources[i], targets[i % targets.Count], i * 0.05);

            // Seek over the most changed places, a few per refresh.
            foreach (var b in targets.Take(3).Concat(sources.Take(2)))
                if ((uint)b < (uint)_blockCount) _seekQueue.Enqueue(_geometry.RadiusOf(b, _blockCount) * PlatterRadius);
            while (_seekQueue.Count > 8) _seekQueue.Dequeue();
        }
        else
        {
            int pairs = Math.Min(Math.Min(sources.Count, targets.Count), 14);
            for (int i = 0; i < pairs; i++)
                SpawnParticle(sources[i], targets[i % targets.Count], i * 0.05);
        }
    }

    /// <summary>
    /// Real reads and writes the disk just did (as fractions of the volume): the
    /// blocks glow and the head goes there — a few seeks per call, because a
    /// drawn head can't visibly follow hundreds of I/Os a second.
    /// </summary>
    public void ShowIo(IReadOnlyList<(double Fraction, bool Write)> io)
    {
        if (io.Count == 0) return;
        for (int i = Math.Max(0, io.Count - 48); i < io.Count; i++)
            Flash((int)Math.Clamp(io[i].Fraction * _blockCount, 0, _blockCount - 1), io[i].Write ? 1f : 0.7f);

        if (_layout != DiskSceneLayout.Platter) return;
        int step = Math.Max(1, io.Count / 3);
        for (int i = io.Count - 1, n = 0; i >= 0 && n < 3; i -= step, n++)
            _seekQueue.Enqueue(_geometry.RadiusOfVolumeFraction(Math.Clamp(io[i].Fraction, 0, 1)) * PlatterRadius);
        while (_seekQueue.Count > 6) _seekQueue.Dequeue();
    }

    /// <summary>Lights one block (used when the analysis reaches it or Windows touched it).</summary>
    public void Flash(int block, float strength = 1f)
    {
        if ((uint)block >= (uint)_blockCount) return;
        _activity[block] = Math.Max(_activity[block], strength);
        _colorsDirty = true;
    }

    /// <summary>Moves the head over a fraction of the volume (0 = start, 1 = end), or parks it (null).</summary>
    public void SeekToFraction(double? fraction)
    {
        _seekQueue.Clear();
        _headTarget = fraction is double f
            ? _geometry.RadiusOfVolumeFraction(Math.Clamp(f, 0, 1)) * PlatterRadius
            : ParkRadius;
    }

    /// <summary>A moving light over the SSD board (TRIM progress), or null to hide it.</summary>
    public void SetSweep(double? fraction) => _sweep = fraction;

    public void ResetView()
    {
        _yaw = -26;
        _pitch = 50;
        _distance = _layout == DiskSceneLayout.Platter ? 16.5 : 14;
        UpdateCamera();
    }

    #endregion

    #region Model building

    private void BuildHardDisk()
    {
        var aluminium = Mat(Color.FromRgb(0x2a, 0x33, 0x45), Color.FromArgb(0x60, 0xff, 0xff, 0xff), 25);
        var darkMetal = Mat(Color.FromRgb(0x1a, 0x20, 0x2c), Color.FromArgb(0x50, 0xff, 0xff, 0xff), 30);
        var chrome = Mat(Color.FromRgb(0xb9, 0xc4, 0xd6), Color.FromArgb(0xd0, 0xff, 0xff, 0xff), 70);
        var steel = Mat(Color.FromRgb(0x6e, 0x79, 0x8c), Color.FromArgb(0x90, 0xff, 0xff, 0xff), 45);

        // Casing: base plate, a raised rim around the platter well.
        _deviceGroup.Children.Add(Model(Mesh.Box(-3.3, 3.3, -0.42, -0.08, -3.55, 5.05), aluminium));
        _deviceGroup.Children.Add(Model(Mesh.Ring(new Point3D(PlatterCenter.X, 0, PlatterCenter.Z), PlatterRadius + 0.18,
            PlatterRadius + 0.42, -0.08, 0.22, 96), darkMetal));
        _deviceGroup.Children.Add(Model(Mesh.Box(-3.3, -2.9, -0.08, 0.22, 2.4, 5.05), darkMetal));
        _deviceGroup.Children.Add(Model(Mesh.Box(-3.3, 3.3, -0.08, 0.22, 4.7, 5.05), darkMetal));
        // Screws in the corners.
        foreach (var (x, z) in new[] { (-2.95, -3.2), (2.95, -3.2), (-2.95, 4.7), (2.95, 4.7), (-2.95, 1.0), (2.95, 1.0) })
            _deviceGroup.Children.Add(Model(Mesh.Cylinder(new Point3D(x, -0.08, z), 0.13, 0.06, 16), chrome));

        // Platter (painted with the map) on a spinning group.
        _textureWidth = _textureHeight = PlatterTextureSize;
        _texture = new WriteableBitmap(_textureWidth, _textureHeight, 96, 96, PixelFormats.Bgra32, null);
        BuildPlatterPixelMap();

        var mapBrush = new ImageBrush(_texture) { ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, 1, 1) };
        var glowBrush = new ImageBrush(_texture) { Opacity = 0.62, ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, 1, 1) };
        var platterMaterial = new MaterialGroup();
        platterMaterial.Children.Add(new DiffuseMaterial(mapBrush));
        platterMaterial.Children.Add(new EmissiveMaterial(glowBrush));
        platterMaterial.Children.Add(new SpecularMaterial(new SolidColorBrush(Color.FromArgb(0x70, 0xff, 0xff, 0xff)), 38));

        _spinGroup.Children.Clear();
        _spinGroup.Children.Add(Model(Mesh.Disc(PlatterCenter, PlatterRadius, 128), platterMaterial));
        _spinGroup.Children.Add(Model(Mesh.Cylinder(new Point3D(PlatterCenter.X, PlatterCenter.Y - 0.1, PlatterCenter.Z),
            PlatterRadius, 0.1, 128, capTop: false), steel));
        // Spindle hub with clamp screws.
        _spinGroup.Children.Add(Model(Mesh.Cylinder(new Point3D(PlatterCenter.X, PlatterCenter.Y, PlatterCenter.Z), 0.68, 0.16, 48), chrome));
        _spinGroup.Children.Add(Model(Mesh.Cylinder(new Point3D(PlatterCenter.X, PlatterCenter.Y + 0.16, PlatterCenter.Z), 0.36, 0.06, 32), steel));
        for (int i = 0; i < 6; i++)
        {
            double a = i * Math.PI / 3;
            _spinGroup.Children.Add(Model(Mesh.Cylinder(new Point3D(PlatterCenter.X + Math.Cos(a) * 0.52,
                PlatterCenter.Y + 0.16, PlatterCenter.Z + Math.Sin(a) * 0.52), 0.06, 0.03, 10), darkMetal));
        }
        _spinGroup.Children.Add(_particleGroup);
        _spinGroup.Transform = new RotateTransform3D(_spinRotation, PlatterCenter);
        _deviceGroup.Children.Add(_spinGroup);

        // Actuator: voice-coil magnet, pivot bearing, the arm and the head.
        _deviceGroup.Children.Add(Model(Mesh.Box(ArmPivot.X - 0.2, ArmPivot.X + 0.85, -0.08, 0.3, ArmPivot.Z - 0.15, ArmPivot.Z + 1.2), darkMetal));
        _deviceGroup.Children.Add(Model(Mesh.Cylinder(new Point3D(ArmPivot.X, 0.1, ArmPivot.Z), 0.34, 0.42, 32), chrome));

        _armGroup.Children.Clear();
        _armGroup.Children.Add(Model(Mesh.Arm(ArmLength), steel));
        _armGroup.Children.Add(Model(Mesh.Box(ArmLength - 0.16, ArmLength + 0.06, -0.06, 0.02, -0.07, 0.07),
            new EmissiveMaterial(new SolidColorBrush(Color.FromRgb(0x5c, 0xd8, 0xff)))));
        var armTransform = new Transform3DGroup();
        armTransform.Children.Add(new RotateTransform3D(_armRotation));
        armTransform.Children.Add(new TranslateTransform3D(ArmPivot.X, ArmPivot.Y + 0.02, ArmPivot.Z));
        _armGroup.Transform = armTransform;
        _deviceGroup.Children.Add(_armGroup);

        // Connector strip at the back edge.
        _deviceGroup.Children.Add(Model(Mesh.Box(-2.2, 1.4, -0.36, -0.12, 5.05, 5.25),
            Mat(Color.FromRgb(0x10, 0x14, 0x1c), Color.FromArgb(0x30, 0xff, 0xff, 0xff), 10)));
        SetArmForRadius(_headRadius);
    }

    private void BuildBoard()
    {
        var pcb = Mat(Color.FromRgb(0x0f, 0x24, 0x22), Color.FromArgb(0x40, 0xff, 0xff, 0xff), 20);
        var chip = Mat(Color.FromRgb(0x12, 0x16, 0x1e), Color.FromArgb(0x60, 0xff, 0xff, 0xff), 40);
        var gold = Mat(Color.FromRgb(0xd8, 0xa8, 0x3c), Color.FromArgb(0xc0, 0xff, 0xf2, 0xc0), 60);

        _boardWidth = 7.4;
        _boardDepth = 3.3;
        _boardOrigin = new Point3D(-3.3, 0.12, -1.65);
        _deviceGroup.Children.Add(Model(Mesh.Box(-4.2, 4.2, -0.2, 0.02, -2.05, 2.05), pcb));
        // Controller chip and the M.2 edge connector.
        _deviceGroup.Children.Add(Model(Mesh.Box(-4.05, -3.45, 0.02, 0.22, -0.55, 0.55), chip));
        for (int i = 0; i < 14; i++)
        {
            double z = -1.6 + i * 0.235;
            _deviceGroup.Children.Add(Model(Mesh.Box(4.2, 4.6, -0.17, -0.02, z, z + 0.14), gold));
        }

        _textureWidth = BoardTextureWidth;
        _textureHeight = BoardTextureHeight;
        _texture = new WriteableBitmap(_textureWidth, _textureHeight, 96, 96, PixelFormats.Bgra32, null);
        BuildBoardPixelMap();

        var mapBrush = new ImageBrush(_texture) { ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, 1, 1) };
        var glowBrush = new ImageBrush(_texture) { Opacity = 0.7, ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, 1, 1) };
        var material = new MaterialGroup();
        material.Children.Add(new DiffuseMaterial(mapBrush));
        material.Children.Add(new EmissiveMaterial(glowBrush));
        material.Children.Add(new SpecularMaterial(new SolidColorBrush(Color.FromArgb(0x50, 0xff, 0xff, 0xff)), 30));
        _deviceGroup.Children.Add(Model(Mesh.TexturedQuad(_boardOrigin, _boardWidth, _boardDepth), material));

        _particleGroup.Transform = null;
        _deviceGroup.Children.Add(_particleGroup);
    }

    private static Model3DGroup BuildFloor()
    {
        // A faint "tron" grid that fades out at the edges.
        const int size = 512;
        var bmp = new WriteableBitmap(size, size, 96, 96, PixelFormats.Bgra32, null);
        var pixels = new uint[size * size];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                double dx = (x - size / 2.0) / (size / 2.0), dy = (y - size / 2.0) / (size / 2.0);
                double fade = Math.Clamp(1 - Math.Sqrt(dx * dx + dy * dy), 0, 1);
                bool line = x % 32 == 0 || y % 32 == 0;
                byte alpha = (byte)((line ? 110 : 18) * fade * fade);
                uint rgb = line ? 0x00_2e_9c_d8u : 0x00_0a_1a_33u;
                pixels[y * size + x] = ((uint)alpha << 24) | rgb;
            }
        }
        bmp.WritePixels(new Int32Rect(0, 0, size, size), pixels, size * 4, 0);
        bmp.Freeze();
        var brush = new ImageBrush(bmp);
        brush.Freeze();
        var group = new Model3DGroup();
        group.Children.Add(new GeometryModel3D(Mesh.TexturedQuad(new Point3D(-14, -0.45, -13), 28, 28), new EmissiveMaterial(brush)));
        return group;
    }

    private static Material Mat(Color diffuse, Color specular, double power)
    {
        var group = new MaterialGroup();
        group.Children.Add(new DiffuseMaterial(new SolidColorBrush(diffuse)));
        group.Children.Add(new SpecularMaterial(new SolidColorBrush(specular), power));
        group.Freeze();
        return group;
    }

    private static GeometryModel3D Model(MeshGeometry3D mesh, Material material) =>
        new(mesh, material) { BackMaterial = material };

    #endregion

    #region Texture (the allocation map)

    private void BuildPlatterPixelMap()
    {
        int n = _textureWidth * _textureHeight;
        _pixels = new uint[n];
        _pixelBlock = new int[n];
        _pixelGap = new bool[n];
        _staticColor = new uint[n];

        for (int y = 0; y < _textureHeight; y++)
        {
            for (int x = 0; x < _textureWidth; x++)
            {
                int i = y * _textureWidth + x;
                double dx = (x + 0.5) / _textureWidth * 2 - 1;
                double dy = (y + 0.5) / _textureHeight * 2 - 1;
                double r = Math.Sqrt(dx * dx + dy * dy);
                double theta = Math.Atan2(dy, dx);
                if (theta < 0) theta += Math.PI * 2;

                // Brushed-metal look for everything that isn't data.
                double grain = 0.5 + 0.5 * Math.Sin(theta * 3 + r * 40) * 0.15 + (r * 997 % 1) * 0.08;
                byte metal = (byte)Math.Clamp(58 + 40 * grain + 22 * r, 0, 255);
                _staticColor[i] = Argb(metal, (byte)(metal + 4 > 255 ? 255 : metal + 4), (byte)Math.Min(255, metal + 14));
                _pixelBlock[i] = -1;

                var cell = _geometry.CellAt(r, theta);
                if (cell.Ring < 0) continue;
                _pixelGap[i] = cell.Gap;
                if (cell.Fraction >= _geometry.SpanStart && cell.Fraction < _geometry.SpanEnd)
                {
                    int block = (int)((cell.Fraction - _geometry.SpanStart) / (_geometry.SpanEnd - _geometry.SpanStart) * _blockCount);
                    _pixelBlock[i] = Math.Clamp(block, 0, _blockCount - 1);
                }
                else
                {
                    // Other partitions on the same disk.
                    _staticColor[i] = cell.Gap ? 0xFF0A101Cu : 0xFF141E30u;
                }
            }
        }
    }

    private void BuildBoardPixelMap()
    {
        int n = _textureWidth * _textureHeight;
        _pixels = new uint[n];
        _pixelBlock = new int[n];
        _pixelGap = new bool[n];
        _staticColor = new uint[n];
        _grid = GridGeometry.Fit(_blockCount, _textureWidth, _textureHeight);

        for (int y = 0; y < _textureHeight; y++)
        {
            for (int x = 0; x < _textureWidth; x++)
            {
                int i = y * _textureWidth + x;
                _staticColor[i] = 0xFF0B1A1Cu;
                var (block, gap) = _grid.BlockAt(x, y, _textureWidth, _textureHeight);
                _pixelBlock[i] = block;
                _pixelGap[i] = gap;
            }
        }
    }

    private void RefreshTexture()
    {
        if (_texture == null) return;

        if (_colorsDirty)
        {
            for (int b = 0; b < _blockCount; b++)
            {
                uint c = _health != null
                    ? MapPalette.HealthColor(_health[b], _activity[b])
                    : MapPalette.Color(_used[b], _fragmented?[b] ?? 0, _metadata?[b] ?? 0, _unmovable?[b] ?? 0, _activity[b]);
                _blockColor[b] = c;
                _blockColorGap[b] = MapPalette.Darken(c, 0.45);
            }
            _colorsDirty = false;
            _textureDirty = true;
        }

        if (!_textureDirty && _sweep == null) return;

        int sweepX = -1000;
        if (_layout == DiskSceneLayout.Board && _sweep is double s) sweepX = (int)(s * _textureWidth);

        for (int i = 0; i < _pixels.Length; i++)
        {
            int b = _pixelBlock[i];
            uint c = b >= 0 ? (_pixelGap[i] ? _blockColorGap[b] : _blockColor[b]) : _staticColor[i];
            if (sweepX > -1000)
            {
                int d = Math.Abs(i % _textureWidth - sweepX);
                if (d < 18) c = MapPalette.Blend(c, 0xFFB8F2FFu, (18 - d) / 22f);
            }
            _pixels[i] = c;
        }
        _texture.WritePixels(new Int32Rect(0, 0, _textureWidth, _textureHeight), _pixels, _textureWidth * 4, 0);
        _textureDirty = false;
    }

    private static uint Argb(byte r, byte g, byte b) => 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | b;

    #endregion

    #region Animation

    private void StartRendering()
    {
        if (_rendering || !IsLoaded || !IsVisible) return;
        _rendering = true;
        CompositionTarget.Rendering += OnRendering;
    }

    private void StopRendering()
    {
        if (!_rendering) return;
        _rendering = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        double now = _clock.Elapsed.TotalSeconds;
        double dt = now - _lastFrame;
        if (dt < 1 / 50.0) return;
        _lastFrame = now;
        dt = Math.Min(dt, 0.1);

        if (_layout == DiskSceneLayout.Platter)
        {
            _spinAngle = (_spinAngle + (Busy ? 150 : 22) * dt) % 360;
            _spinRotation.Angle = _spinAngle;
            AnimateHead(now, dt);
        }
        else
        {
            AnimateBoardLight(now);
        }

        // Gentle camera sway when nobody is dragging.
        if (_dragFrom == null && now - _lastInteraction > 6)
        {
            _yaw += Math.Sin(now * 0.18) * 2.2 * dt;
            UpdateCamera();
        }

        AnimateParticles(now, dt);

        // Fade the glow of blocks that were touched.
        bool anyActive = false;
        float decay = (float)Math.Pow(0.32, dt);
        for (int b = 0; b < _activity.Length; b++)
        {
            if (_activity[b] <= 0) continue;
            _activity[b] *= decay;
            if (_activity[b] < 0.02f) _activity[b] = 0;
            anyActive = true;
        }
        if (anyActive) _colorsDirty = true;

        if (now - _lastTextureFrame >= 1 / 20.0 && (_colorsDirty || _textureDirty || _sweep != null))
        {
            _lastTextureFrame = now;
            RefreshTexture();
        }
    }

    private void AnimateHead(double now, double dt)
    {
        if (_seekQueue.Count > 0 && now >= _nextSeekAt)
        {
            _headTarget = _seekQueue.Dequeue();
            _nextSeekAt = now + 0.22 + _random.NextDouble() * 0.18;
        }
        else if (_seekQueue.Count == 0 && !Busy && now >= _nextSeekAt + 2.5)
        {
            _headTarget = ParkRadius;
        }

        // Critically damped spring: a quick seek that settles without wobble.
        const double stiffness = 140;
        double damping = 2 * Math.Sqrt(stiffness);
        double accel = stiffness * (_headTarget - _headRadius) - damping * _headVelocity;
        _headVelocity += accel * dt;
        _headRadius += _headVelocity * dt;
        SetArmForRadius(_headRadius);

        double brightness = Busy ? 1.0 : 0.45;
        _headLight.Color = Color.FromRgb((byte)(0x5c * brightness), (byte)(0xd8 * brightness), (byte)(0xff * brightness));
    }

    private void SetArmForRadius(double radius)
    {
        // Head position = pivot + L·(cos α, sin α); its distance from the platter centre must be `radius`.
        double cx = PlatterCenter.X - ArmPivot.X, cz = PlatterCenter.Z - ArmPivot.Z;
        double d = Math.Sqrt(cx * cx + cz * cz);
        double phi = Math.Atan2(cz, cx);
        double cosBeta = Math.Clamp((ArmLength * ArmLength + d * d - radius * radius) / (2 * ArmLength * d), -1, 1);
        double alpha = phi - Math.Acos(cosBeta);
        _armRotation.Angle = -alpha * 180 / Math.PI;

        var head = new Point3D(ArmPivot.X + ArmLength * Math.Cos(alpha), 0.75, ArmPivot.Z + ArmLength * Math.Sin(alpha));
        _headLight.Position = head;
    }

    private void AnimateBoardLight(double now)
    {
        double x = _sweep is double s
            ? _boardOrigin.X + s * _boardWidth
            : _boardOrigin.X + (0.5 + 0.45 * Math.Sin(now * 0.7)) * _boardWidth;
        _headLight.Position = new Point3D(x, 0.9, 0);
        double brightness = Busy || _sweep != null ? 1.0 : 0.4;
        _headLight.Color = Color.FromRgb((byte)(0x5c * brightness), (byte)(0xd8 * brightness), (byte)(0xff * brightness));
    }

    #endregion

    #region Particles (data that moved)

    private sealed class Particle
    {
        public required GeometryModel3D Model { get; init; }
        public required TranslateTransform3D Position { get; init; }
        public Point3D From, To;
        public double Start, Duration, Height;
        public int TargetBlock;
    }

    private static readonly MeshGeometry3D ParticleMesh = Mesh.Box(-0.045, 0.045, -0.045, 0.045, -0.045, 0.045);
    private static readonly Material SourceGlow = Frozen(new EmissiveMaterial(new SolidColorBrush(Color.FromRgb(0xff, 0xc8, 0x57))));

    private static Material Frozen(Material m)
    {
        m.Freeze();
        return m;
    }

    private void SpawnParticle(int from, int to, double delay)
    {
        if ((uint)from >= (uint)_blockCount || (uint)to >= (uint)_blockCount || _particles.Count > 90) return;
        var transform = new TranslateTransform3D();
        var model = new GeometryModel3D(ParticleMesh, SourceGlow) { Transform = transform };
        var a = BlockPoint(from);
        var b = BlockPoint(to);
        var particle = new Particle
        {
            Model = model,
            Position = transform,
            From = a,
            To = b,
            Start = _clock.Elapsed.TotalSeconds + delay,
            Duration = 0.75 + _random.NextDouble() * 0.35,
            Height = 0.45 + (a - b).Length * 0.18,
            TargetBlock = to
        };
        transform.OffsetX = a.X;
        transform.OffsetY = a.Y;
        transform.OffsetZ = a.Z;
        _particles.Add(particle);
        _particleGroup.Children.Add(model);
    }

    private void AnimateParticles(double now, double dt)
    {
        for (int i = _particles.Count - 1; i >= 0; i--)
        {
            var p = _particles[i];
            double t = (now - p.Start) / p.Duration;
            if (t < 0) continue;
            if (t >= 1)
            {
                Flash(p.TargetBlock, 1f);
                _particleGroup.Children.Remove(p.Model);
                _particles.RemoveAt(i);
                continue;
            }
            double ease = t * t * (3 - 2 * t);
            p.Position.OffsetX = p.From.X + (p.To.X - p.From.X) * ease;
            p.Position.OffsetZ = p.From.Z + (p.To.Z - p.From.Z) * ease;
            p.Position.OffsetY = p.From.Y + (p.To.Y - p.From.Y) * ease + Math.Sin(Math.PI * t) * p.Height;
        }
    }

    /// <summary>Where a block is drawn, in the coordinates of the spinning platter (or the board).</summary>
    private Point3D BlockPoint(int block)
    {
        if (_layout == DiskSceneLayout.Platter)
        {
            var (r, theta) = _geometry.PolarOf(block, _blockCount);
            return new Point3D(PlatterCenter.X + r * PlatterRadius * Math.Cos(theta), PlatterCenter.Y + 0.05,
                PlatterCenter.Z + r * PlatterRadius * Math.Sin(theta));
        }
        var (u, v) = _grid.CenterOf(block, _textureWidth, _textureHeight);
        return new Point3D(_boardOrigin.X + u * _boardWidth, _boardOrigin.Y + 0.05, _boardOrigin.Z + v * _boardDepth);
    }

    #endregion

    #region Camera

    private void UpdateCamera()
    {
        var target = new Point3D(0, -0.4, _layout == DiskSceneLayout.Platter ? 0.55 : 0);
        double yaw = _yaw * Math.PI / 180, pitch = _pitch * Math.PI / 180;
        var offset = new Vector3D(
            _distance * Math.Cos(pitch) * Math.Sin(yaw),
            _distance * Math.Sin(pitch),
            _distance * Math.Cos(pitch) * Math.Cos(yaw));
        _camera.Position = target + offset;
        _camera.LookDirection = -offset;
        _camera.UpDirection = new Vector3D(0, 1, 0);
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ResetView();
            return;
        }
        _dragFrom = e.GetPosition(this);
        CaptureMouse();
        _lastInteraction = _clock.Elapsed.TotalSeconds;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragFrom is not Point from) return;
        var now = e.GetPosition(this);
        _yaw -= (now.X - from.X) * 0.35;
        _pitch = Math.Clamp(_pitch + (now.Y - from.Y) * 0.25, 12, 88);
        _dragFrom = now;
        _lastInteraction = _clock.Elapsed.TotalSeconds;
        UpdateCamera();
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        _dragFrom = null;
        ReleaseMouseCapture();
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        _distance = Math.Clamp(_distance * (e.Delta > 0 ? 0.9 : 1.1), 6.5, 24);
        _lastInteraction = _clock.Elapsed.TotalSeconds;
        UpdateCamera();
        e.Handled = true;
    }

    #endregion
}

/// <summary>
/// How the volume is laid out on a platter: tracks from the outer edge
/// inwards (where a hard disk starts), each holding data in proportion to its
/// circumference — so outer tracks hold more, like real zoned recording.
/// </summary>
internal sealed class PlatterGeometry
{
    public const double InnerRadius = 0.27, OuterRadius = 0.965;
    public const int Rings = 30;
    private const int SectorsOnOuterRing = 120;

    private readonly double[] _ringStart = new double[Rings];
    private readonly double[] _ringShare = new double[Rings];

    public double SpanStart { get; }
    public double SpanEnd { get; }

    public PlatterGeometry(double spanStart, double spanEnd)
    {
        SpanStart = spanStart;
        SpanEnd = spanEnd;
        double total = 0;
        for (int k = 0; k < Rings; k++) total += MidRadius(k);
        double acc = 0;
        for (int k = 0; k < Rings; k++)
        {
            _ringStart[k] = acc;
            _ringShare[k] = MidRadius(k) / total;
            acc += _ringShare[k];
        }
    }

    private static double RingWidth => (OuterRadius - InnerRadius) / Rings;
    private static double MidRadius(int ring) => OuterRadius - (ring + 0.5) * RingWidth;

    /// <summary>Which ring and disk fraction a texture point (normalized radius, angle) shows.</summary>
    public (int Ring, double Fraction, bool Gap) CellAt(double r, double theta)
    {
        if (r < InnerRadius || r >= OuterRadius) return (-1, 0, false);
        double ringPos = (OuterRadius - r) / RingWidth;
        int ring = Math.Min(Rings - 1, (int)ringPos);
        double within = ringPos - ring;
        double pos = theta / (2 * Math.PI);
        int sectors = Math.Max(24, (int)(SectorsOnOuterRing * MidRadius(ring) / OuterRadius));
        double sectorPos = pos * sectors;
        bool gap = within < 0.12 || sectorPos - Math.Floor(sectorPos) < 0.06;
        return (ring, _ringStart[ring] + pos * _ringShare[ring], gap);
    }

    /// <summary>Normalized radius and angle of the middle of a volume block.</summary>
    public (double R, double Theta) PolarOf(int block, int blockCount)
    {
        double f = SpanStart + (SpanEnd - SpanStart) * (block + 0.5) / blockCount;
        int ring = Rings - 1;
        for (int k = 0; k < Rings; k++)
        {
            if (f < _ringStart[k] + _ringShare[k]) { ring = k; break; }
        }
        double pos = Math.Clamp((f - _ringStart[ring]) / _ringShare[ring], 0, 1);
        return (MidRadius(ring), pos * 2 * Math.PI);
    }

    public double RadiusOf(int block, int blockCount) => PolarOf(block, blockCount).R;

    public double RadiusOfVolumeFraction(double fraction)
    {
        double f = SpanStart + (SpanEnd - SpanStart) * fraction;
        for (int k = 0; k < Rings; k++)
            if (f < _ringStart[k] + _ringShare[k]) return MidRadius(k);
        return MidRadius(Rings - 1);
    }
}

/// <summary>Blocks drawn as a grid of cells (the SSD board and the flat map).</summary>
internal sealed class GridGeometry
{
    public int Columns { get; }
    public int Rows { get; }
    public int BlockCount { get; }

    public GridGeometry(int columns, int rows, int blockCount)
    {
        Columns = Math.Max(1, columns);
        Rows = Math.Max(1, rows);
        BlockCount = Math.Max(1, blockCount);
    }

    public static GridGeometry Fit(int blockCount, int width, int height)
    {
        double aspect = width / (double)height;
        int columns = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(blockCount * aspect)));
        int rows = Math.Max(1, (blockCount + columns - 1) / columns);
        return new GridGeometry(columns, rows, blockCount);
    }

    public (int Block, bool Gap) BlockAt(int x, int y, int width, int height)
    {
        double cellW = width / (double)Columns, cellH = height / (double)Rows;
        int cx = Math.Min(Columns - 1, (int)(x / cellW));
        int cy = Math.Min(Rows - 1, (int)(y / cellH));
        int block = cy * Columns + cx;
        if (block >= BlockCount) return (-1, false);
        bool gap = cellW >= 4 && x - cx * cellW < 1 || cellH >= 4 && y - cy * cellH < 1;
        return (block, gap);
    }

    public (double U, double V) CenterOf(int block, int width, int height) =>
        (((block % Columns) + 0.5) / Columns, ((block / Columns) + 0.5) / Rows);
}

/// <summary>Colours of the allocation map (shared by the 3D view and the flat map).</summary>
public static class MapPalette
{
    public const uint Free = 0xFF0E1A31;
    public const uint UsedLow = 0xFF1B4F94;
    public const uint UsedHigh = 0xFF35A6FF;
    public const uint Fragmented = 0xFFFF6A3D;
    public const uint Metadata = 0xFFB46CFF;
    public const uint Unmovable = 0xFFE2E8F0;
    public const uint Glow = 0xFFFFF1B8;

    public static uint Color(float used, float fragmented, float metadata, float unmovable, float activity)
    {
        uint c;
        if (used <= 0.002f)
        {
            c = Free;
        }
        else
        {
            c = Blend(UsedLow, UsedHigh, Math.Clamp(used, 0, 1));
            if (metadata > 0.04f) c = Blend(c, Metadata, Math.Min(1f, 0.5f + metadata));
            else if (unmovable > 0.04f) c = Blend(c, Unmovable, Math.Min(1f, 0.45f + unmovable));
            // Tint by the share of the block's data that sits in fragmented files.
            float share = fragmented / Math.Max(used, 0.01f);
            if (share >= 0.08f) c = Blend(c, Fragmented, Math.Clamp(share * 1.1f, 0.3f, 1f));
        }
        if (activity > 0.01f) c = Blend(c, Glow, Math.Min(1f, activity));
        return c;
    }

    public const uint Untested = 0xFF101A2C;
    public const uint HealthGood = 0xFF1E9E72;
    public const uint HealthSlow = 0xFFE5C440;
    public const uint HealthVerySlow = 0xFFFF8A3D;
    public const uint HealthError = 0xFFFF3B4E;

    /// <summary>Surface-scan colours: untested, good, slow, very slow, unreadable.</summary>
    public static uint HealthColor(byte health, float activity)
    {
        uint c = health switch
        {
            1 => HealthGood,
            2 => HealthSlow,
            3 => HealthVerySlow,
            4 => HealthError,
            _ => Untested
        };
        return activity > 0.01f ? Blend(c, Glow, Math.Min(1f, activity)) : c;
    }

    public static uint Blend(uint a, uint b, float t)
    {
        t = Math.Clamp(t, 0, 1);
        uint r = (uint)(((a >> 16) & 0xFF) + ((((b >> 16) & 0xFF) - (float)((a >> 16) & 0xFF)) * t));
        uint g = (uint)(((a >> 8) & 0xFF) + ((((b >> 8) & 0xFF) - (float)((a >> 8) & 0xFF)) * t));
        uint bl = (uint)((a & 0xFF) + (((b & 0xFF) - (float)(a & 0xFF)) * t));
        return 0xFF000000u | (r << 16) | (g << 8) | bl;
    }

    public static uint Darken(uint c, double factor)
    {
        uint r = (uint)(((c >> 16) & 0xFF) * factor), g = (uint)(((c >> 8) & 0xFF) * factor), b = (uint)((c & 0xFF) * factor);
        return 0xFF000000u | (r << 16) | (g << 8) | b;
    }
}

/// <summary>Small mesh builders for the drive models.</summary>
internal static class Mesh
{
    public static MeshGeometry3D Box(double x0, double x1, double y0, double y1, double z0, double z1)
    {
        var m = new MeshGeometry3D();
        void Face(Point3D a, Point3D b, Point3D c, Point3D d)
        {
            int i = m.Positions.Count;
            m.Positions.Add(a); m.Positions.Add(b); m.Positions.Add(c); m.Positions.Add(d);
            m.TriangleIndices.Add(i); m.TriangleIndices.Add(i + 1); m.TriangleIndices.Add(i + 2);
            m.TriangleIndices.Add(i); m.TriangleIndices.Add(i + 2); m.TriangleIndices.Add(i + 3);
        }
        Face(new(x0, y1, z0), new(x0, y1, z1), new(x1, y1, z1), new(x1, y1, z0)); // top
        Face(new(x0, y0, z0), new(x1, y0, z0), new(x1, y0, z1), new(x0, y0, z1)); // bottom
        Face(new(x0, y0, z1), new(x1, y0, z1), new(x1, y1, z1), new(x0, y1, z1)); // front
        Face(new(x1, y0, z0), new(x0, y0, z0), new(x0, y1, z0), new(x1, y1, z0)); // back
        Face(new(x0, y0, z0), new(x0, y0, z1), new(x0, y1, z1), new(x0, y1, z0)); // left
        Face(new(x1, y0, z1), new(x1, y0, z0), new(x1, y1, z0), new(x1, y1, z1)); // right
        m.Freeze();
        return m;
    }

    public static MeshGeometry3D Cylinder(Point3D baseCenter, double radius, double height, int segments, bool capTop = true)
    {
        var m = new MeshGeometry3D();
        double y0 = baseCenter.Y, y1 = baseCenter.Y + height;
        for (int s = 0; s <= segments; s++)
        {
            double a = s * 2 * Math.PI / segments;
            double x = baseCenter.X + radius * Math.Cos(a), z = baseCenter.Z + radius * Math.Sin(a);
            m.Positions.Add(new Point3D(x, y0, z));
            m.Positions.Add(new Point3D(x, y1, z));
        }
        for (int s = 0; s < segments; s++)
        {
            int i = s * 2;
            m.TriangleIndices.Add(i); m.TriangleIndices.Add(i + 1); m.TriangleIndices.Add(i + 2);
            m.TriangleIndices.Add(i + 2); m.TriangleIndices.Add(i + 1); m.TriangleIndices.Add(i + 3);
        }
        if (capTop)
        {
            int center = m.Positions.Count;
            m.Positions.Add(new Point3D(baseCenter.X, y1, baseCenter.Z));
            int first = m.Positions.Count;
            for (int s = 0; s <= segments; s++)
            {
                double a = s * 2 * Math.PI / segments;
                m.Positions.Add(new Point3D(baseCenter.X + radius * Math.Cos(a), y1, baseCenter.Z + radius * Math.Sin(a)));
            }
            for (int s = 0; s < segments; s++)
            {
                m.TriangleIndices.Add(center); m.TriangleIndices.Add(first + s + 1); m.TriangleIndices.Add(first + s);
            }
        }
        m.Freeze();
        return m;
    }

    /// <summary>A flat ring wall (outer and inner surfaces plus top).</summary>
    public static MeshGeometry3D Ring(Point3D center, double rIn, double rOut, double y0, double y1, int segments)
    {
        var m = new MeshGeometry3D();
        for (int s = 0; s < segments; s++)
        {
            double a0 = s * 2 * Math.PI / segments, a1 = (s + 1) * 2 * Math.PI / segments;
            Point3D P(double r, double a, double y) => new(center.X + r * Math.Cos(a), y, center.Z + r * Math.Sin(a));
            void Quad(Point3D a, Point3D b, Point3D c, Point3D d)
            {
                int i = m.Positions.Count;
                m.Positions.Add(a); m.Positions.Add(b); m.Positions.Add(c); m.Positions.Add(d);
                m.TriangleIndices.Add(i); m.TriangleIndices.Add(i + 1); m.TriangleIndices.Add(i + 2);
                m.TriangleIndices.Add(i); m.TriangleIndices.Add(i + 2); m.TriangleIndices.Add(i + 3);
            }
            Quad(P(rIn, a0, y1), P(rIn, a1, y1), P(rOut, a1, y1), P(rOut, a0, y1));
            Quad(P(rIn, a0, y0), P(rIn, a1, y0), P(rIn, a1, y1), P(rIn, a0, y1));
            Quad(P(rOut, a0, y1), P(rOut, a1, y1), P(rOut, a1, y0), P(rOut, a0, y0));
        }
        m.Freeze();
        return m;
    }

    /// <summary>The platter's top face, texture-mapped so (u,v) = (0.5 + x/2R, 0.5 + z/2R).</summary>
    public static MeshGeometry3D Disc(Point3D center, double radius, int segments)
    {
        var m = new MeshGeometry3D();
        m.Positions.Add(center);
        m.TextureCoordinates.Add(new Point(0.5, 0.5));
        for (int s = 0; s <= segments; s++)
        {
            double a = s * 2 * Math.PI / segments;
            double x = Math.Cos(a), z = Math.Sin(a);
            m.Positions.Add(new Point3D(center.X + radius * x, center.Y, center.Z + radius * z));
            m.TextureCoordinates.Add(new Point(0.5 + x / 2, 0.5 + z / 2));
        }
        for (int s = 0; s < segments; s++)
        {
            m.TriangleIndices.Add(0); m.TriangleIndices.Add(s + 2); m.TriangleIndices.Add(s + 1);
        }
        m.Freeze();
        return m;
    }

    /// <summary>A flat rectangle on the XZ plane with the whole texture on it.</summary>
    public static MeshGeometry3D TexturedQuad(Point3D origin, double width, double depth)
    {
        var m = new MeshGeometry3D();
        m.Positions.Add(new Point3D(origin.X, origin.Y, origin.Z));
        m.Positions.Add(new Point3D(origin.X, origin.Y, origin.Z + depth));
        m.Positions.Add(new Point3D(origin.X + width, origin.Y, origin.Z + depth));
        m.Positions.Add(new Point3D(origin.X + width, origin.Y, origin.Z));
        m.TextureCoordinates.Add(new Point(0, 0));
        m.TextureCoordinates.Add(new Point(0, 1));
        m.TextureCoordinates.Add(new Point(1, 1));
        m.TextureCoordinates.Add(new Point(1, 0));
        m.TriangleIndices.Add(0); m.TriangleIndices.Add(1); m.TriangleIndices.Add(2);
        m.TriangleIndices.Add(0); m.TriangleIndices.Add(2); m.TriangleIndices.Add(3);
        m.Freeze();
        return m;
    }

    /// <summary>A tapered actuator arm along +X from the pivot, as a thin prism.</summary>
    public static MeshGeometry3D Arm(double length)
    {
        var outline = new (double X, double Z)[]
        {
            (-0.45, -0.32), (0.55, -0.26), (length - 0.35, -0.07), (length + 0.05, -0.05),
            (length + 0.05, 0.05), (length - 0.35, 0.07), (0.55, 0.26), (-0.45, 0.32)
        };
        const double y0 = 0.02, y1 = 0.1;
        var m = new MeshGeometry3D();
        int n = outline.Length;
        // Top and bottom as triangle fans around the first vertex (the outline is convex).
        foreach (var y in new[] { y1, y0 })
        {
            int first = m.Positions.Count;
            foreach (var (x, z) in outline) m.Positions.Add(new Point3D(x, y, z));
            for (int i = 1; i < n - 1; i++)
            {
                m.TriangleIndices.Add(first); m.TriangleIndices.Add(first + i); m.TriangleIndices.Add(first + i + 1);
            }
        }
        for (int i = 0; i < n; i++)
        {
            var (ax, az) = outline[i];
            var (bx, bz) = outline[(i + 1) % n];
            int k = m.Positions.Count;
            m.Positions.Add(new Point3D(ax, y0, az)); m.Positions.Add(new Point3D(bx, y0, bz));
            m.Positions.Add(new Point3D(bx, y1, bz)); m.Positions.Add(new Point3D(ax, y1, az));
            m.TriangleIndices.Add(k); m.TriangleIndices.Add(k + 1); m.TriangleIndices.Add(k + 2);
            m.TriangleIndices.Add(k); m.TriangleIndices.Add(k + 2); m.TriangleIndices.Add(k + 3);
        }
        m.Freeze();
        return m;
    }
}
