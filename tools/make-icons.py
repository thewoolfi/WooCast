# -*- coding: utf-8 -*-
"""Отрисовка значка WooCast.

Значок собирается из простых фигур, а не из шрифта: так пропорции заданы числами
и не зависят от того, какие шрифты есть на машине сборки. Рисование идёт
в четырёхкратном размере с последующим уменьшением — это даёт чистые края
на всех размерах, включая мелкие значки панели задач.

Запуск:  python tools/make-icons.py
"""

from __future__ import annotations

import math
import re
import sys
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFilter, ImageFont

ROOT = Path(__file__).resolve().parent.parent

# Палитра взята из интерфейса приложения (Theme.kt), чтобы значок не спорил с ним.
ACCENT = (0x35, 0xD6, 0xC4)
ACCENT_LIGHT = (0x6A, 0xEF, 0xDF)
ACCENT_DEEP = (0x14, 0x8F, 0x86)
BACKGROUND = (0x07, 0x09, 0x0D)
ON_BACKGROUND = (0xE6, 0xED, 0xF3)
MUTED = (0x8B, 0x94, 0x9E)

MASTER = 1024
SCALE = 4                     # во сколько раз рисуем крупнее перед уменьшением
TILE_MARGIN = 0.035           # поля плитки, доля от стороны
TILE_RADIUS = 0.235           # радиус скругления, доля от стороны плитки
MARK_COLOR = (0xFF, 0xFF, 0xFF)


def rounded_mask(size: int, margin: int, radius: int) -> Image.Image:
    """Маска скруглённого квадрата."""
    mask = Image.new("L", (size, size), 0)
    draw = ImageDraw.Draw(mask)
    draw.rounded_rectangle((margin, margin, size - margin, size - margin), radius=radius, fill=255)
    return mask


def gradient_tile(size: int, margin: int) -> Image.Image:
    """Плитка: плавный вертикальный градиент плюс мягкий блик сверху слева."""
    # Три опорные точки вместо двух участков: на стыке участков получался заметный шов.
    stops = np.array([0.0, 0.55, 1.0], dtype=np.float32)
    colors = np.array([ACCENT_LIGHT, ACCENT, ACCENT_DEEP], dtype=np.float32)

    position = np.linspace(0.0, 1.0, size, dtype=np.float32)
    channels = [np.interp(position, stops, colors[:, i]) for i in range(3)]
    column = np.stack(channels, axis=1)
    tile = np.repeat(column[:, None, :], size, axis=1)

    # Блик: мягкое светлое пятно в верхней части, придаёт объём без «стекла».
    yy, xx = np.mgrid[0:size, 0:size].astype(np.float32)
    cx, cy = size * 0.30, size * 0.20
    distance = np.sqrt((xx - cx) ** 2 + (yy - cy) ** 2) / (size * 0.80)
    glow = np.clip(1.0 - distance, 0.0, 1.0) ** 2 * 0.22

    tile = tile * (1.0 - glow[:, :, None]) + 255.0 * glow[:, :, None]

    image = Image.fromarray(np.clip(tile, 0, 255).astype(np.uint8), "RGB").convert("RGBA")
    image.putalpha(rounded_mask(size, margin, int((size - 2 * margin) * TILE_RADIUS)))
    return image


def draw_broadcast_arcs(draw: ImageDraw.ImageDraw, center: tuple[float, float],
                        radii: list[float], thickness: int, color: tuple[int, int, int]) -> None:
    """Дуги сигнала над знаком: три расходящиеся волны."""
    cx, cy = center

    for radius in radii:
        box = (cx - radius, cy - radius, cx + radius, cy + radius)

        # Дуга в верхней половине, симметрично относительно вертикали. Концы оставлены
        # прямыми: круглые заделки превращали волны в бугорки и на мелких размерах
        # сливались в одно пятно.
        draw.arc(box, start=210, end=330, fill=color, width=thickness)


def draw_mark(size: int) -> Image.Image:
    """Знак: буква W и волны сигнала над ней."""
    layer = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    draw = ImageDraw.Draw(layer)

    unit = size / MASTER
    px = lambda value: value * unit  # noqa: E731 — короткая запись перевода в пиксели

    center_x = size / 2

    # Волны помещены в ложбинку буквы, а не над ней: над буквой они читались как
    # отдельный значок, и получалось два знака вместо одного.
    draw_broadcast_arcs(
        draw,
        center=(center_x, px(478)),
        radii=[px(52), px(104), px(156)],
        thickness=int(px(34)),
        color=MARK_COLOR,
    )

    # Буква W: четыре отрезка с закруглёнными соединениями. Отношение ширины к высоте
    # 1,42 — у буквы правильные пропорции; при 1,88 она выглядела растянутой.
    # Весь знак (волны и буква) центрирован по вертикали: иначе он оседал вниз
    # и сверху оставалась пустота.
    width = px(560)
    top = px(301)
    bottom = px(695)
    middle = top + (bottom - top) * 0.45
    stroke = int(px(96))
    left = center_x - width / 2
    step = width / 4

    points = [
        (left, top),
        (left + step, bottom),
        (left + 2 * step, middle),
        (left + 3 * step, bottom),
        (left + width, top),
    ]

    draw.line(points, fill=MARK_COLOR, width=stroke, joint="curve")

    for point in (points[0], points[-1]):
        draw.ellipse(
            (point[0] - stroke / 2, point[1] - stroke / 2,
             point[0] + stroke / 2, point[1] + stroke / 2),
            fill=MARK_COLOR,
        )

    return layer


def compose(size: int) -> Image.Image:
    """Собрать значок целиком."""
    margin = int(size * TILE_MARGIN)
    tile = gradient_tile(size, margin)
    mark = draw_mark(size)

    # Тень убрана намеренно: на мелких размерах она читалась как грязное пятно,
    # а знак и без неё отделяется от плитки — он белый на насыщенном фоне.
    return Image.alpha_composite(tile, mark)


def render(size: int) -> Image.Image:
    """Отрисовать значок в нужном размере через увеличенный проход."""
    big = compose(size * SCALE)
    return big.resize((size, size), Image.LANCZOS)


def make_tv_banner(width: int = 320, height: int = 180) -> Image.Image:
    """Баннер для главного экрана Android TV: тёмный фон и знак со словом."""
    scale = 4
    w, h = width * scale, height * scale
    banner = Image.new("RGBA", (w, h), BACKGROUND + (255,))

    # Лёгкое свечение в левой части: баннер выглядит живым на тёмном экране.
    yy, xx = np.mgrid[0:h, 0:w].astype(np.float32)
    distance = np.sqrt((xx - w * 0.06) ** 2 + (yy - h * 0.5) ** 2) / (w * 0.55)
    glow = np.clip(1.0 - distance, 0.0, 1.0) ** 2
    tint = np.array(ACCENT_DEEP, dtype=np.float32)
    base = np.array(BACKGROUND, dtype=np.float32)
    blended = base[None, None, :] * (1.0 - glow[:, :, None] * 0.55) + tint[None, None, :] * (glow[:, :, None] * 0.55)
    banner = Image.fromarray(np.clip(blended, 0, 255).astype(np.uint8), "RGB").convert("RGBA")

    # Знак слева.
    icon_size = int(h * 0.56)
    icon_x = int(h * 0.12)
    icon = render(icon_size)
    banner.alpha_composite(icon, (icon_x, (h - icon_size) // 2))

    draw = ImageDraw.Draw(banner)
    font_path = Path(r"C:\Windows\Fonts\segoeuib.ttf")
    if not font_path.exists():
        font_path = Path(r"C:\Windows\Fonts\arialbd.ttf")

    if not font_path.exists():
        return banner.resize((width, height), Image.LANCZOS)

    text_x = icon_x + icon_size + int(h * 0.075)
    available = w - text_x - int(h * 0.09)

    def fit_font(text: str, start_size: int) -> ImageFont.FreeTypeFont:
        """Подобрать размер шрифта так, чтобы надпись поместилась целиком."""
        size = start_size

        while size > 8:
            font = ImageFont.truetype(str(font_path), size)
            if draw.textlength(text, font=font) <= available:
                return font

            size -= 2

        return ImageFont.truetype(str(font_path), 8)

    title_font = fit_font("WooCast", int(h * 0.235))
    sub_font = fit_font("трансляция с ПК", int(h * 0.105))

    draw.text((text_x, h * 0.40), "WooCast", font=title_font, fill=ON_BACKGROUND, anchor="lm")
    draw.text((text_x, h * 0.62), "трансляция с ПК", font=sub_font, fill=ACCENT, anchor="lm")

    return banner.resize((width, height), Image.LANCZOS)


def main() -> int:
    assets = ROOT / "assets" / "icon"
    assets.mkdir(parents=True, exist_ok=True)

    master = render(MASTER)
    master.save(assets / "woocast-icon-1024.png")

    for size in (512, 256, 128):
        render(size).save(assets / f"woocast-icon-{size}.png")

    # Значок Windows: набор размеров в одном файле, иначе система растягивает
    # единственное изображение и в панели задач оно выглядит мутным.
    ico_sizes = [(16, 16), (20, 20), (24, 24), (32, 32), (40, 40), (48, 48), (64, 64), (128, 128), (256, 256)]
    master.resize((256, 256), Image.LANCZOS).save(
        assets / "woocast.ico",
        format="ICO",
        sizes=ico_sizes,
    )

    # Значки приложения для Android TV: плотности mdpi…xxxhdpi.
    densities = {
        "mdpi": 48,
        "hdpi": 72,
        "xhdpi": 96,
        "xxhdpi": 144,
        "xxxhdpi": 192,
    }

    for name, size in densities.items():
        target = ROOT / "android" / "app" / "src" / "main" / "res" / f"mipmap-{name}" / "ic_launcher.png"
        target.parent.mkdir(parents=True, exist_ok=True)
        render(size).save(target)

    banner = make_tv_banner()
    banner.save(ROOT / "android" / "app" / "src" / "main" / "res" / "drawable-xhdpi" / "tv_banner.png")

    # Адаптивный значок Android: на системе 8 и новее используется именно он,
    # а PNG-значки остаются только для старых версий. Поэтому передний план
    # рисуется отдельно — без плитки, только знак: фон задаётся цветом, а система
    # сама вырезает нужную форму и добавляет движение при наклоне.
    #
    # Холст адаптивного значка — 108 dp, безопасная зона — центральные 72 dp.
    # Знак занимает примерно 55 % холста, то есть уверенно помещается в неё.
    res = ROOT / "android" / "app" / "src" / "main" / "res"
    foreground = draw_mark(432)
    (res / "drawable-xxxhdpi").mkdir(parents=True, exist_ok=True)
    foreground.save(res / "drawable-xxxhdpi" / "ic_launcher_mark.png")

    # Цвет подложки адаптивного значка — фирменный акцент, как середина плитки.
    colors = res / "values" / "colors.xml"
    if colors.exists():
        text = colors.read_text(encoding="utf-8")
        updated = re.sub(
            r'(<color name="wc_launcher_background">)#[0-9A-Fa-f]{8}(</color>)',
            rf'\g<1>#FF35D6C4\g<2>',
            text,
        )

        if updated != text:
            colors.write_text(updated, encoding="utf-8", newline="\n")

    print("Готово:")
    for path in sorted(assets.iterdir()):
        print(f"  {path.relative_to(ROOT)}")
    print("  android/app/src/main/res/mipmap-*/ic_launcher.png")
    print("  android/app/src/main/res/drawable-xxxhdpi/ic_launcher_mark.png")
    print("  android/app/src/main/res/drawable-xhdpi/tv_banner.png")
    return 0


if __name__ == "__main__":
    sys.exit(main())
