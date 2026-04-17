"""Janela de chat flutuante — design dark glassmorphism com PyQt6."""
from typing import Optional

from PyQt6.QtWidgets import (
    QWidget, QVBoxLayout, QHBoxLayout, QScrollArea,
    QLabel, QTextEdit, QPushButton, QFrame, QSizePolicy, QApplication,
)
from PyQt6.QtCore import (
    Qt, QPropertyAnimation, QEasingCurve, pyqtSignal,
    QThread, QObject, QTimer, QEvent, QRect,
)
from PyQt6.QtGui import (
    QColor, QPainter, QPixmap, QCursor, QPainterPath,
)

from app.openai_client import OpenAIClient


# ──────────────────────────────────────────────────────────
# Constantes de layout
# ──────────────────────────────────────────────────────────
SCREEN_WIDTH_RATIO      = 0.50   # 50% da largura da tela
SCROLL_HEIGHT_RATIO     = 0.45   # área de respostas = 45% da altura do monitor (fixo)
MARGIN_BOTTOM           = 100    # pixels de margem da borda inferior (aumentado para erguer a janela)
WINDOW_MARGIN           = 8      # margem da janela ao container
INPUT_HEIGHT            = 70     # altura approximate da área de input


# ──────────────────────────────────────────────────────────
# Worker de streaming
# ──────────────────────────────────────────────────────────
class StreamWorker(QObject):
    chunk_received = pyqtSignal(str)
    finished = pyqtSignal()
    error = pyqtSignal(str)

    def __init__(self, client: OpenAIClient, text: str, image_base64: Optional[str]):
        super().__init__()
        self.client = client
        self.text = text
        self.image_base64 = image_base64

    def run(self):
        try:
            for chunk in self.client.send_message(self.text, self.image_base64):
                self.chunk_received.emit(chunk)
            self.finished.emit()
        except Exception as e:
            self.error.emit(str(e))


# ──────────────────────────────────────────────────────────
# Overlay escuro que cobre o restante da tela quando o chat
# está visível
# ──────────────────────────────────────────────────────────
class DimOverlay(QWidget):
    """Widget fullscreen semi-transparente que escurece o fundo."""

    def __init__(self, parent=None):
        super().__init__(parent)
        self.setWindowFlags(
            Qt.WindowType.FramelessWindowHint
            | Qt.WindowType.WindowStaysOnTopHint
            | Qt.WindowType.Tool
            | Qt.WindowType.BypassWindowManagerHint
        )
        self.setAttribute(Qt.WidgetAttribute.WA_TranslucentBackground)
        self.setAttribute(Qt.WidgetAttribute.WA_ShowWithoutActivating)
        self.setAttribute(Qt.WidgetAttribute.WA_TransparentForMouseEvents)
        self._alpha = 0
        self._anim = QPropertyAnimation(self, b"_opacity_prop")
        self._anim.setDuration(180)
        self._anim.setEasingCurve(QEasingCurve.Type.OutCubic)

    # Propriedade animável para opacidade do overlay
    def _get_opacity(self):
        return self._alpha

    def _set_opacity(self, value):
        self._alpha = value
        self.update()

    from PyQt6.QtCore import pyqtProperty
    _opacity_prop = pyqtProperty(int, _get_opacity, _set_opacity)

    def paintEvent(self, event):
        painter = QPainter(self)
        painter.fillRect(self.rect(), QColor(0, 0, 0, self._alpha))

    def show_dim(self, screen_geometry):
        self.setGeometry(screen_geometry)
        self._anim.stop()
        try:
            self._anim.finished.disconnect()
        except TypeError:
            pass
        self._anim.setStartValue(self._alpha)
        self._anim.setEndValue(120)  # ~47% opacidade
        self.show()
        self._anim.start()

    def hide_dim(self, on_done=None):
        self._anim.stop()
        try:
            self._anim.finished.disconnect()
        except TypeError:
            pass
        self._anim.setStartValue(self._alpha)
        self._anim.setEndValue(0)
        if on_done:
            self._anim.finished.connect(on_done)
        self._anim.finished.connect(self.hide)
        self._anim.start()



# ──────────────────────────────────────────────────────────
# Bubble de mensagem
# ──────────────────────────────────────────────────────────
class MessageBubble(QFrame):
    def __init__(self, text: str = "", is_user: bool = True, max_bubble_width: int = 340, parent=None):
        super().__init__(parent)
        self.is_user = is_user
        self.setStyleSheet("background: transparent; border: none;")

        layout = QHBoxLayout(self)
        layout.setContentsMargins(4, 2, 4, 2)

        self.label = QLabel(text)
        self.label.setWordWrap(True)
        self.label.setMaximumWidth(max_bubble_width)
        self.label.setTextInteractionFlags(Qt.TextInteractionFlag.TextSelectableByMouse)
        self.label.setSizePolicy(QSizePolicy.Policy.Preferred, QSizePolicy.Policy.Minimum)

        if is_user:
            self.label.setStyleSheet("""
                QLabel {
                    background: qlineargradient(x1:0, y1:0, x2:1, y2:1,
                        stop:0 #6C63FF, stop:1 #4ECDC4);
                    color: white;
                    border-radius: 16px 16px 4px 16px;
                    padding: 10px 14px;
                    font-size: 13px;
                    font-family: 'Inter', 'Segoe UI', sans-serif;
                }
            """)
            layout.addStretch()
            layout.addWidget(self.label)
        else:
            self.label.setStyleSheet("""
                QLabel {
                    background: rgba(255, 255, 255, 0.07);
                    color: #DCDCF0;
                    border-radius: 16px 16px 16px 4px;
                    border: 1px solid rgba(108, 99, 255, 0.2);
                    padding: 10px 14px;
                    font-size: 13px;
                    font-family: 'Inter', 'Segoe UI', sans-serif;
                }
            """)
            layout.addWidget(self.label)
            layout.addStretch()


# ──────────────────────────────────────────────────────────
# Janela principal
# ──────────────────────────────────────────────────────────
class ChatWindow(QWidget):
    def __init__(self):
        super().__init__()
        self.openai_client    = OpenAIClient()
        self.screenshot_base64: Optional[str] = None
        self.include_screenshot = False
        self.current_response_label: Optional[QLabel] = None
        self.current_response_text  = ""
        self._stream_thread: Optional[QThread] = None
        self._stream_worker: Optional[StreamWorker] = None
        self._bubble_count  = 0   # total de bubbles (user + AI)
        self._recalculating = False  # guard anti-recursão
        self._showing       = False  # True durante animação de entrada
        self._dim_overlay   = DimOverlay()  # overlay de escurecimento (tela inteira)

        # Cache de geometria das telas (calculado apenas na inicialização)
        self._cached_screens_geometry = [s.geometry() for s in QApplication.screens()]
        ps = QApplication.primaryScreen()
        self._cached_virtual_geometry = ps.virtualGeometry() if ps else QRect()

        self._setup_ui()
        self._setup_animation()

    # ── Tela ativa (onde o mouse está) usando cache ──────
    def _get_active_screen_geometry(self):
        pos = QCursor.pos()
        for sg in self._cached_screens_geometry:
            if sg.contains(pos):
                return sg
        return self._cached_screens_geometry[0] if self._cached_screens_geometry else QRect()

    # ── UI ───────────────────────────────────────────────
    def _setup_ui(self):
        self.setWindowFlags(
            Qt.WindowType.FramelessWindowHint
            | Qt.WindowType.WindowStaysOnTopHint
            | Qt.WindowType.Tool
            | Qt.WindowType.BypassWindowManagerHint
        )
        self.setAttribute(Qt.WidgetAttribute.WA_TranslucentBackground)

        root = QVBoxLayout(self)
        root.setContentsMargins(WINDOW_MARGIN, WINDOW_MARGIN,
                                WINDOW_MARGIN, WINDOW_MARGIN)
        root.setSpacing(0)

        # Container visual (borda arredondada + fundo escuro)
        self.container = QFrame()
        self.container.setObjectName("container")
        self.container.setStyleSheet("""
            QFrame#container {
                background: rgba(12, 12, 22, 0.90);
                border-radius: 18px;
                border: 1px solid rgba(108, 99, 255, 0.38);
            }
        """)

        c = QVBoxLayout(self.container)
        c.setContentsMargins(0, 0, 0, 0)
        c.setSpacing(0)

        # Mensagens (ocultas inicialmente)
        self.scroll_area = self._build_messages_area()
        self.scroll_area.hide()
        c.addWidget(self.scroll_area)

        # Separador sutil
        self.separator = QFrame()
        self.separator.setFixedHeight(1)
        self.separator.setStyleSheet("background: rgba(108, 99, 255, 0.15);")
        self.separator.hide()
        c.addWidget(self.separator)

        # Input — sempre visível
        self.input_frame = self._build_input_area()
        c.addWidget(self.input_frame)

        root.addWidget(self.container)

    def _build_messages_area(self) -> QScrollArea:
        scroll = QScrollArea()
        scroll.setWidgetResizable(True)
        scroll.setHorizontalScrollBarPolicy(Qt.ScrollBarPolicy.ScrollBarAlwaysOff)
        scroll.setStyleSheet("""
            QScrollArea { background: transparent; border: none; }
            QScrollBar:vertical {
                background: rgba(255,255,255,0.03);
                width: 5px;
                border-radius: 3px;
            }
            QScrollBar::handle:vertical {
                background: rgba(108,99,255,0.45);
                border-radius: 3px;
                min-height: 24px;
            }
            QScrollBar::add-line:vertical,
            QScrollBar::sub-line:vertical { height: 0; }
        """)

        self.messages_widget = QWidget()
        self.messages_widget.setStyleSheet("background: transparent;")
        self.messages_layout = QVBoxLayout(self.messages_widget)
        self.messages_layout.setContentsMargins(10, 12, 10, 8)
        self.messages_layout.setSpacing(6)
        self.messages_layout.addStretch()

        scroll.setWidget(self.messages_widget)
        self.scroll_area = scroll

        return scroll

    def _build_input_area(self) -> QFrame:
        area = QFrame()
        area.setObjectName("inputArea")
        area.setStyleSheet("QFrame#inputArea { background: transparent; }")

        layout = QHBoxLayout(area)
        layout.setContentsMargins(12, 12, 12, 12)
        layout.setSpacing(8)

        self.input_box = QTextEdit()
        self.input_box.setPlaceholderText("Mensagem… (Enter envia, Shift+Enter nova linha)")
        self.input_box.setMaximumHeight(96)
        self.input_box.setMinimumHeight(44)
        self.input_box.setStyleSheet("""
            QTextEdit {
                background: rgba(255,255,255,0.06);
                color: #E8E8F0;
                border: 1px solid rgba(108,99,255,0.22);
                border-radius: 14px;
                padding: 10px 14px;
                font-size: 13px;
                font-family: 'Inter', 'Segoe UI', sans-serif;
            }
            QTextEdit:focus {
                border: 1px solid rgba(108,99,255,0.6);
                background: rgba(255,255,255,0.09);
            }
        """)
        self.input_box.installEventFilter(self)

        # Botão enviar
        send_btn = QPushButton("➤")
        send_btn.setObjectName("sendBtn")
        send_btn.setFixedSize(44, 44)
        send_btn.setCursor(QCursor(Qt.CursorShape.PointingHandCursor))
        send_btn.clicked.connect(self._send_message)
        send_btn.setStyleSheet("""
            QPushButton#sendBtn {
                background: qlineargradient(x1:0,y1:0,x2:1,y2:1,
                    stop:0 #6C63FF, stop:1 #4ECDC4);
                color: white;
                border: none;
                border-radius: 22px;
                font-size: 17px;
            }
            QPushButton#sendBtn:hover {
                background: qlineargradient(x1:0,y1:0,x2:1,y2:1,
                    stop:0 #7D75FF, stop:1 #5EDDD5);
            }
            QPushButton#sendBtn:pressed {
                background: qlineargradient(x1:0,y1:0,x2:1,y2:1,
                    stop:0 #5550CC, stop:1 #3AAFA8);
            }
        """)

        # Botão screenshot (menor, abaixo do enviar)
        self.include_btn = QPushButton("📷")
        self.include_btn.setObjectName("includeBtn")
        self.include_btn.setFixedSize(36, 36)
        self.include_btn.setCursor(QCursor(Qt.CursorShape.PointingHandCursor))
        self.include_btn.setToolTip("Incluir screenshot na mensagem")
        self.include_btn.clicked.connect(self._toggle_screenshot)
        self._apply_include_btn_style()

        btn_col = QVBoxLayout()
        btn_col.setSpacing(6)
        btn_col.setContentsMargins(0, 0, 0, 0)
        btn_col.addWidget(send_btn,        alignment=Qt.AlignmentFlag.AlignHCenter)
        btn_col.addWidget(self.include_btn, alignment=Qt.AlignmentFlag.AlignHCenter)
        btn_col.addStretch()

        layout.addWidget(self.input_box)
        layout.addLayout(btn_col)
        return area

    # ── Geometria dinâmica ────────────────────────────────
    def _recalc_geometry(self):
        """Reposiciona e redimensiona a janela ancorada na parte inferior."""
        if self._recalculating:
            return
        self._recalculating = True
        try:
            sg = self._get_active_screen_geometry()

            scroll_h         = int(sg.height() * SCROLL_HEIGHT_RATIO)  # fixo 45%
            w                = int(sg.width()  * SCREEN_WIDTH_RATIO)
            x                = sg.x() + (sg.width() - w) // 2
            bottom           = sg.y() + sg.height() - MARGIN_BOTTOM
            container_chrome = WINDOW_MARGIN * 2

            if self._bubble_count == 0:
                self.scroll_area.hide()
                self.separator.hide()
                new_h = INPUT_HEIGHT + container_chrome
            else:
                self.scroll_area.setFixedHeight(scroll_h)
                self.scroll_area.show()
                self.separator.show()

                new_h = scroll_h + INPUT_HEIGHT + container_chrome

            y = bottom - new_h
            self.setGeometry(x, y, w, new_h)
        finally:
            self._recalculating = False

    # ── Screenshot ────────────────────────────────────────
    def _capture_screenshot(self):
        try:
            from app.screenshot import capture_screen, image_to_base64
            self.screenshot_base64 = image_to_base64(capture_screen())
        except Exception as exc:
            print(f"[screenshot] erro: {exc}")

    def _toggle_screenshot(self):
        self.include_screenshot = not self.include_screenshot
        self._apply_include_btn_style()

    def _apply_include_btn_style(self):
        if self.include_screenshot:
            self.include_btn.setText("✓")
            self.include_btn.setToolTip("Screenshot incluído — clique para remover")
            self.include_btn.setStyleSheet("""
                QPushButton#includeBtn {
                    background: qlineargradient(x1:0,y1:0,x2:1,y2:1,
                        stop:0 #6C63FF, stop:1 #4ECDC4);
                    color: white;
                    border: none;
                    border-radius: 18px;
                    font-size: 15px;
                    font-weight: 700;
                }
                QPushButton#includeBtn:hover {
                    background: qlineargradient(x1:0,y1:0,x2:1,y2:1,
                        stop:0 #7D75FF, stop:1 #5EDDD5);
                }
            """)
        else:
            self.include_btn.setText("📷")
            self.include_btn.setToolTip("Incluir screenshot na mensagem")
            self.include_btn.setStyleSheet("""
                QPushButton#includeBtn {
                    background: rgba(255,255,255,0.06);
                    color: rgba(200,195,255,0.7);
                    border: 1px solid rgba(108,99,255,0.25);
                    border-radius: 18px;
                    font-size: 15px;
                }
                QPushButton#includeBtn:hover {
                    background: rgba(108,99,255,0.2);
                    color: white;
                    border-color: rgba(108,99,255,0.55);
                }
            """)

    # ── Mensagens ─────────────────────────────────────────
    def _bubble_max_width(self) -> int:
        """Calcula largura máxima das bubbles com base na janela atual."""
        # ~72% da área de conteúdo (janela - margens - botões)
        w = self.width() if self.width() > 100 else 480
        return int((w - WINDOW_MARGIN * 2 - 70) * 0.72)

    def _add_bubble(self, text: str, is_user: bool) -> QLabel:
        bubble = MessageBubble(text, is_user, self._bubble_max_width())
        self.messages_layout.insertWidget(
            self.messages_layout.count() - 1, bubble
        )
        self._bubble_count += 1
        self._recalc_geometry()          # imediato, heightForWidth é computação pura
        QTimer.singleShot(60, self._scroll_bottom)
        return bubble.label

    def _scroll_bottom(self):
        sb = self.scroll_area.verticalScrollBar()
        sb.setValue(sb.maximum())

    # ── Envio ─────────────────────────────────────────────
    def _send_message(self):
        text = self.input_box.toPlainText().strip()
        if not text:
            return

        self.input_box.clear()
        self._add_bubble(text, is_user=True)

        img_b64 = self.screenshot_base64 if self.include_screenshot else None
        if self.include_screenshot:
            self.include_screenshot = False
            self._apply_include_btn_style()

        # Encerra stream anterior se ainda estiver rodando
        if self._stream_thread and self._stream_thread.isRunning():
            self._stream_thread.quit()
            self._stream_thread.wait(500)

        # Bubble de resposta (será preenchida pelo streaming)
        self.current_response_label = self._add_bubble("⋯", is_user=False)
        self.current_response_text  = ""

        self._stream_thread = QThread()
        self._stream_worker = StreamWorker(self.openai_client, text, img_b64)
        self._stream_worker.moveToThread(self._stream_thread)
        self._stream_thread.started.connect(self._stream_worker.run)
        self._stream_worker.chunk_received.connect(self._on_chunk)
        self._stream_worker.finished.connect(self._on_stream_done)
        self._stream_worker.error.connect(self._on_stream_error)
        self._stream_worker.finished.connect(self._stream_thread.quit)
        self._stream_thread.start()

    def _on_chunk(self, chunk: str):
        self.current_response_text += chunk
        self.current_response_label.setText(self.current_response_text)
        self._recalc_geometry()          # atualiza tamanho a cada token recebido
        QTimer.singleShot(30, self._scroll_bottom)

    def _on_stream_done(self):
        pass

    def _on_stream_error(self, error: str):
        self.current_response_label.setText(f"❌  Erro: {error}")

    # ── Nova conversa ─────────────────────────────────────
    def new_conversation(self):
        self.openai_client.clear_history()
        self._bubble_count = 0
        while self.messages_layout.count() > 1:
            item = self.messages_layout.takeAt(0)
            if item.widget():
                item.widget().deleteLater()
        QTimer.singleShot(50, self._recalc_geometry)

    # ── Animação / visibilidade ───────────────────────────
    def _setup_animation(self):
        self._anim = QPropertyAnimation(self, b"windowOpacity")
        self._anim.setDuration(180)
        self._anim.setEasingCurve(QEasingCurve.Type.OutCubic)

    def show_window(self):
        self._capture_screenshot()
        self.include_screenshot = False
        self._apply_include_btn_style()

        self._recalc_geometry()

        # Exibe overlay de escurecimento cobrindo toda a área virtual combinada dos monitores
        if self._cached_virtual_geometry and not self._cached_virtual_geometry.isEmpty():
            self._dim_overlay.show_dim(self._cached_virtual_geometry)

        self._showing = True
        self.setWindowOpacity(0.0)
        self.show()
        self.raise_()
        self.activateWindow()
        self.input_box.setFocus()

        # Garante que _do_hide não esteja conectado antes de iniciar
        try:
            self._anim.finished.disconnect(self._do_hide)
        except TypeError:
            pass
        self._anim.stop()
        self._anim.setStartValue(0.0)
        self._anim.setEndValue(1.0)
        self._anim.finished.connect(self._on_show_done)
        self._anim.start()

    def _on_show_done(self):
        self._showing = False
        try:
            self._anim.finished.disconnect(self._on_show_done)
        except TypeError:
            pass

    def hide_window(self):
        if self._showing:
            return  # não fecha durante animação de entrada
        self._anim.stop()
        try:
            self._anim.finished.disconnect(self._do_hide)
        except TypeError:
            pass
        self._anim.setStartValue(self.windowOpacity())
        self._anim.setEndValue(0.0)
        self._anim.finished.connect(self._do_hide)
        self._anim.start()
        # Esconde o overlay junto com a janela
        self._dim_overlay.hide_dim()

    def _do_hide(self):
        try:
            self._anim.finished.disconnect(self._do_hide)
        except TypeError:
            pass
        self.hide()

    # ── Eventos ───────────────────────────────────────────
    def eventFilter(self, obj, event):
        if obj is self.input_box and event.type() == QEvent.Type.KeyPress:
            if (
                event.key() == Qt.Key.Key_Return
                and not (event.modifiers() & Qt.KeyboardModifier.ShiftModifier)
            ):
                self._send_message()
                return True
        return super().eventFilter(obj, event)

    def changeEvent(self, event):
        """Fecha a janela quando perde o foco (clique fora)."""
        if event.type() == QEvent.Type.ActivationChange:
            # Não fecha durante animação de entrada ou cálculo de geometria
            if not self._showing and not self._recalculating:
                if not self.isActiveWindow() and self.isVisible():
                    self.hide_window()
        super().changeEvent(event)

    def keyPressEvent(self, event):
        if event.key() == Qt.Key.Key_Escape:
            self.hide_window()
        super().keyPressEvent(event)

    # ── Paint — sombra suave ──────────────────────────────
    def paintEvent(self, event):
        painter = QPainter(self)
        painter.setRenderHint(QPainter.RenderHint.Antialiasing)
        path = QPainterPath()
        r = self.rect().adjusted(WINDOW_MARGIN, WINDOW_MARGIN,
                                  -WINDOW_MARGIN, -WINDOW_MARGIN)
        path.addRoundedRect(float(r.x()), float(r.y()),
                             float(r.width()), float(r.height()), 18.0, 18.0)
        painter.fillPath(path, QColor(0, 0, 0, 0))
