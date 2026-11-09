"""앱/트레이 아이콘(snow.ico)을 만든다. 실행: python tools/make_icon.py

결과물은 src/SnowWindow/Assets/snow.ico 에 저장되며 저장소에 함께 커밋한다.
"""
import math
import pathlib

from PIL import Image, ImageDraw

SIZE = 256
SS = 4  # 슈퍼샘플링 배율(크게 그린 뒤 줄여서 계단 현상을 없앤다)
OUT = pathlib.Path(__file__).resolve().parent.parent / "src" / "SnowWindow" / "Assets" / "snow.ico"


def seg(draw: ImageDraw.ImageDraw, a, b, width: int, color) -> None:
    """끝이 둥근 선분."""
    draw.line([a, b], fill=color, width=width)
    h = width / 2
    for x, y in (a, b):
        draw.ellipse([x - h, y - h, x + h, y + h], fill=color)


def draw_flake(draw: ImageDraw.ImageDraw, cx: float, cy: float, r: float, width: int, color) -> None:
    for i in range(6):
        a = math.radians(90 + i * 60)
        ex, ey = cx + r * math.cos(a), cy - r * math.sin(a)
        seg(draw, (cx, cy), (ex, ey), width, color)
        # 가지 두 쌍
        for t, blen in ((0.45, 0.30), (0.72, 0.22)):
            bx, by = cx + r * t * math.cos(a), cy - r * t * math.sin(a)
            for side in (-1, 1):
                ba = a + side * math.radians(45)
                seg(draw, (bx, by), (bx + r * blen * math.cos(ba), by - r * blen * math.sin(ba)), width, color)
    draw.ellipse([cx - width, cy - width, cx + width, cy + width], fill=color)


def main() -> None:
    big = SIZE * SS
    img = Image.new("RGBA", (big, big), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    # 밝은/어두운 작업 표시줄 모두에서 보이도록 진한 남색 원판 위에 흰 눈송이를 그린다.
    pad = 6 * SS
    d.ellipse([pad, pad, big - pad, big - pad], fill=(28, 58, 110, 255))
    ring = pad + 9 * SS
    d.ellipse([ring, ring, big - ring, big - ring], fill=(46, 92, 160, 255))
    draw_flake(d, big / 2, big / 2, big * 0.35, 15 * SS, (255, 255, 255, 255))
    img = img.resize((SIZE, SIZE), Image.LANCZOS)
    OUT.parent.mkdir(parents=True, exist_ok=True)
    img.save(OUT, sizes=[(16, 16), (20, 20), (24, 24), (32, 32), (40, 40), (48, 48), (64, 64), (256, 256)])
    print(f"wrote {OUT}")


if __name__ == "__main__":
    main()
