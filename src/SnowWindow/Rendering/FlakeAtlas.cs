namespace SnowWindow.Rendering;

/// <summary>
/// 눈송이 스프라이트 여러 크기를 한 장에 모은 아틀라스(BGRA, 알파 곱셈 완료).
/// 크기별로 미리 그려 두어 GPU에서 크게 축소할 때 생기는 깜빡임을 피한다.
/// </summary>
internal sealed class FlakeAtlas
{
    private static readonly int[] Sizes = { 3, 4, 5, 6, 8, 10, 13, 16, 20, 26, 34 };

    public FlakeAtlas()
    {
        Height = Sizes[^1];
        Offsets = new int[Sizes.Length];
        int x = 0;
        for (int i = 0; i < Sizes.Length; i++)
        {
            Offsets[i] = x;
            x += Sizes[i] + 1;
        }

        Width = x;
        Pixels = new byte[Width * Height * 4];
        for (int i = 0; i < Sizes.Length; i++)
        {
            Paint(Offsets[i], Sizes[i]);
        }
    }

    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }
    private int[] Offsets { get; }

    /// <summary>지름 d(px)를 그리기에 알맞은 스프라이트의 원본 사각형.</summary>
    public (int Left, int Top, int Right, int Bottom) SourceFor(float diameter)
    {
        int i = 0;
        while (i < Sizes.Length - 1 && Sizes[i] < diameter)
        {
            i++;
        }

        return (Offsets[i], 0, Offsets[i] + Sizes[i], Sizes[i]);
    }

    private void Paint(int ox, int size)
    {
        // 흰 눈 + 바깥쪽의 옅은 푸른 회색 테두리(흰 배경 위에서도 보이게).
        const int sub = 4;
        float r = size / 2f;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float core = 0f;
                float halo = 0f;
                for (int sy = 0; sy < sub; sy++)
                {
                    for (int sx = 0; sx < sub; sx++)
                    {
                        float px = x + (sx + 0.5f) / sub - r;
                        float py = y + (sy + 0.5f) / sub - r;
                        float u = MathF.Sqrt(px * px + py * py) / r;
                        core += 1f - SmoothStep(0.42f, 0.72f, u);
                        halo += 0.22f * (1f - SmoothStep(0.72f, 1.0f, u));
                    }
                }

                core /= sub * sub;
                halo /= sub * sub;
                float a = core + halo * (1f - core);
                // 테두리 색 (70, 90, 120), 알파 곱셈
                float b = 255f * core + 120f * halo * (1f - core);
                float g = 255f * core + 90f * halo * (1f - core);
                float rr = 255f * core + 70f * halo * (1f - core);
                int idx = ((y * Width) + ox + x) * 4;
                Pixels[idx + 0] = ToByte(b);
                Pixels[idx + 1] = ToByte(g);
                Pixels[idx + 2] = ToByte(rr);
                Pixels[idx + 3] = ToByte(255f * a);
            }
        }
    }

    private static float SmoothStep(float e0, float e1, float x)
    {
        float t = Math.Clamp((x - e0) / (e1 - e0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    private static byte ToByte(float v) => (byte)Math.Clamp((int)MathF.Round(v), 0, 255);
}
