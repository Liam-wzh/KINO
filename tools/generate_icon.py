"""Extract the supplied KINO mark and generate transparent PNG / Windows ICO assets.

Requires Pillow (development only): python -m pip install -r tools/requirements-assets.txt
The original artwork is retained in design/branding/logo-original.png.
"""
from io import BytesIO
from pathlib import Path
import struct

from PIL import Image, ImageDraw


ROOT = Path(__file__).resolve().parents[1]
ASSETS = ROOT / 'src' / 'Kino' / 'Assets'
ICON_SIZES = (16, 20, 24, 32, 40, 48, 64, 128, 256)


def extract_mark():
    # The supplied RGB image includes a baked-in checkerboard. Its near-black
    # artwork is separate from the pale background; retain the antialiased edge
    # while making the black interior solid and discarding the checkerboard.
    with Image.open(ROOT / 'design' / 'branding' / 'logo-original.png') as source:
        gray = source.convert('L')
    alpha = gray.point(lambda value: round(max(0, min(1, (200 - value) / 176)) * 255))
    bounds = alpha.getbbox()
    if bounds is None:
        raise ValueError('The source image does not contain a dark logo.')
    alpha = alpha.crop(bounds)
    alpha.thumbnail((960, 960), Image.Resampling.LANCZOS)
    mark = Image.new('RGBA', (1024, 1024))
    solid = Image.new('RGBA', alpha.size, (0, 0, 0, 255))
    solid.putalpha(alpha)
    mark.alpha_composite(solid, ((1024 - alpha.width) // 2, (1024 - alpha.height) // 2))
    return mark


def app_icon(mark):
    # The same silhouette, reversed on a teal tile, remains visible in both
    # light and dark Windows taskbars. Render large, then downsample each size.
    size = 1024
    tile = Image.new('RGBA', (size, size))
    gradient = Image.new('RGBA', (1, size))
    gradient.putdata([
        (round(22 - y / (size - 1) * 10),
         round(125 - y / (size - 1) * 44),
         round(109 - y / (size - 1) * 38), 255)
        for y in range(size)
    ])
    plate = Image.new('L', (size, size))
    ImageDraw.Draw(plate).rounded_rectangle((24, 24, 999, 999), radius=220, fill=255)
    tile.paste(gradient.resize((size, size)), (0, 0), plate)
    alpha = mark.getchannel('A').resize((874, 874), Image.Resampling.LANCZOS)
    glyph = Image.new('RGBA', alpha.size, (241, 255, 251, 255))
    glyph.putalpha(alpha)
    tile.alpha_composite(glyph, (75, 75))
    return tile


def write_ico(icon):
    entries, images = [], []
    offset = 6 + 16 * len(ICON_SIZES)
    for size in ICON_SIZES:
        stream = BytesIO()
        icon.resize((size, size), Image.Resampling.LANCZOS).save(stream, format='PNG', optimize=True)
        data = stream.getvalue()
        entries.append(struct.pack('<BBBBHHII', size % 256, size % 256, 0, 0, 1, 32, len(data), offset))
        images.append(data)
        offset += len(data)
    (ASSETS / 'kino.ico').write_bytes(struct.pack('<HHH', 0, 1, len(ICON_SIZES)) + b''.join(entries) + b''.join(images))


if __name__ == '__main__':
    ASSETS.mkdir(parents=True, exist_ok=True)
    logo = extract_mark()
    logo.save(ASSETS / 'logo.png', optimize=True)
    icon = app_icon(logo)
    preview = icon.resize((256, 256), Image.Resampling.LANCZOS)
    preview.save(ASSETS / 'app-icon.png', optimize=True)
    preview.save(ROOT / 'docs' / 'images' / 'app-icon.png', optimize=True)
    write_ico(icon)
    print(f'Generated transparent logo and {len(ICON_SIZES)} Windows icon sizes: {ASSETS}')
