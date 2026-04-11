"""AIB — ponto de entrada principal."""
import sys
import os
from dotenv import load_dotenv

load_dotenv()

from PyQt6.QtWidgets import QApplication, QSystemTrayIcon, QMenu
from PyQt6.QtGui import QIcon, QPixmap, QColor
from PyQt6.QtCore import QObject, pyqtSignal

from app.chat_window import ChatWindow
from app.hotkey import HotkeyListener


def _make_tray_icon() -> QIcon:
    """Cria um ícone simples para a system tray."""
    pixmap = QPixmap(32, 32)
    pixmap.fill(QColor(0, 0, 0, 0))
    from PyQt6.QtGui import QPainter, QBrush, QLinearGradient, QPen
    painter = QPainter(pixmap)
    painter.setRenderHint(QPainter.RenderHint.Antialiasing)
    grad = QLinearGradient(0, 0, 32, 32)
    grad.setColorAt(0, QColor("#6C63FF"))
    grad.setColorAt(1, QColor("#4ECDC4"))
    painter.setBrush(QBrush(grad))
    painter.setPen(QPen(QColor(0, 0, 0, 0)))
    painter.drawEllipse(2, 2, 28, 28)
    painter.setPen(QPen(QColor("white")))
    font = painter.font()
    font.setPixelSize(16)
    font.setBold(True)
    painter.setFont(font)
    painter.drawText(pixmap.rect(), 0x84, "✦")  # AlignCenter
    painter.end()
    return QIcon(pixmap)


class App(QObject):
    _show_signal = pyqtSignal()

    def __init__(self, qt_app: QApplication):
        super().__init__()
        self.qt_app = qt_app
        self.qt_app.setQuitOnLastWindowClosed(False)

        self.window = ChatWindow()
        self._show_signal.connect(self._toggle_window)

        self._setup_tray()

        self._hotkey = HotkeyListener(callback=self._on_hotkey)
        self._hotkey.start()

    # ── System tray ───────────────────────────────────────
    def _setup_tray(self):
        icon = _make_tray_icon()
        self.tray = QSystemTrayIcon(icon, self.qt_app)
        self.tray.setToolTip("AIB (Ctrl+Shift+Space)")

        menu = QMenu()
        open_action = menu.addAction("✦  Abrir Chat")
        open_action.triggered.connect(self._toggle_window)
        menu.addSeparator()
        quit_action = menu.addAction("Sair")
        quit_action.triggered.connect(self.qt_app.quit)

        self.tray.setContextMenu(menu)
        self.tray.activated.connect(
            lambda reason: self._toggle_window()
            if reason == QSystemTrayIcon.ActivationReason.Trigger
            else None
        )
        self.tray.show()
        self.tray.showMessage(
            "AIB iniciado",
            "Pressione Ctrl+Shift+Space para abrir o chat.",
            QSystemTrayIcon.MessageIcon.Information,
            3000,
        )

    # ── Hotkey callback (chamado de thread pynput) ────────
    def _on_hotkey(self):
        self._show_signal.emit()  # seguro entre threads

    def _toggle_window(self):
        if self.window.isVisible():
            self.window.hide_window()
        else:
            self.window.show_window()

    def run(self) -> int:
        return self.qt_app.exec()


def main():
    app = QApplication(sys.argv)
    app.setApplicationName("AIB")
    assistant = App(app)
    sys.exit(assistant.run())


if __name__ == "__main__":
    main()
