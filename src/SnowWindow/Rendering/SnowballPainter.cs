using System.Numerics;
using SnowWindow.Simulation;
using Vortice.Direct2D1;
using Vortice.Mathematics;
using RectI = SnowWindow.Simulation.RectI;

namespace SnowWindow.Rendering;

/// <summary>
/// 눈덩이와 눈사람(머리: 숯 눈·당근 코, 몸통: 나뭇가지 팔)을 그린다.
/// 호출자가 가상 화면 좌표 → 모니터 좌표 변환을 ctx.Transform에 걸어 둔 상태로 부른다.
/// </summary>
internal sealed class SnowballPainter : IDisposable
{
    private readonly ID2D1RadialGradientBrush _fill;
    private readonly ID2D1SolidColorBrush _rim;
    private readonly ID2D1SolidColorBrush _coal;
    private readonly ID2D1SolidColorBrush _carrot;
    private readonly ID2D1SolidColorBrush _twig;
    private readonly ID2D1StrokeStyle _roundStroke;
    private readonly ID2D1PathGeometry _nose; // 반지름 1인 머리 기준 모양. 그릴 때 반지름만큼 키운다

    public SnowballPainter(ID2D1DeviceContext ctx, ID2D1Factory1 factory)
    {
        var stops = new[]
        {
            new GradientStop(0f, new Color4(1f, 1f, 1f, 0.98f)),
            new GradientStop(0.65f, new Color4(0.95f, 0.97f, 1f, 0.98f)),
            new GradientStop(1f, new Color4(0.76f, 0.83f, 0.93f, 0.98f)),
        };
        using (var collection = ctx.CreateGradientStopCollection(stops))
        {
            _fill = ctx.CreateRadialGradientBrush(
                new RadialGradientBrushProperties(Vector2.Zero, Vector2.Zero, 1f, 1f), collection);
        }

        _rim = ctx.CreateSolidColorBrush(new Color4(0.40f, 0.48f, 0.60f, 0.45f));
        _coal = ctx.CreateSolidColorBrush(new Color4(0.13f, 0.13f, 0.16f, 1f));
        _carrot = ctx.CreateSolidColorBrush(new Color4(0.96f, 0.52f, 0.13f, 1f));
        _twig = ctx.CreateSolidColorBrush(new Color4(0.40f, 0.27f, 0.16f, 1f));
        _roundStroke = factory.CreateStrokeStyle(new StrokeStyleProperties
        {
            StartCap = CapStyle.Round,
            EndCap = CapStyle.Round,
            LineJoin = LineJoin.Round,
        });

        _nose = factory.CreatePathGeometry();
        using var sink = _nose.Open();
        sink.BeginFigure(new Vector2(0f, -0.02f), FigureBegin.Filled);
        sink.AddLines(new[] { new Vector2(0.62f, 0.12f), new Vector2(0f, 0.2f) });
        sink.EndFigure(FigureEnd.Closed);
        sink.Close();
    }

    /// <summary>bounds(이 모니터) 안에 걸친 보이는 눈덩이를 목록 순서(아래쪽부터)대로 그린다.</summary>
    public void Draw(ID2D1DeviceContext ctx, IReadOnlyList<Snowball> balls, RectI bounds)
    {
        var world = ctx.Transform;
        foreach (var b in balls)
        {
            float reach = b.R * 1.9f; // 팔까지 포함
            if (!b.Visible || b.X + reach < bounds.Left || b.X - reach > bounds.Right
                || b.Y + b.R < bounds.Top || b.Y - reach > bounds.Bottom)
            {
                continue;
            }

            var center = new Vector2(b.X, b.Y);
            ctx.FillEllipse(new Ellipse(center, b.R + 0.9f, b.R + 0.9f), _rim);
            _fill.Center = center;
            _fill.GradientOriginOffset = new Vector2(-b.R * 0.35f, -b.R * 0.4f);
            _fill.RadiusX = b.R;
            _fill.RadiusY = b.R;
            ctx.FillEllipse(new Ellipse(center, b.R, b.R), _fill);

            if (b.Role == SnowballRole.Body)
            {
                DrawArms(ctx, b);
            }
            else if (b.Role == SnowballRole.Head)
            {
                DrawFace(ctx, b, world);
            }
        }

        ctx.Transform = world;
    }

    public void Dispose()
    {
        _nose.Dispose();
        _roundStroke.Dispose();
        _twig.Dispose();
        _carrot.Dispose();
        _coal.Dispose();
        _rim.Dispose();
        _fill.Dispose();
    }

    private void DrawFace(ID2D1DeviceContext ctx, Snowball b, Matrix3x2 world)
    {
        float eye = MathF.Max(1.1f, b.R * 0.1f);
        ctx.FillEllipse(new Ellipse(new Vector2(b.X - b.R * 0.32f, b.Y - b.R * 0.2f), eye, eye), _coal);
        ctx.FillEllipse(new Ellipse(new Vector2(b.X + b.R * 0.22f, b.Y - b.R * 0.2f), eye, eye), _coal);

        ctx.Transform = Matrix3x2.CreateScale(b.R) * Matrix3x2.CreateTranslation(b.X - b.R * 0.05f, b.Y) * world;
        ctx.FillGeometry(_nose, _carrot);
        ctx.Transform = world;

        // 숯 조각 입
        float dot = MathF.Max(0.8f, b.R * 0.06f);
        for (int i = -2; i <= 2; i++)
        {
            float t = i / 2f;
            ctx.FillEllipse(new Ellipse(new Vector2(b.X - b.R * 0.05f + t * b.R * 0.38f, b.Y + b.R * (0.48f - 0.1f * t * t)), dot, dot), _coal);
        }
    }

    private void DrawArms(ID2D1DeviceContext ctx, Snowball b)
    {
        float w = MathF.Max(1.2f, b.R * 0.08f);
        for (int side = -1; side <= 1; side += 2)
        {
            var root = new Vector2(b.X + side * b.R * 0.75f, b.Y - b.R * 0.25f);
            var tip = new Vector2(b.X + side * b.R * 1.75f, b.Y - b.R * 0.85f);
            ctx.DrawLine(root, tip, _twig, w, _roundStroke);

            // 끝에 갈라진 잔가지
            var fork = Vector2.Lerp(root, tip, 0.7f);
            ctx.DrawLine(fork, fork + new Vector2(side * b.R * 0.18f, b.R * 0.12f), _twig, w * 0.8f, _roundStroke);
            ctx.DrawLine(fork, fork + new Vector2(side * b.R * 0.05f, -b.R * 0.25f), _twig, w * 0.8f, _roundStroke);
        }
    }
}
