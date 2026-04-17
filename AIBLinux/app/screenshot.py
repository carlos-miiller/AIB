"""Captura de tela e conversão de imagens."""
import io
import base64
import mss
from PIL import Image


def capture_screen() -> Image.Image:
    """Captura o monitor primário e retorna uma imagem PIL."""
    with mss.mss() as sct:
        monitor = sct.monitors[1]  # 1 = monitor primário
        sct_img = sct.grab(monitor)
        img = Image.frombytes("RGB", sct_img.size, sct_img.bgra, "raw", "BGRX")
    return img


def image_to_base64(img: Image.Image, max_size=(1920, 1080), quality=85) -> str:
    """Converte imagem PIL para base64 JPEG para envio à API."""
    img_copy = img.copy()
    img_copy.thumbnail(max_size, Image.LANCZOS)
    buffer = io.BytesIO()
    img_copy.save(buffer, format="JPEG", quality=quality)
    return base64.b64encode(buffer.getvalue()).decode()


def image_to_qpixmap(img: Image.Image, size=(160, 90)):
    """Converte imagem PIL para QPixmap (thumbnail) para exibição na UI."""
    from PyQt6.QtGui import QPixmap
    thumb = img.copy()
    thumb.thumbnail(size, Image.LANCZOS)
    buffer = io.BytesIO()
    thumb.save(buffer, format="PNG")
    buffer.seek(0)
    pixmap = QPixmap()
    pixmap.loadFromData(buffer.getvalue())
    return pixmap
