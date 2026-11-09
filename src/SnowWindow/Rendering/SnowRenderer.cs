using System.Numerics;
using SharpGen.Runtime;
using SnowWindow.App;
using SnowWindow.Simulation;
using Vortice;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;
using Vortice.Mathematics;
using BitmapInterpolationMode = Vortice.Direct2D1.BitmapInterpolationMode;
using FeatureLevel = Vortice.Direct3D.FeatureLevel;

namespace SnowWindow.Rendering;

/// <summary>
/// GPU로 눈을 그린다. 모니터마다 투명 오버레이 창 + 컴포지션 스왑 체인을 두고,
/// 시뮬레이션 상태(가상 화면 좌표)를 각 모니터 위치만큼 옮겨 그린다.
/// 장치 손실 등 그래픽 오류는 예외로 올려 보내며, 호출자가 이 객체를 버리고 새로 만든다.
/// </summary>
internal sealed class SnowRenderer : IDisposable
{
    private const float MinVisibleHeight = 0.35f;
    private const float SpriteScale = 1.4f; // 스프라이트 지름 / 눈송이 지름(테두리 여유)

    private readonly ID2D1Factory1 _d2dFactory;
    private readonly ID3D11Device _d3d;
    private readonly IDXGIDevice1 _dxgi;
    private readonly IDXGIFactory2 _dxgiFactory;
    private readonly ID2D1Device _d2dDevice;
    private readonly ID2D1DeviceContext3 _ctx;
    private readonly IDCompositionDevice _dcomp;
    private readonly FlakeAtlas _atlas = new();
    private readonly ID2D1Bitmap1 _atlasBitmap;
    private readonly ID2D1SpriteBatch _batch;
    private readonly ID2D1LinearGradientBrush _pileBrush;
    private readonly ID2D1SolidColorBrush _rimBrush;

    private readonly List<Overlay> _overlays = new();
    private readonly List<PileShape> _shapes = new();
    private readonly Dictionary<int, Vector2[]> _pointArrays = new();
    private readonly RawRectF[] _dest = new RawRectF[SnowSimulation.MaxParticles];
    private readonly RawRect[] _src = new RawRect[SnowSimulation.MaxParticles];
    private readonly Color4[] _colors = new Color4[SnowSimulation.MaxParticles];

    public SnowRenderer()
    {
        _d2dFactory = D2D1.D2D1CreateFactory<ID2D1Factory1>(FactoryType.SingleThreaded, DebugLevel.None);
        _d3d = CreateD3DDevice();
        _dxgi = _d3d.QueryInterface<IDXGIDevice1>();
        _dxgi.MaximumFrameLatency = 1;
        using (var adapter = _dxgi.GetAdapter())
        {
            _dxgiFactory = adapter.GetParent<IDXGIFactory2>();
        }

        _d2dDevice = _d2dFactory.CreateDevice(_dxgi);
        using (var ctx = _d2dDevice.CreateDeviceContext(DeviceContextOptions.None))
        {
            _ctx = ctx.QueryInterface<ID2D1DeviceContext3>(); // 스프라이트 배치: Windows 10 1607 이상
        }

        _dcomp = DComp.DCompositionCreateDevice<IDCompositionDevice>(_dxgi);

        unsafe
        {
            fixed (byte* pixels = _atlas.Pixels)
            {
                _atlasBitmap = _ctx.CreateBitmap(
                    new SizeI(_atlas.Width, _atlas.Height), (IntPtr)pixels, (uint)(_atlas.Width * 4),
                    new BitmapProperties1(new PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied), 96, 96));
            }
        }

        _batch = _ctx.CreateSpriteBatch();

        var stops = new[]
        {
            new GradientStop(0f, new Color4(1f, 1f, 1f, 0.97f)),
            new GradientStop(1f, new Color4(0.80f, 0.86f, 0.94f, 0.97f)),
        };
        using (var collection = _ctx.CreateGradientStopCollection(stops))
        {
            _pileBrush = _ctx.CreateLinearGradientBrush(new LinearGradientBrushProperties(Vector2.Zero, Vector2.UnitY), collection);
        }

        _rimBrush = _ctx.CreateSolidColorBrush(new Color4(0.40f, 0.48f, 0.60f, 0.55f));
    }

    public void SetMonitors(IReadOnlyList<MonitorInfo> monitors)
    {
        DisposeOverlays();
        foreach (var m in monitors)
        {
            _overlays.Add(CreateOverlay(m));
        }
    }

    /// <summary>다른 '항상 위' 창에 밀려나지 않도록 주기적으로 맨 위로 올린다.</summary>
    public void KeepOnTop()
    {
        foreach (var ov in _overlays)
        {
            if (ov.Shown)
            {
                OverlayWindow.BringToTop(ov.Hwnd);
            }
        }
    }

    public void Render(SnowSimulation sim, FrameProfiler profiler)
    {
        long t = FrameProfiler.Now();
        int spriteCount = BuildSprites(sim);
        _batch.Clear();
        if (spriteCount > 0)
        {
            _batch.AddSprites((uint)spriteCount, _dest, _src, _colors, null!, 16, 16, 16, 0);
        }

        BuildShapes(sim);
        t = profiler.Add(FrameProfiler.Prepare, t);
        try
        {
            for (int i = 0; i < _overlays.Count; i++)
            {
                var ov = _overlays[i];
                if (i < sim.MonitorCount && sim.IsSuspended(i))
                {
                    if (ov.Shown)
                    {
                        OverlayWindow.Hide(ov.Hwnd);
                        ov.Shown = false;
                    }

                    continue;
                }

                t = DrawOverlay(ov, spriteCount, profiler, t);
                if (!ov.Shown)
                {
                    OverlayWindow.Show(ov.Hwnd); // 첫 프레임을 그린 뒤에 보여서 쓰레기 화면이 비치지 않게 한다
                    ov.Shown = true;
                }
            }
        }
        finally
        {
            foreach (var shape in _shapes)
            {
                shape.Geometry.Dispose();
            }

            _shapes.Clear();
        }
    }

    public void Dispose()
    {
        DisposeOverlays();
        _rimBrush.Dispose();
        _pileBrush.Dispose();
        _batch.Dispose();
        _atlasBitmap.Dispose();
        _dcomp.Dispose();
        _ctx.Dispose();
        _d2dDevice.Dispose();
        _dxgiFactory.Dispose();
        _dxgi.Dispose();
        _d3d.Dispose();
        _d2dFactory.Dispose();
    }

    private long DrawOverlay(Overlay ov, int spriteCount, FrameProfiler profiler, long t)
    {
        var b = ov.Info.Bounds;
        var world = Matrix3x2.CreateTranslation(-b.Left, -b.Top);
        var lifted = Matrix3x2.CreateTranslation(-b.Left, -b.Top - 1f);

        _ctx.Target = ov.Target;
        _ctx.BeginDraw();
        _ctx.Clear(new Color4(0f, 0f, 0f, 0f));
        _ctx.AntialiasMode = AntialiasMode.PerPrimitive;
        foreach (var shape in _shapes)
        {
            if (shape.Right < b.Left || shape.Left > b.Right || shape.Base < b.Top || shape.Top > b.Bottom)
            {
                continue;
            }

            // 1px 위로 옮긴 어두운 모양을 먼저 깔면, 윗면 윤곽에만 테두리가 생긴다(흰 배경에서도 보이게).
            _ctx.Transform = lifted;
            _ctx.FillGeometry(shape.Geometry, _rimBrush);
            _ctx.Transform = world;
            _pileBrush.StartPoint = new Vector2(0f, shape.Base - shape.MaxHeight);
            _pileBrush.EndPoint = new Vector2(0f, shape.Base);
            _ctx.FillGeometry(shape.Geometry, _pileBrush);
        }

        _ctx.Transform = world;
        t = profiler.Add(FrameProfiler.Piles, t);
        if (spriteCount > 0)
        {
            _ctx.AntialiasMode = AntialiasMode.Aliased; // 스프라이트 배치 필수 조건
            _ctx.DrawSpriteBatch(_batch, 0, (uint)spriteCount, _atlasBitmap, BitmapInterpolationMode.Linear, SpriteOptions.None);
        }

        t = profiler.Add(FrameProfiler.Sprites, t);
        Result end = _ctx.EndDraw();
        _ctx.Target = null;
        end.CheckError();
        t = profiler.Add(FrameProfiler.EndDraw, t);
        ov.SwapChain.Present(0, PresentFlags.None).CheckError();
        return profiler.Add(FrameProfiler.Present, t);
    }

    private int BuildSprites(SnowSimulation sim)
    {
        var particles = sim.Particles;
        int n = 0;
        foreach (ref readonly var p in particles)
        {
            float d = p.Radius * 2f * SpriteScale;
            float h = d * 0.5f;
            var (l, t, r, bottom) = _atlas.SourceFor(d);
            _dest[n] = new RawRectF(p.X - h, p.Y - h, p.X + h, p.Y + h);
            _src[n] = new RawRect(l, t, r, bottom);
            _colors[n] = new Color4(1f, 1f, 1f, p.Alpha);
            n++;
        }

        return n;
    }

    private void BuildShapes(SnowSimulation sim)
    {
        for (int m = 0; m < sim.MonitorCount; m++)
        {
            AddPileShape(sim.GetGround(m));
        }

        var piles = sim.WindowPilesTopFirst;
        for (int i = piles.Count - 1; i >= 0; i--)
        {
            AddPileShape(piles[i]); // 뒤쪽 창부터 그려 앞쪽 창의 눈이 위에 오게 한다
        }
    }

    private void AddPileShape(Pile pile)
    {
        if (!pile.Landable || !pile.HasSnow())
        {
            return;
        }

        ID2D1PathGeometry? geometry = null;
        ID2D1GeometrySink? sink = null;
        float top = pile.BaseY;
        try
        {
            foreach (var range in pile.Exposed)
            {
                int c = range.Start;
                while (c < range.End)
                {
                    while (c < range.End && DisplayHeight(pile, c) < MinVisibleHeight)
                    {
                        c++;
                    }

                    if (c >= range.End)
                    {
                        break;
                    }

                    int start = c;
                    while (c < range.End && DisplayHeight(pile, c) >= MinVisibleHeight)
                    {
                        c++;
                    }

                    geometry ??= _d2dFactory.CreatePathGeometry();
                    sink ??= geometry.Open();
                    var points = PointArray(c - start + 1);
                    for (int k = 0; k < c - start; k++)
                    {
                        float y = pile.BaseY - DisplayHeight(pile, start + k);
                        points[k] = new Vector2(pile.Left + (start + k + 0.5f) * pile.ColW, y);
                        top = MathF.Min(top, y);
                    }

                    points[^1] = new Vector2(pile.Left + c * pile.ColW, pile.BaseY);
                    sink.BeginFigure(new Vector2(pile.Left + start * pile.ColW, pile.BaseY), FigureBegin.Filled);
                    sink.AddLines(points);
                    sink.EndFigure(FigureEnd.Closed);
                }
            }

            if (sink != null)
            {
                sink.Close();
                _shapes.Add(new PileShape(geometry!, pile.Left, pile.Right, top - 2f, pile.BaseY, pile.MaxH));
                geometry = null;
            }
        }
        finally
        {
            sink?.Dispose();
            geometry?.Dispose();
        }
    }

    /// <summary>이웃 칸과 살짝 섞어 계단 없이 부드러운 윤곽을 만든다.</summary>
    private static float DisplayHeight(Pile pile, int c)
    {
        var h = pile.H;
        float l = h[Math.Max(0, c - 1)];
        float r = h[Math.Min(h.Length - 1, c + 1)];
        return 0.25f * l + 0.5f * h[c] + 0.25f * r;
    }

    private Vector2[] PointArray(int length)
    {
        if (!_pointArrays.TryGetValue(length, out var array))
        {
            if (_pointArrays.Count > 256)
            {
                _pointArrays.Clear(); // 창 크기가 계속 바뀌는 경우 무한히 쌓이지 않게
            }

            array = new Vector2[length];
            _pointArrays[length] = array;
        }

        return array;
    }

    private Overlay CreateOverlay(MonitorInfo m)
    {
        nint hwnd = OverlayWindow.Create(m.Bounds);
        IDXGISwapChain1? swapChain = null;
        IDCompositionTarget? target = null;
        IDCompositionVisual? visual = null;
        try
        {
            var desc = new SwapChainDescription1
            {
                Width = (uint)m.Bounds.Width,
                Height = (uint)m.Bounds.Height,
                Format = Format.B8G8R8A8_UNorm,
                Stereo = false,
                SampleDescription = new SampleDescription(1, 0),
                BufferUsage = Usage.RenderTargetOutput,
                BufferCount = 2,
                Scaling = Scaling.Stretch,
                SwapEffect = SwapEffect.FlipSequential,
                AlphaMode = Vortice.DXGI.AlphaMode.Premultiplied,
                Flags = SwapChainFlags.None,
            };
            swapChain = _dxgiFactory.CreateSwapChainForComposition(_d3d, desc, null);
            _dcomp.CreateTargetForHwnd(hwnd, true, out target).CheckError();
            visual = _dcomp.CreateVisual();
            visual.SetContent(swapChain).CheckError();
            target!.SetRoot(visual).CheckError();
            _dcomp.Commit().CheckError();

            using var surface = swapChain.GetBuffer<IDXGISurface>(0);
            var bitmap = _ctx.CreateBitmapFromDxgiSurface(surface, new BitmapProperties1(
                new PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied), 96, 96,
                BitmapOptions.Target | BitmapOptions.CannotDraw));
            return new Overlay(hwnd, m, swapChain, bitmap, target, visual);
        }
        catch
        {
            visual?.Dispose();
            target?.Dispose();
            swapChain?.Dispose();
            OverlayWindow.Destroy(hwnd);
            throw;
        }
    }

    private void DisposeOverlays()
    {
        foreach (var ov in _overlays)
        {
            ov.Dispose();
        }

        _overlays.Clear();
    }

    private static ID3D11Device CreateD3DDevice()
    {
        var levels = new[]
        {
            FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1,
            FeatureLevel.Level_10_0, FeatureLevel.Level_9_3,
        };
        Result hr = D3D11.D3D11CreateDevice(IntPtr.Zero, DriverType.Hardware, DeviceCreationFlags.BgraSupport, levels, out ID3D11Device? device);
        if (hr.Failure || device == null)
        {
            // GPU를 쓸 수 없는 환경(원격 세션 등)에서는 소프트웨어 렌더러로 대신한다.
            D3D11.D3D11CreateDevice(IntPtr.Zero, DriverType.Warp, DeviceCreationFlags.BgraSupport, levels, out device).CheckError();
        }

        return device!;
    }

    private sealed record PileShape(ID2D1PathGeometry Geometry, float Left, float Right, float Top, float Base, float MaxHeight);

    private sealed class Overlay(
        nint hwnd, MonitorInfo info, IDXGISwapChain1 swapChain, ID2D1Bitmap1 target,
        IDCompositionTarget compositionTarget, IDCompositionVisual visual) : IDisposable
    {
        public nint Hwnd { get; } = hwnd;
        public MonitorInfo Info { get; } = info;
        public IDXGISwapChain1 SwapChain { get; } = swapChain;
        public ID2D1Bitmap1 Target { get; } = target;
        public bool Shown { get; set; }

        public void Dispose()
        {
            Target.Dispose();
            visual.Dispose();
            compositionTarget.Dispose();
            SwapChain.Dispose();
            OverlayWindow.Destroy(Hwnd);
        }
    }
}
