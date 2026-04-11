"""AI Screen Assistant — hotkey listener usando pynput."""
import threading
from pynput import keyboard


class HotkeyListener(threading.Thread):
    """Escuta globalmente por Ctrl+Shift+Space e chama o callback."""

    HOTKEY = "<ctrl>+<shift>+<space>"

    def __init__(self, callback):
        super().__init__(daemon=True)
        self.callback = callback

    def run(self):
        with keyboard.GlobalHotKeys({self.HOTKEY: self.callback}) as listener:
            listener.join()
