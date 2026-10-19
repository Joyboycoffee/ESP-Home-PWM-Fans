import sys
import os
import json
import asyncio
import threading
import time
import aiohttp
from aioesphomeapi import APIClient, APIConnectionError
from PyQt6.QtWidgets import (
    QApplication, QMainWindow, QWidget, QVBoxLayout, QHBoxLayout,
    QLabel, QLineEdit, QPushButton, QSystemTrayIcon, QMenu, QStyle, QMessageBox, QSlider,
    QSpacerItem, QSizePolicy, QScrollArea, QSpinBox, QGridLayout, QFrame
)
from PyQt6.QtGui import QIcon, QAction, QPalette, QColor, QFont, QPainter, QPen, QBrush, QLinearGradient, QPainterPath
from PyQt6.QtCore import Qt, pyqtSignal, QObject, QTimer, QCoreApplication, QSize

# Constants
ESPHOME_HOST = "192.168.1.40"
ESPHOME_PORT = 6053
ESPHOME_PASSWORD = ""

def get_settings_path():
    """Get absolute path to settings file in the same directory as the script/executable"""
    if getattr(sys, 'frozen', False):
        base_dir = os.path.dirname(sys.executable)
    else:
        base_dir = os.path.dirname(os.path.abspath(__file__))
    return os.path.join(base_dir, "fan_settings.json")

SETTINGS_FILE = get_settings_path()

def log_event(message):
    timestamp = time.strftime("%Y-%m-%d %H:%M:%S")
    log_line = f"[{timestamp}] {message}"
    print(log_line)
    try:
        log_file_path = os.path.join(os.path.dirname(SETTINGS_FILE), "fan_controller.log")
        with open(log_file_path, "a") as f:
            f.write(log_line + "\n")
    except Exception as e:
        print(f"Failed to write to log file: {e}")

TEMP_DATA_URL = "http://localhost:8085/data.json"
ICON_PATH = "logo.png"
ICO_ICON_PATH = "app_icon.ico"

# Timing Configurations
MAIN_LOOP_INTERVAL = 3000         # Main loop running every 3 seconds
LHM_CHECK_INTERVAL = 15           # Check LHM status every 15 seconds

def get_resource_path(relative_path):
    """Get absolute path to resource, works for dev and for PyInstaller"""
    try:
        base_path = sys._MEIPASS
    except Exception:
        base_path = os.path.abspath(".")
    return os.path.join(base_path, relative_path)

# Custom Widgets

class ToggleSwitch(QPushButton):
    """Sleek neon toggle switch for automation."""
    def __init__(self, parent=None):
        super().__init__(parent)
        self.setCheckable(True)
        self.setCursor(Qt.CursorShape.PointingHandCursor)
        self.setFixedSize(54, 28)
        self.setStyleSheet("border: none; background: transparent;")
        
        # Colors
        self._on_color = QColor("#10b981")
        self._off_color = QColor("#1e293b")
        self._knob_color = QColor("#ffffff")
        
    def paintEvent(self, event):
        radius = self.height() // 2
        knob_margin = 3
        painter = QPainter(self)
        painter.setRenderHint(QPainter.RenderHint.Antialiasing)
        
        # Draw background track
        painter.setPen(Qt.PenStyle.NoPen)
        if self.isChecked():
            painter.setBrush(QBrush(self._on_color))
        else:
            painter.setBrush(QBrush(self._off_color))
        painter.drawRoundedRect(0, 0, self.width(), self.height(), radius, radius)
        
        # Draw knob
        knob_d = self.height() - 2 * knob_margin
        if self.isChecked():
            x = self.width() - knob_margin - knob_d
        else:
            x = knob_margin
            
        # Draw subtle knob shadow
        painter.setBrush(QBrush(QColor(0, 0, 0, 80)))
        painter.drawEllipse(x + 1, knob_margin + 1, knob_d, knob_d)
        
        # Draw knob itself
        painter.setBrush(QBrush(self._knob_color))
        painter.drawEllipse(x, knob_margin, knob_d, knob_d)


class CircularGauge(QWidget):
    """Premium visual circular gauge with neon gradients."""
    def __init__(self, title, unit, min_val, max_val, parent=None):
        super().__init__(parent)
        self.title = title
        self.unit = unit
        self.min_val = float(min_val)
        self.max_val = float(max_val)
        self.value = self.min_val
        self.setMinimumSize(140, 140)
        
        # Arc colors
        self.gradient_colors = []
        
    def set_value(self, val):
        self.value = max(self.min_val, min(self.max_val, float(val)))
        self.update()
        
    def set_gradient_colors(self, colors):
        """List of tuples (position, QColor)"""
        self.gradient_colors = colors
        self.update()
        
    def paintEvent(self, event):
        painter = QPainter(self)
        painter.setRenderHint(QPainter.RenderHint.Antialiasing)
        
        width = self.width()
        height = self.height()
        side = min(width, height)
        
        # Center coordinate system
        painter.translate(width / 2, height / 2)
        painter.scale(side / 200.0, side / 200.0)
        
        # Draw dark outer ring
        painter.setPen(QPen(QColor("#1a2233"), 1, Qt.PenStyle.SolidLine))
        painter.setBrush(QBrush(QColor("#080b11")))
        painter.drawEllipse(-90, -90, 180, 180)
        
        # Draw background track for arc
        pen_bg = QPen(QColor("#111625"), 10, Qt.PenStyle.SolidLine)
        pen_bg.setCapStyle(Qt.PenCapStyle.RoundCap)
        painter.setPen(pen_bg)
        painter.drawArc(-75, -75, 150, 150, 225 * 16, -270 * 16)
        
        # Calculate active span angle
        ratio = (self.value - self.min_val) / (self.max_val - self.min_val) if (self.max_val > self.min_val) else 0.0
        span_angle = -270.0 * ratio
        
        # Draw active arc with dynamic color/gradient
        if ratio > 0.0:
            color = QColor("#10b981") # Fallback
            if self.gradient_colors:
                # Basic interpolation of colors
                sorted_colors = sorted(self.gradient_colors, key=lambda x: x[0])
                color = sorted_colors[0][1]
                for i in range(len(sorted_colors) - 1):
                    pos1, col1 = sorted_colors[i]
                    pos2, col2 = sorted_colors[i+1]
                    if pos1 <= ratio <= pos2:
                        r_ratio = (ratio - pos1) / (pos2 - pos1) if (pos2 > pos1) else 0.0
                        color = QColor(
                            int(col1.red() + r_ratio * (col2.red() - col1.red())),
                            int(col1.green() + r_ratio * (col2.green() - col1.green())),
                            int(col1.blue() + r_ratio * (col2.blue() - col1.blue()))
                        )
                        break
                if ratio > sorted_colors[-1][0]:
                    color = sorted_colors[-1][1]
            
            # Setup glowing active pen
            pen_active = QPen(color, 10, Qt.PenStyle.SolidLine)
            pen_active.setCapStyle(Qt.PenCapStyle.RoundCap)
            painter.setPen(pen_active)
            painter.drawArc(-75, -75, 150, 150, 225 * 16, int(span_angle * 16))
            
        # Draw value text
        painter.setPen(QPen(QColor("#f8fafc")))
        painter.setFont(QFont("Segoe UI", 26, QFont.Weight.Bold))
        val_str = f"{self.value:.1f}" if self.unit == "°C" else f"{int(self.value)}"
        rect_val = painter.fontMetrics().boundingRect(val_str)
        painter.drawText(-rect_val.width() // 2, 8, val_str)
        
        # Draw unit
        painter.setPen(QPen(QColor("#94a3b8")))
        painter.setFont(QFont("Segoe UI", 11, QFont.Weight.Bold))
        rect_unit = painter.fontMetrics().boundingRect(self.unit)
        painter.drawText(-rect_unit.width() // 2, 36, self.unit)
        
        # Draw label
        painter.setPen(QPen(QColor("#64748b")))
        painter.setFont(QFont("Segoe UI", 9, QFont.Weight.Bold))
        rect_title = painter.fontMetrics().boundingRect(self.title)
        painter.drawText(-rect_title.width() // 2, -38, self.title)


class FanCurveWidget(QWidget):
    """Paints a professional real-time temperature-fan-speed graph with drag-to-edit thresholds."""
    def __init__(self, parent=None):
        super().__init__(parent)
        self.thresholds = []
        self.current_temp = None
        self.dragged_index = None
        self.setMinimumHeight(240)
        self.setMouseTracking(True)
        self.setCursor(Qt.CursorShape.ArrowCursor)
        
    def set_data(self, thresholds, current_temp=None):
        if self.dragged_index is None:
            self.thresholds = sorted(thresholds, key=lambda x: x[0])
        self.current_temp = current_temp
        self.update()
        
    def get_graph_dims(self):
        rect = self.rect()
        margin_left = 40
        margin_right = 20
        margin_top = 20
        margin_bottom = 30
        graph_width = rect.width() - margin_left - margin_right
        graph_height = rect.height() - margin_top - margin_bottom
        return margin_left, margin_right, margin_top, margin_bottom, graph_width, graph_height
        
    def map_x(self, t):
        margin_left, _, _, _, graph_width, _ = self.get_graph_dims()
        min_temp, max_temp = 20.0, 90.0
        t_clipped = max(min_temp, min(max_temp, float(t)))
        ratio = (t_clipped - min_temp) / (max_temp - min_temp)
        return margin_left + int(ratio * graph_width)
        
    def map_y(self, s):
        _, _, _, margin_bottom, _, graph_height = self.get_graph_dims()
        ratio = s / 100.0
        return self.rect().height() - margin_bottom - int(ratio * graph_height)
        
    def unmap_x(self, x):
        margin_left, _, _, _, graph_width, _ = self.get_graph_dims()
        min_temp, max_temp = 20.0, 90.0
        if graph_width <= 0:
            return min_temp
        ratio = (x - margin_left) / graph_width
        t = min_temp + ratio * (max_temp - min_temp)
        return max(min_temp, min(max_temp, round(t)))
        
    def unmap_y(self, y):
        _, _, _, margin_bottom, _, graph_height = self.get_graph_dims()
        if graph_height <= 0:
            return 0
        ratio = (self.rect().height() - margin_bottom - y) / graph_height
        s = ratio * 100.0
        return max(0, min(100, round(s)))
        
    def paintEvent(self, event):
        painter = QPainter(self)
        painter.setRenderHint(QPainter.RenderHint.Antialiasing)
        
        rect = self.rect()
        # Draw smooth background
        painter.setBrush(QBrush(QColor("#05070a")))
        painter.setPen(QPen(QColor("#1a2233"), 1))
        painter.drawRoundedRect(rect, 8, 8)
        
        margin_left, margin_right, margin_top, margin_bottom, graph_width, graph_height = self.get_graph_dims()
        min_temp, max_temp = 20.0, 90.0
        
        # Draw grid
        grid_pen = QPen(QColor("#111625"), 1, Qt.PenStyle.SolidLine)
        painter.setPen(grid_pen)
        
        # Horizontal lines (Speed)
        for pct in [0, 25, 50, 75, 100]:
            y = self.map_y(pct)
            painter.drawLine(margin_left, y, rect.width() - margin_right, y)
            painter.setPen(QPen(QColor("#475569"), 1))
            painter.setFont(QFont("Segoe UI", 8))
            painter.drawText(8, y + 4, f"{pct}%")
            painter.setPen(grid_pen)
            
        # Vertical lines (Temp)
        for temp in [20, 30, 40, 50, 60, 70, 80, 90]:
            x = self.map_x(temp)
            painter.drawLine(x, margin_top, x, rect.height() - margin_bottom)
            painter.setPen(QPen(QColor("#475569"), 1))
            painter.setFont(QFont("Segoe UI", 8))
            painter.drawText(x - 10, rect.height() - 10, f"{temp}°")
            painter.setPen(grid_pen)
            
        # Draw step curve line
        if len(self.thresholds) >= 2:
            curve_pen = QPen(QColor("#06b6d4"), 2, Qt.PenStyle.SolidLine)
            painter.setPen(curve_pen)
            
            first_speed = self.thresholds[0][1]
            last_speed = self.thresholds[-1][1]
            
            # Step curve drawing:
            # From min_temp to t_1: height s_1
            painter.drawLine(self.map_x(min_temp), self.map_y(first_speed), self.map_x(self.thresholds[0][0]), self.map_y(first_speed))
            
            # Draw stair steps
            for i in range(len(self.thresholds) - 1):
                t1, s1 = self.thresholds[i]
                t2, s2 = self.thresholds[i+1]
                
                # Vertical jump line at t1 from s1 to s2
                painter.drawLine(self.map_x(t1), self.map_y(s1), self.map_x(t1), self.map_y(s2))
                # Horizontal line from t1 to t2 at height s2
                painter.drawLine(self.map_x(t1), self.map_y(s2), self.map_x(t2), self.map_y(s2))
                
            # From t_n to max_temp: height s_n
            painter.drawLine(self.map_x(self.thresholds[-1][0]), self.map_y(last_speed), self.map_x(max_temp), self.map_y(last_speed))
            
        # Draw points
        for t, s in self.thresholds:
            x = self.map_x(t)
            y = self.map_y(s)
            painter.setPen(QPen(QColor("#22d3ee"), 2))
            painter.setBrush(QBrush(QColor("#0891b2")))
            painter.drawEllipse(x - 5, y - 5, 10, 10)
            
        # Draw current CPU temp marker line
        if self.current_temp is not None:
            x_marker = self.map_x(self.current_temp)
            
            # Vertical indicator line
            painter.setPen(QPen(QColor("#ef4444"), 1, Qt.PenStyle.DashLine))
            painter.drawLine(x_marker, margin_top, x_marker, rect.height() - margin_bottom)
            
            # Calculate target speed using step curve logic
            target_speed = 0
            if len(self.thresholds) > 0:
                if self.current_temp <= self.thresholds[0][0]:
                    target_speed = self.thresholds[0][1]
                elif self.current_temp > self.thresholds[-1][0]:
                    target_speed = self.thresholds[-1][1]
                else:
                    target_speed = self.thresholds[0][1]
                    for i in range(len(self.thresholds) - 1):
                        t1, s1 = self.thresholds[i]
                        t2, s2 = self.thresholds[i+1]
                        if t1 < self.current_temp <= t2:
                            target_speed = s2
                            break
                            
            y_marker = self.map_y(target_speed)
            
            # Draw glowing intersection dot
            painter.setPen(QPen(QColor("#fca5a5"), 2))
            painter.setBrush(QBrush(QColor("#ef4444")))
            painter.drawEllipse(x_marker - 5, y_marker - 5, 10, 10)

    def mousePressEvent(self, event):
        if event.button() == Qt.MouseButton.LeftButton:
            pos = event.position()
            x = pos.x()
            y = pos.y()
            
            # Find closest threshold point (within 16 pixels radius)
            best_idx = None
            min_dist = 99999.0
            for i, (t, s) in enumerate(self.thresholds):
                px = self.map_x(t)
                py = self.map_y(s)
                dist = ((x - px)**2 + (y - py)**2)**0.5
                if dist < 16.0 and dist < min_dist:
                    min_dist = dist
                    best_idx = i
            
            if best_idx is not None:
                self.dragged_index = best_idx
                self.setCursor(Qt.CursorShape.ClosedHandCursor)
                self.update()
                
    def mouseMoveEvent(self, event):
        pos = event.position()
        x = pos.x()
        y = pos.y()
        
        if self.dragged_index is not None:
            new_t = self.unmap_x(x)
            new_s = self.unmap_y(y)
            
            idx = self.dragged_index
            min_t = 20
            max_t = 90
            
            if idx > 0:
                min_t = self.thresholds[idx-1][0] + 1
            if idx < len(self.thresholds) - 1:
                max_t = self.thresholds[idx+1][0] - 1
                
            new_t = max(min_t, min(max_t, new_t))
            
            self.thresholds[idx] = (new_t, new_s)
            self.update()
            
            # Propagate live value to controller in memory
            controller = None
            p = self.parent()
            while p is not None:
                if hasattr(p, 'controller'):
                    controller = p.controller
                    break
                p = p.parent()
                
            if controller:
                controller.thresholds = list(self.thresholds)
                if controller.main_window and hasattr(controller.main_window, 'fan_curve'):
                    controller.main_window.fan_curve.update()
        else:
            hover_point = False
            for t, s in self.thresholds:
                px = self.map_x(t)
                py = self.map_y(s)
                dist = ((x - px)**2 + (y - py)**2)**0.5
                if dist < 12.0:
                    hover_point = True
                    break
            if hover_point:
                self.setCursor(Qt.CursorShape.PointingHandCursor)
            else:
                self.setCursor(Qt.CursorShape.ArrowCursor)
                
    def mouseReleaseEvent(self, event):
        if self.dragged_index is not None:
            self.dragged_index = None
            self.setCursor(Qt.CursorShape.PointingHandCursor)
            
            controller = None
            p = self.parent()
            while p is not None:
                if hasattr(p, 'controller'):
                    controller = p.controller
                    break
                p = p.parent()
                
            if controller:
                controller.save_settings()


# Connection Logic

class OptimizedESPHomeClient:
    """Handles communications with the ESPHome Device."""
    def __init__(self, host, port, password, worker):
        self.host = host
        self.port = port
        self.password = password
        self.worker = worker
        self.client = None
        self.fan_entity = None
        self.is_connected = False
        self._connection_lock = asyncio.Lock()
        self._last_command_time = 0
        self._command_cooldown = 1.0
        self._last_successful_speed = None
        self._entities_cache = []
        
    async def connect(self):
        async with self._connection_lock:
            try:
                if self.client:
                    try:
                        await self.client.disconnect()
                    except:
                        pass
                    self.client = None
                    
                log_event(f"Attempting connection to ESP device at {self.host}:{self.port}...")
                self.client = APIClient(self.host, self.port, self.password)
                await self.client.connect(login=True)
                self.is_connected = True
                
                found = await self.find_fan_entity()
                if found:
                    log_event(f"Connected to ESP device. Found fan entity: {self.fan_entity.name}")
                else:
                    log_event("Connected to ESP device, but 'silent_fan' entity was not found.")
                
                # Subscribe to state updates
                try:
                    self.client.subscribe_states(self.handle_state_update)
                    log_event("Subscribed to ESPHome state updates.")
                except Exception as e:
                    log_event(f"Warning: Failed to subscribe to state updates: {e}")
                    
                # Explicitly disable full speed override on startup connection to prevent safety override remaining on
                try:
                    await self.set_switch_state("full_speed_override", False)
                except Exception as e:
                    log_event(f"Failed to clear full speed override on connection startup: {e}")
                    
                return self.is_connected and self.fan_entity is not None
            except Exception as e:
                log_event(f"ESPHome connection failure: {e}")
                self.is_connected = False
                self.client = None
                self.fan_entity = None
                return False
                
    def handle_state_update(self, state):
        if not self._entities_cache:
            return
            
        for entity in self._entities_cache:
            if entity.key == state.key:
                object_id = getattr(entity, 'object_id', None)
                if object_id:
                    self.worker.entity_state_signal.emit(object_id, state)
                break
                
    async def set_switch_state(self, object_id, state):
        if not self.is_connected or not self.client or not self._entities_cache:
            return False
            
        for entity in self._entities_cache:
            if getattr(entity, 'object_id', None) == object_id:
                try:
                    cmd = self.client.switch_command(key=entity.key, state=state)
                    if asyncio.iscoroutine(cmd):
                        await cmd
                    elif isinstance(cmd, asyncio.Future):
                        await cmd
                    log_event(f"Dispatched switch command [{object_id}]: {state}")
                    return True
                except Exception as e:
                    log_event(f"Error sending switch command: {e}")
                    return False
        return False
                
    async def find_fan_entity(self):
        try:
            entities_data = await self.client.list_entities_services()
            entities = []
            if isinstance(entities_data, tuple):
                entities.extend(entities_data[0])
            elif isinstance(entities_data, list):
                entities.extend(entities_data)
                
            self._entities_cache = entities
            
            for entity in entities:
                if getattr(entity, 'object_id', None) in ("silent_fan", "pc_fan"):
                    self.fan_entity = entity
                    print(f"Found Silent Fan: {self.fan_entity.name}")
                    return True
            print("Warning: object_id 'silent_fan' or 'pc_fan' not found on device.")
            self.fan_entity = None
            return False
        except Exception as e:
            print(f"Error listing ESPHome entities: {e}")
            self.fan_entity = None
            return False
            
    async def set_fan_speed(self, speed_percent):
        """Sends speed to ESPHome with fallback layouts."""
        current_time = time.time()
        if (self._last_successful_speed == speed_percent and 
            current_time - self._last_command_time < self._command_cooldown):
            return True
            
        if not self.is_connected or not self.client or not self.fan_entity:
            return False
            
        try:
            self._last_command_time = current_time
            print(f"Dispatching fan command: {speed_percent}%")
            
            # Robust compatibility check
            cmd_result = None
            try:
                cmd_result = self.client.fan_command(
                    key=self.fan_entity.key,
                    state=speed_percent > 0,
                    speed_level=speed_percent
                )
            except TypeError:
                try:
                    cmd_result = self.client.fan_command(
                        self.fan_entity.key,
                        speed_percent > 0,
                        speed_percent
                    )
                except Exception as ex:
                    log_event(f"Fallback command failed: {ex}")
                    raise
                    
            if asyncio.iscoroutine(cmd_result):
                await cmd_result
            elif isinstance(cmd_result, asyncio.Future):
                await cmd_result
                
            self._last_successful_speed = speed_percent
            self.worker.fan_speed_signal.emit(speed_percent)
            return True
        except Exception as e:
            log_event(f"Error issuing fan command: {e}. Connection lost.")
            await self.disconnect()
            self.worker.status_signal.emit("Disconnected")
            self.worker.trigger_reconnect()
            return False
            
    async def disconnect(self):
        self.is_connected = False
        self.fan_entity = None
        if self.client:
            try:
                await self.client.disconnect()
            except Exception as e:
                print(f"Disconnect clean error: {e}")
            self.client = None


class OptimizedAsyncioWorker(QObject):
    """Background connection worker with automatic reconnection logic."""
    status_signal = pyqtSignal(str)
    fan_speed_signal = pyqtSignal(int)
    entity_state_signal = pyqtSignal(str, object)
    
    def __init__(self, parent=None):
        super().__init__(parent)
        self.esphome_client = None
        self.loop = asyncio.new_event_loop()
        
        self._target_host = ESPHOME_HOST
        self._target_port = ESPHOME_PORT
        self._target_password = ESPHOME_PASSWORD
        self._target_changed = True
        self._wake_event = asyncio.Event()
        self._last_connect_time = 0
        self._consecutive_failures = 0
        
        log_event("Nexus Fan Controller worker thread started.")
        threading.Thread(target=self.run_loop, daemon=True).start()
        self.loop.call_soon_threadsafe(lambda: asyncio.ensure_future(self.connection_keeper_loop()))
        
    def run_loop(self):
        asyncio.set_event_loop(self.loop)
        self.loop.run_forever()
        
    def stop_loop(self):
        self.loop.call_soon_threadsafe(self.loop.stop)
        
    def schedule_task(self, coro):
        if self.loop.is_running():
            return asyncio.run_coroutine_threadsafe(coro, self.loop)
            
    def update_connection_target(self, host, port=6053, password=""):
        log_event(f"ESP Host target updated to: {host}")
        self._target_host = host
        self._target_port = port
        self._target_password = password
        self._target_changed = True
        self.trigger_reconnect()
        
    def set_fan_speed_command(self, speed):
        if self.esphome_client and self.esphome_client.is_connected:
            self.schedule_task(self.esphome_client.set_fan_speed(speed))
            
    def trigger_reconnect(self):
        self.loop.call_soon_threadsafe(self._wake_event.set)
        
    async def wait_cooldown(self, timeout):
        self._wake_event.clear()
        try:
            await asyncio.wait_for(self._wake_event.wait(), timeout)
            return True # Woke up early (target change or fail trigger)
        except asyncio.TimeoutError:
            return False # Completed full sleep
            
    async def connection_keeper_loop(self):
        log_event("Async connection supervisor running.")
        backoff = 2
        
        while True:
            try:
                if self._target_changed or not self.esphome_client:
                    self._target_changed = False
                    if self.esphome_client:
                        await self.esphome_client.disconnect()
                    self.esphome_client = OptimizedESPHomeClient(
                        self._target_host, self._target_port, self._target_password, self
                    )
                    backoff = 2
                    self._consecutive_failures = 0
                    
                if not self.esphome_client.is_connected:
                    self.status_signal.emit("Connecting")
                    success = await self.esphome_client.connect()
                    if success:
                        self.status_signal.emit("Connected")
                        self._last_connect_time = time.time()
                    else:
                        self.status_signal.emit("Disconnected")
                        self._consecutive_failures += 1
                        log_event(f"Connection failed. Next retry in {backoff}s...")
                        # Sleep with backoff (wakes immediately on target change)
                        await self.wait_cooldown(backoff)
                        backoff = min(backoff * 2, 30)
                        continue
                        
                # Perform ping check every 5 seconds
                woke_early = await self.wait_cooldown(5.0)
                if woke_early:
                    continue
                    
                if self.esphome_client and self.esphome_client.is_connected:
                    # Connection stability detection: Reset backoff if connected for 30s
                    if time.time() - self._last_connect_time > 30.0 and backoff > 2:
                        log_event("Connection stable for 30s. Resetting backoff.")
                        backoff = 2
                        self._consecutive_failures = 0
                        
                    try:
                        conn = self.esphome_client.client._connection
                        if conn is None or not conn.is_connected:
                            raise APIConnectionError("Socket connection lost")
                    except Exception as e:
                        log_event(f"ESPHome connection drop: {e}. Connection lost.")
                        await self.esphome_client.disconnect()
                        self.status_signal.emit("Disconnected")
                        
            except asyncio.CancelledError:
                break
            except Exception as e:
                log_event(f"Keeper loop execution error: {e}")
                await asyncio.sleep(5)


# Main controller class

class OptimizedFanController(QObject):
    """Manages automation curves and temperature polling."""
    def __init__(self, parent=None):
        super().__init__(parent)
        self.main_window = None
        self.worker = None
        self.manual_speed_mode = False
        self.full_speed_mode = False
        self.manual_speed_value = 0
        self.last_fan_speed = -1
        self.automation_enabled = False
        
        self._temp_cache = None
        self._temp_cache_time = 0
        self._temp_cache_duration = 2
        
        # Default curve thresholds
        self.thresholds = [
            (30, 0), (40, 25), (50, 50), (60, 75), (70, 100)
        ]
        self.load_settings()
        
    def set_worker(self, worker):
        self.worker = worker
        self.worker.status_signal.connect(self.update_gui_status)
        self.worker.fan_speed_signal.connect(self.update_gui_speed)
        self.worker.entity_state_signal.connect(self.handle_entity_state)
        
    def handle_entity_state(self, object_id, state):
        if self.main_window:
            self.main_window.update_entity_display(object_id, state)
            
    def update_gui_status(self, state):
        if self.main_window:
            self.main_window.update_connection_pill(state)
            
    def update_gui_speed(self, speed):
        if self.main_window:
            self.main_window.update_fan_speed_display(speed)
            
    def load_settings(self):
        global ESPHOME_HOST
        if os.path.exists(SETTINGS_FILE):
            try:
                with open(SETTINGS_FILE, 'r') as f:
                    settings = json.load(f)
                    self.thresholds = settings.get("thresholds", self.thresholds)
                    self.automation_enabled = settings.get("automation_enabled", False)
                    # Sync manual mode status
                    self.manual_speed_mode = not self.automation_enabled
                    ESPHOME_HOST = settings.get("esp_host", ESPHOME_HOST)
                    log_event("Fan settings restored successfully.")
            except Exception as e:
                log_event(f"Load settings exception: {e}")
                
    def save_settings(self):
        settings = {
            "thresholds": self.thresholds,
            "automation_enabled": self.automation_enabled,
            "esp_host": ESPHOME_HOST
        }
        if self.main_window:
            settings["window_geometry"] = self.main_window.saveGeometry().toHex().data().decode("utf-8")
        try:
            with open(SETTINGS_FILE, 'w') as f:
                json.dump(settings, f, indent=4)
            log_event("Fan settings saved successfully.")
        except Exception as e:
            log_event(f"Save settings error: {e}")
            
    def update_thresholds(self, new_thresholds):
        self.thresholds = sorted(new_thresholds, key=lambda x: x[0])
        self.save_settings()
        if self.main_window:
            self.main_window.fan_curve.set_data(self.thresholds)
            
    def update_manual_speed(self, speed):
        self.manual_speed_value = speed
        if self.worker:
            self.worker.set_fan_speed_command(speed)
            
    async def get_cpu_gpu_temps(self):
        """Fetch temperature from Libre Hardware Monitor."""
        current_time = time.time()
        if self._temp_cache is not None and (current_time - self._temp_cache_time < self._temp_cache_duration):
            return self._temp_cache
            
        try:
            timeout = aiohttp.ClientTimeout(total=1.5)
            async with aiohttp.ClientSession(timeout=timeout) as session:
                async with session.get(TEMP_DATA_URL) as response:
                    if response.status == 200:
                        data = await response.json()
                        temp = None
                        
                        # Navigate Libre Hardware Monitor JSON
                        for computer in data.get('Children', []):
                            for hardware in computer.get('Children', []):
                                if 'CPU' in hardware.get('Text', '') or 'Ryzen' in hardware.get('Text', ''):
                                    for sensor_group in hardware.get('Children', []):
                                        if sensor_group.get('Text') == 'Temperatures':
                                            for sensor in sensor_group.get('Children', []):
                                                if 'Package' in sensor.get('Text', '') or 'Tctl/Tdie' in sensor.get('Text', ''):
                                                    temp_str = sensor.get('Value', '0.0').split(' ')[0]
                                                    try:
                                                        temp = float(temp_str)
                                                    except ValueError:
                                                        continue
                                                    break
                                            if temp is not None:
                                                break
                                    if temp is not None:
                                        break
                            if temp is not None:
                                break
                                
                        if temp is not None:
                            self._temp_cache = temp
                            self._temp_cache_time = current_time
                            if self.main_window:
                                self.main_window.update_lhm_pill(True)
                            return temp
                            
                        if self.main_window:
                            self.main_window.update_lhm_pill(False)
                        return None
                    else:
                        if self.main_window:
                            self.main_window.update_lhm_pill(False)
                        return None
        except Exception:
            if self.main_window:
                self.main_window.update_lhm_pill(False)
            return None
            
    async def main_loop(self):
        """Main calculation loop driven by PyQt timer."""
        # Always read temp to update dashboard
        cpu_temp = await self.get_cpu_gpu_temps()
        if cpu_temp is not None:
            if self.main_window:
                self.main_window.temp_gauge.set_value(cpu_temp)
                self.main_window.fan_curve.set_data(self.thresholds, cpu_temp)
                
        # If worker or ESP is not connected, stop commands but continue UI updates
        if not self.worker or not self.worker.esphome_client or not self.worker.esphome_client.is_connected:
            return
            
        try:
            if self.full_speed_mode:
                if self.last_fan_speed != 100:
                    self.worker.set_fan_speed_command(100)
                    self.last_fan_speed = 100
                return
                
            if self.manual_speed_mode:
                if self.last_fan_speed != self.manual_speed_value:
                    self.worker.set_fan_speed_command(self.manual_speed_value)
                    self.last_fan_speed = self.manual_speed_value
                return
                
            if self.automation_enabled and cpu_temp is not None:
                # Step curve logic: if temp is between t_i and t_{i+1}, speed is s_{i+1}
                sorted_thresholds = sorted(self.thresholds, key=lambda x: x[0])  # ascending temp
                
                if cpu_temp <= sorted_thresholds[0][0]:
                    target_speed = sorted_thresholds[0][1]
                elif cpu_temp > sorted_thresholds[-1][0]:
                    target_speed = sorted_thresholds[-1][1]
                else:
                    target_speed = sorted_thresholds[0][1]
                    for i in range(len(sorted_thresholds) - 1):
                        t1, s1 = sorted_thresholds[i]      # lower temp
                        t2, s2 = sorted_thresholds[i+1]    # higher temp
                        if t1 < cpu_temp <= t2:
                            target_speed = s2
                            break
                            
                if self.last_fan_speed != target_speed:
                    self.worker.set_fan_speed_command(target_speed)
                    self.last_fan_speed = target_speed
                    if self.main_window:
                        self.main_window.update_logic_label(f"Auto Mode: {cpu_temp:.1f}°C → {target_speed}% Speed")
                        
        except Exception as e:
            print(f"Error in controller main loop: {e}")


# Main PyQt6 GUI window

class OptimizedFanGUI(QMainWindow):
    def __init__(self, controller, parent=None):
        super().__init__(parent)
        self.controller = controller
        self.controller.main_window = self
        
        self.init_ui()
        self.setup_tray_icon()
        self.setup_timer()
        
        # Load window geometry
        self.load_window_state()
        # Start minimized to tray - do not call self.show()
        
        # Trigger target update now that GUI and signals are fully set up
        self.controller.worker.update_connection_target(ESPHOME_HOST, ESPHOME_PORT, ESPHOME_PASSWORD)
        
    def init_ui(self):
        self.setWindowTitle("Nexus ESPHome Fan Controller")
        self.setMinimumSize(380, 580)  # Narrower/shorter layout support
        # Set Window icon
        try:
            ico_path = get_resource_path(ICO_ICON_PATH)
            if os.path.exists(ico_path):
                self.setWindowIcon(QIcon(ico_path))
            else:
                png_path = get_resource_path(ICON_PATH)
                if os.path.exists(png_path):
                    self.setWindowIcon(QIcon(png_path))
        except Exception as e:
            print(f"Error setting app window icon: {e}")
            
        # Apply CSS style sheet
        self.setStyleSheet("""
            QMainWindow {
                background-color: #080b11;
            }
            QScrollArea {
                background-color: #080b11;
                border: none;
            }
            QWidget#scroll_widget {
                background-color: #080b11;
            }
            QFrame.card {
                background-color: #0e1320;
                border: 1px solid #1a2233;
                border-radius: 12px;
            }
            QLabel {
                color: #e2e8f0;
                font-family: 'Segoe UI', Arial, sans-serif;
            }
            QLineEdit {
                background-color: #05070a;
                border: 1px solid #1e293b;
                border-radius: 6px;
                padding: 6px 12px;
                color: #f1f5f9;
                font-size: 13px;
            }
            QLineEdit:focus {
                border: 1px solid #10b981;
            }
            QSpinBox {
                background-color: #05070a;
                border: 1px solid #1e293b;
                border-radius: 6px;
                padding: 5px 8px;
                color: #f1f5f9;
                font-size: 13px;
            }
            QSpinBox:focus {
                border: 1px solid #10b981;
            }
            QPushButton.btn-primary {
                background: qlineargradient(x1:0, y1:0, x2:1, y2:0, stop:0 #10b981, stop:1 #059669);
                color: #ffffff;
                border: none;
                border-radius: 6px;
                padding: 8px 16px;
                font-weight: bold;
                font-size: 13px;
            }
            QPushButton.btn-primary:hover {
                background: qlineargradient(x1:0, y1:0, x2:1, y2:0, stop:0 #34d399, stop:1 #10b981);
            }
            QPushButton.btn-primary:pressed {
                background: #047857;
            }
            QPushButton.btn-secondary {
                background-color: #111625;
                border: 1px solid #1a2233;
                border-radius: 6px;
                padding: 6px 12px;
                color: #94a3b8;
                font-weight: bold;
                font-size: 12px;
            }
            QPushButton.btn-secondary:hover {
                background-color: #1e293b;
                color: #f1f5f9;
                border-color: #334155;
            }
            QPushButton.btn-secondary:pressed {
                background-color: #0f172a;
            }
            QSlider::groove:horizontal {
                height: 6px;
                background-color: #111625;
                border-radius: 3px;
            }
            QSlider::sub-page:horizontal {
                background: qlineargradient(x1:0, y1:0, x2:1, y2:0, stop:0 #06b6d4, stop:1 #8b5cf6);
                border-radius: 3px;
            }
            QSlider::handle:horizontal {
                background: #ffffff;
                width: 18px;
                height: 18px;
                margin-top: -6px;
                margin-bottom: -6px;
                border-radius: 9px;
                border: 2px solid #8b5cf6;
            }
            QSlider::handle:horizontal:hover {
                border-color: #06b6d4;
            }
            QScrollBar:vertical {
                background-color: #080b11;
                width: 10px;
                border: none;
            }
            QScrollBar::handle:vertical {
                background-color: #111625;
                min-height: 20px;
                border-radius: 5px;
            }
            QScrollBar::handle:vertical:hover {
                background-color: #1e293b;
            }
            QScrollBar::add-line:vertical, QScrollBar::sub-line:vertical {
                height: 0px;
            }
        """)
        
        # Central Scroll area
        scroll = QScrollArea()
        scroll.setWidgetResizable(True)
        scroll.setHorizontalScrollBarPolicy(Qt.ScrollBarPolicy.ScrollBarAlwaysOff)
        
        scroll_widget = QWidget()
        scroll_widget.setObjectName("scroll_widget")
        
        # Main layout that spans our content
        main_h_layout = QHBoxLayout(scroll_widget)
        main_h_layout.setContentsMargins(16, 16, 16, 16)
        
        self.content_container = QWidget()
        self.content_container.setSizePolicy(QSizePolicy.Policy.Expanding, QSizePolicy.Policy.Preferred)
        
        self.content_layout = QVBoxLayout(self.content_container)
        self.content_layout.setContentsMargins(16, 16, 16, 16)
        self.content_layout.setSpacing(16)
        
        # ESP Fan Controller Title Label
        self.title_label = QLabel("ESP FAN\nCONTROLLER")
        self.title_label.setFont(QFont("Segoe UI", 26, QFont.Weight.Bold))
        self.title_label.setStyleSheet("color: #10b981; line-height: 1.0; font-family: 'Segoe UI', Arial, sans-serif;")
        
        # Keep references to pills to avoid breaking other logic, but keep them hidden
        self.conn_pill = QLabel()
        self.conn_pill.hide()
        self.lhm_pill = QLabel()
        self.lhm_pill.hide()
        
        # Create Sidebar Details Card (Left side in desktop, top stacked in mobile)
        self.sidebar_card = QFrame()
        self.sidebar_card.setProperty("class", "card")
        sidebar_layout = QVBoxLayout(self.sidebar_card)
        sidebar_layout.setContentsMargins(16, 16, 16, 16)
        sidebar_layout.setSpacing(14)
        
        sidebar_title = QLabel("Device Details")
        sidebar_title.setFont(QFont("Segoe UI", 12, QFont.Weight.Bold))
        sidebar_title.setStyleSheet("color: #94a3b8;")
        
        self.lbl_name = QLabel("Name: Silent Fan")
        self.lbl_name.setStyleSheet("color: #e2e8f0; font-size: 13px;")
        
        override_row = QHBoxLayout()
        override_lbl = QLabel("Full Speed Override:")
        override_lbl.setStyleSheet("color: #e2e8f0; font-size: 13px;")
        self.override_toggle = ToggleSwitch()
        self.override_toggle.clicked.connect(self.toggle_override_action)
        override_row.addWidget(override_lbl)
        override_row.addStretch()
        override_row.addWidget(self.override_toggle)
        
        self.lbl_fan_state = QLabel("Fan State: Disconnected")
        self.lbl_fan_state.setStyleSheet("color: #e2e8f0; font-size: 13px;")
        
        self.lbl_uptime = QLabel("Uptime: --")
        self.lbl_uptime.setStyleSheet("color: #e2e8f0; font-size: 13px;")
        
        self.lbl_wifi = QLabel("WiFi Signal: --")
        self.lbl_wifi.setStyleSheet("color: #e2e8f0; font-size: 13px;")
        
        web_btn = QPushButton("Open Web Console")
        web_btn.setProperty("class", "btn-secondary")
        web_btn.setFixedHeight(30)
        web_btn.setCursor(Qt.CursorShape.PointingHandCursor)
        web_btn.clicked.connect(self.open_web_console)
        
        sidebar_layout.addWidget(sidebar_title)
        sidebar_layout.addWidget(self.lbl_name)
        sidebar_layout.addLayout(override_row)
        sidebar_layout.addWidget(self.lbl_fan_state)
        sidebar_layout.addWidget(self.lbl_uptime)
        sidebar_layout.addWidget(self.lbl_wifi)
        sidebar_layout.addStretch()
        sidebar_layout.addWidget(web_btn)
        
        # Visual dashboard gauges card (no background card box)
        self.gauges_card = QFrame()
        self.gauges_card.setStyleSheet("background: transparent; border: none;")
        gauges_layout = QHBoxLayout(self.gauges_card)
        gauges_layout.setContentsMargins(6, 10, 6, 10)
        gauges_layout.setSpacing(10)
        
        self.temp_gauge = CircularGauge("CPU TEMP", "°C", 20, 90)
        self.temp_gauge.set_gradient_colors([
            (0.0, QColor("#eab308")),  # Yellow
            (1.0, QColor("#f97316"))   # Orange
        ])
        
        self.speed_gauge = CircularGauge("FAN SPEED", "%", 0, 100)
        self.speed_gauge.set_gradient_colors([
            (0.0, QColor("#8b5cf6")),  # Purple
            (1.0, QColor("#a855f7"))   # Magenta
        ])
        
        gauges_layout.addStretch()
        gauges_layout.addWidget(self.temp_gauge)
        gauges_layout.addSpacing(30)
        gauges_layout.addWidget(self.speed_gauge)
        gauges_layout.addStretch()
        
        # Fan curve graph card
        self.curve_card = QFrame()
        self.curve_card.setProperty("class", "card")
        curve_layout = QVBoxLayout(self.curve_card)
        curve_layout.setContentsMargins(16, 16, 16, 16)
        
        curve_title = QLabel("Temperature / Fan Speed Curve Map")
        curve_title.setFont(QFont("Segoe UI", 12, QFont.Weight.Bold))
        curve_title.setStyleSheet("color: #94a3b8;")
        
        self.fan_curve = FanCurveWidget()
        self.fan_curve.set_data(self.controller.thresholds)
        
        curve_layout.addWidget(curve_title)
        curve_layout.addSpacing(10)
        curve_layout.addWidget(self.fan_curve)
        
        # Connection settings card
        self.conn_card = QFrame()
        self.conn_card.setProperty("class", "card")
        conn_layout = QVBoxLayout(self.conn_card)
        conn_layout.setContentsMargins(16, 16, 16, 16)
        conn_layout.setSpacing(12)
        
        conn_title = QLabel("Connection Settings")
        conn_title.setFont(QFont("Segoe UI", 12, QFont.Weight.Bold))
        conn_title.setStyleSheet("color: #94a3b8;")
        
        ip_label = QLabel("ESP Host Address:")
        ip_label.setStyleSheet("color: #64748b; font-size: 11px; font-weight: bold;")
        
        self.ip_input = QLineEdit(ESPHOME_HOST)
        self.ip_input.setPlaceholderText("ESP Device IP Address")
        self.ip_input.setFixedHeight(34)
        
        conn_btn = QPushButton("Connect Device")
        conn_btn.setProperty("class", "btn-primary")
        conn_btn.setCursor(Qt.CursorShape.PointingHandCursor)
        conn_btn.clicked.connect(self.connect_esp_action)
        conn_btn.setFixedHeight(34)
        
        conn_layout.addWidget(conn_title)
        conn_layout.addWidget(ip_label)
        conn_layout.addWidget(self.ip_input)
        conn_layout.addWidget(conn_btn)
        
        # Mode & Manual speed control card
        self.mode_card = QFrame()
        self.mode_card.setProperty("class", "card")
        mode_layout = QVBoxLayout(self.mode_card)
        mode_layout.setContentsMargins(16, 16, 16, 16)
        mode_layout.setSpacing(12)
        
        mode_title = QLabel("Mode & Manual Control")
        mode_title.setFont(QFont("Segoe UI", 12, QFont.Weight.Bold))
        mode_title.setStyleSheet("color: #94a3b8;")
        
        auto_row = QHBoxLayout()
        auto_lbl = QLabel("Auto Fan Curve Mode:")
        auto_lbl.setStyleSheet("color: #e2e8f0; font-size: 13px; font-weight: 500;")
        self.auto_toggle = ToggleSwitch()
        self.auto_toggle.setChecked(self.controller.automation_enabled)
        self.auto_toggle.clicked.connect(self.toggle_automation_action)
        auto_row.addWidget(auto_lbl)
        auto_row.addStretch()
        auto_row.addWidget(self.auto_toggle)
        
        self.manual_label = QLabel("Manual Speed: 0%")
        self.manual_label.setStyleSheet("color: #64748b; font-size: 11px; font-weight: bold;")
        
        self.manual_slider = QSlider(Qt.Orientation.Horizontal)
        self.manual_slider.setMinimum(0)
        self.manual_slider.setMaximum(100)
        self.manual_slider.setValue(0)
        self.manual_slider.setFixedHeight(20)
        self.manual_slider.sliderMoved.connect(self.manual_slider_moved)
        self.manual_slider.sliderReleased.connect(self.manual_slider_released)
        self.manual_slider.setEnabled(not self.controller.automation_enabled)
        
        self.logic_label = QLabel("Logic: Waiting...")
        self.logic_label.setStyleSheet("color: #8b5cf6; font-size: 12px; font-weight: 500;")
        self.update_gui_state()
        
        mode_layout.addWidget(mode_title)
        mode_layout.addLayout(auto_row)
        mode_layout.addWidget(self.manual_label)
        mode_layout.addWidget(self.manual_slider)
        mode_layout.addWidget(self.logic_label)
        
        main_h_layout.addWidget(self.content_container)
        
        scroll.setWidget(scroll_widget)
        self.setCentralWidget(scroll)
        
    def resizeEvent(self, event):
        super().resizeEvent(event)
        width = self.width()
        is_wide = width >= 850
        if not hasattr(self, '_current_layout_mode') or self._current_layout_mode != is_wide:
            self._current_layout_mode = is_wide
            self.apply_layout(is_wide)
            
    def apply_layout(self, is_wide):
        # Clear content layout safely
        def clear_layout(layout):
            if layout is None:
                return
            while layout.count():
                item = layout.takeAt(0)
                widget = item.widget()
                if widget is not None:
                    widget.setParent(None)
                else:
                    sublayout = item.layout()
                    if sublayout is not None:
                        clear_layout(sublayout)
                        
        clear_layout(self.content_layout)
        
        if is_wide:
            # Desktop Wide Split layout matching user blueprint
            split_layout = QHBoxLayout()
            split_layout.setSpacing(20)
            
            # Left Column (Title, Gauges, Manual Mode stacked)
            left_container = QWidget()
            left_layout = QVBoxLayout(left_container)
            left_layout.setContentsMargins(10, 10, 10, 10)
            left_layout.setSpacing(20)
            
            # ESP Fan Controller Title
            left_layout.addWidget(self.title_label)
            
            # Gauges card (containing side-by-side gauges)
            self.gauges_card.setMinimumWidth(320)
            self.gauges_card.setMaximumWidth(450)
            self.gauges_card.setMinimumHeight(180)
            self.gauges_card.setMaximumHeight(300)
            left_layout.addWidget(self.gauges_card, 2)
            
            # Mode card
            self.mode_card.setMinimumWidth(320)
            self.mode_card.setMaximumWidth(450)
            self.mode_card.setMinimumHeight(140)
            self.mode_card.setMaximumHeight(180)
            left_layout.addWidget(self.mode_card, 1)
            
            # Add a vertical stretch at the bottom of the left column to keep widgets compact
            left_layout.addStretch(1)
            
            split_layout.addWidget(left_container, 2)
            
            # Right Column containing:
            # Top row: Device Details and Graph side-by-side
            # Bottom row: Connection card
            right_container = QWidget()
            right_layout = QVBoxLayout(right_container)
            right_layout.setContentsMargins(10, 10, 10, 10)
            right_layout.setSpacing(20)
            
            # Top Row: Device Details & Graph side-by-side
            top_row_container = QWidget()
            top_row_layout = QHBoxLayout(top_row_container)
            top_row_layout.setContentsMargins(0, 0, 0, 0)
            top_row_layout.setSpacing(20)
            
            self.sidebar_card.setMinimumWidth(280)
            self.sidebar_card.setMaximumWidth(320)
            self.sidebar_card.setMinimumHeight(220)
            # Remove maximum height so that sidebar_card matches the curve_card graph's height exactly
            top_row_layout.addWidget(self.sidebar_card)
            
            self.curve_card.setMinimumHeight(220)
            self.curve_card.setSizePolicy(QSizePolicy.Policy.Expanding, QSizePolicy.Policy.Expanding)
            top_row_layout.addWidget(self.curve_card, 1)
            
            right_layout.addWidget(top_row_container, 3)
            
            # Bottom Row: Connection card (Fixed height at the bottom to prevent overlapping)
            self.conn_card.setFixedHeight(180)
            right_layout.addWidget(self.conn_card)
            
            split_layout.addWidget(right_container, 3)
            
            self.content_layout.addLayout(split_layout)
            self.content_container.setMaximumWidth(16777215)
        else:
            # Mobile Stacked layout
            self.sidebar_card.setMinimumWidth(0)
            self.sidebar_card.setMaximumWidth(16777215)
            self.sidebar_card.setMinimumHeight(0)
            self.sidebar_card.setMaximumHeight(16777215)
            
            self.gauges_card.setMinimumWidth(0)
            self.gauges_card.setMaximumWidth(16777215)
            self.gauges_card.setMinimumHeight(0)
            self.gauges_card.setMaximumHeight(16777215)
            
            self.mode_card.setMinimumWidth(0)
            self.mode_card.setMaximumWidth(16777215)
            self.mode_card.setMinimumHeight(0)
            self.mode_card.setMaximumHeight(16777215)
            
            self.curve_card.setSizePolicy(QSizePolicy.Policy.Expanding, QSizePolicy.Policy.Preferred)
            self.curve_card.setMinimumHeight(240)
            self.conn_card.setMinimumHeight(0)
            self.conn_card.setMaximumHeight(16777215)
            
            self.content_layout.addWidget(self.title_label)
            self.content_layout.addWidget(self.gauges_card)
            self.content_layout.addWidget(self.curve_card)
            self.content_layout.addWidget(self.sidebar_card)
            self.content_layout.addWidget(self.conn_card)
            self.content_layout.addWidget(self.mode_card)
            
            self.content_container.setMaximumWidth(700)
            
    def update_entity_display(self, object_id, state):
        log_event(f"State Update [{object_id}]: {state}")
        
        if object_id == "full_speed_override":
            val = getattr(state, 'state', None)
            if val is not None:
                self.override_toggle.setChecked(val)
                self.controller.full_speed_mode = val
                
        elif object_id in ("wifi_signal_sensor", "wifi_signal_strength"):
            val = getattr(state, 'state', None)
            if val is not None:
                self.lbl_wifi.setText(f"WiFi Signal: {val:.0f} dBm")
                
        elif object_id in ("uptime_sensor", "uptime"):
            val = getattr(state, 'state', None)
            if val is not None:
                uptime_sec = float(val)
                d = int(uptime_sec // 86400)
                h = int((uptime_sec % 86400) // 3600)
                m = int((uptime_sec % 3600) // 60)
                if d > 0:
                    uptime_str = f"{d}d {h}h {m}m"
                else:
                    uptime_str = f"{h}h {m}m"
                self.lbl_uptime.setText(f"Uptime: {uptime_str}")
                
        elif object_id in ("pc_fan", "silent_fan"):
            is_on = getattr(state, 'state', False)
            speed = getattr(state, 'speed_level', 0)
            if is_on:
                self.lbl_fan_state.setText(f"Fan State: ON ({speed}%)")
                self.speed_gauge.set_value(speed)
            else:
                self.lbl_fan_state.setText("Fan State: OFF")
                self.speed_gauge.set_value(0)
                
    def open_web_console(self):
        import webbrowser
        ip = self.ip_input.text().strip()
        if ip:
            url = f"http://{ip}/"
            webbrowser.open(url)
            
    def toggle_override_action(self):
        state = self.override_toggle.isChecked()
        if self.controller.worker:
            self.controller.worker.schedule_task(
                self.controller.worker.esphome_client.set_switch_state("full_speed_override", state)
            )
        
    def update_connection_pill(self, state):
        self.conn_pill.setText(f"ESP: {state}")
        if state != "Connected":
            if hasattr(self, 'lbl_fan_state'):
                self.lbl_fan_state.setText("Fan State: Disconnected")
            if hasattr(self, 'lbl_uptime'):
                self.lbl_uptime.setText("Uptime: --")
            if hasattr(self, 'lbl_wifi'):
                self.lbl_wifi.setText("WiFi Signal: --")
                
        if state == "Connected":
            self.conn_pill.setStyleSheet("""
                background-color: rgba(16, 185, 129, 0.1); 
                color: #10b981; 
                border: 1px solid rgba(16, 185, 129, 0.2); 
                border-radius: 12px;
                font-size: 11px;
                font-weight: bold;
            """)
        elif state == "Connecting":
            self.conn_pill.setStyleSheet("""
                background-color: rgba(245, 158, 11, 0.1); 
                color: #f59e0b; 
                border: 1px solid rgba(245, 158, 11, 0.2); 
                border-radius: 12px;
                font-size: 11px;
                font-weight: bold;
            """)
        else:
            self.conn_pill.setStyleSheet("""
                background-color: rgba(239, 68, 68, 0.1); 
                color: #ef4444; 
                border: 1px solid rgba(239, 68, 68, 0.2); 
                border-radius: 12px;
                font-size: 11px;
                font-weight: bold;
            """)
            
    def update_lhm_pill(self, active):
        if active:
            self.lhm_pill.setText("LHM: Active")
            self.lhm_pill.setStyleSheet("""
                background-color: rgba(16, 185, 129, 0.1); 
                color: #10b981; 
                border: 1px solid rgba(16, 185, 129, 0.2); 
                border-radius: 12px;
                font-size: 11px;
                font-weight: bold;
            """)
        else:
            self.lhm_pill.setText("LHM: Inactive")
            self.lhm_pill.setStyleSheet("""
                background-color: rgba(239, 68, 68, 0.1); 
                color: #ef4444; 
                border: 1px solid rgba(239, 68, 68, 0.2); 
                border-radius: 12px;
                font-size: 11px;
                font-weight: bold;
            """)
            
    def update_fan_speed_display(self, speed):
        self.speed_gauge.set_value(speed)
        
    def update_logic_label(self, text):
        # Format logic text to match the screenshot exactly (arrow and styling)
        formatted_text = text.replace("→", "->")
        self.logic_label.setText(formatted_text)
        self.logic_label.setStyleSheet("color: #10b981; font-size: 13px; font-weight: bold;")
        
    def update_gui_state(self):
        auto = self.controller.automation_enabled
        self.auto_toggle.setChecked(auto)
        self.manual_slider.setEnabled(not auto)
        self.manual_slider.setValue(self.controller.manual_speed_value)
        self.manual_label.setText(f"Manual Speed: {self.controller.manual_speed_value}%")
        
        if auto:
            self.logic_label.setStyleSheet("color: #10b981; font-size: 13px; font-weight: bold;")
        else:
            self.logic_label.setText(f"Manual Mode: {self.controller.manual_speed_value}% Speed")
            self.logic_label.setStyleSheet("color: #06b6d4; font-size: 13px; font-weight: bold;")
            
        if hasattr(self, 'automation_action'):
            self.automation_action.setChecked(auto)
            
    def manual_slider_moved(self, value):
        self.manual_label.setText(f"Manual Speed: {value}%")
        
    def manual_slider_released(self):
        val = self.manual_slider.value()
        self.manual_label.setText(f"Manual Speed: {val}%")
        self.controller.update_manual_speed(val)
        
    def toggle_automation_action(self):
        auto = self.auto_toggle.isChecked()
        self.controller.automation_enabled = auto
        self.controller.manual_speed_mode = not auto
        self.controller.save_settings()
        self.update_gui_state()
        
    def connect_esp_action(self):
        ip = self.ip_input.text().strip()
        if not ip:
            QMessageBox.warning(self, "Validation Error", "Please provide a valid ESP IP Address.")
            return
            
        global ESPHOME_HOST
        ESPHOME_HOST = ip
        self.controller.save_settings()
        
        # Command connection target change in worker
        if self.controller.worker:
            self.controller.worker.update_connection_target(ESPHOME_HOST, ESPHOME_PORT, ESPHOME_PASSWORD)
            

        
    def load_window_state(self):
        if os.path.exists(SETTINGS_FILE):
            try:
                with open(SETTINGS_FILE, 'r') as f:
                    settings = json.load(f)
                    geometry = settings.get("window_geometry")
                    if geometry:
                        self.restoreGeometry(bytes.fromhex(geometry))
                    else:
                        self.resize(1100, 680)
            except Exception:
                self.resize(1100, 680)
        else:
            self.resize(1100, 680)
            
    def setup_tray_icon(self):
        self.tray_icon = QSystemTrayIcon(self)
        self.tray_icon.setToolTip("Nexus ESPHome Fan Controller")
        
        icon_set = False
        try:
            ico_path = get_resource_path(ICO_ICON_PATH)
            if os.path.exists(ico_path):
                self.tray_icon.setIcon(QIcon(ico_path))
                icon_set = True
        except Exception:
            pass
            
        if not icon_set:
            try:
                png_path = get_resource_path(ICON_PATH)
                if os.path.exists(png_path):
                    self.tray_icon.setIcon(QIcon(png_path))
                    icon_set = True
            except Exception:
                pass
                
        if not icon_set:
            self.tray_icon.setIcon(QApplication.style().standardIcon(QStyle.StandardPixmap.SP_ComputerIcon))
            
        tray_menu = QMenu()
        
        show_hide = QAction("Show/Hide Controller", self)
        show_hide.triggered.connect(self.toggle_window_visibility)
        tray_menu.addAction(show_hide)
        
        tray_menu.addSeparator()
        
        self.automation_action = QAction("Enable Automation Mode", self)
        self.automation_action.setCheckable(True)
        self.automation_action.setChecked(self.controller.automation_enabled)
        self.automation_action.triggered.connect(self.toggle_automation_from_tray)
        tray_menu.addAction(self.automation_action)
        
        self.full_speed_action = QAction("Full Speed Override", self)
        self.full_speed_action.setCheckable(True)
        self.full_speed_action.setChecked(self.controller.full_speed_mode)
        self.full_speed_action.triggered.connect(self.toggle_full_speed_from_tray)
        tray_menu.addAction(self.full_speed_action)
        
        tray_menu.addSeparator()
        
        reconnect = QAction("Force Reconnect", self)
        reconnect.triggered.connect(self.force_reconnect_action)
        tray_menu.addAction(reconnect)
        
        tray_menu.addSeparator()
        
        exit_act = QAction("Exit Application", self)
        exit_act.triggered.connect(self.exit_application)
        tray_menu.addAction(exit_act)
        
        self.tray_icon.setContextMenu(tray_menu)
        self.tray_icon.activated.connect(self.handle_tray_activated)
        self.tray_icon.show()
        
        # Visual startup hint
        self.tray_icon.showMessage(
            "Nexus Fan Control Active",
            "Double click or use context menu from tray to manage fans.",
            QSystemTrayIcon.MessageIcon.Information,
            3000
        )
        
    def handle_tray_activated(self, reason):
        if reason == QSystemTrayIcon.ActivationReason.Trigger or reason == QSystemTrayIcon.ActivationReason.DoubleClick:
            self.toggle_window_visibility()
            
    def toggle_window_visibility(self):
        if self.isVisible():
            self.hide()
        else:
            self.show()
            self.raise_()
            self.activateWindow()
            
    def toggle_automation_from_tray(self):
        auto = self.automation_action.isChecked()
        self.controller.automation_enabled = auto
        self.controller.manual_speed_mode = not auto
        self.controller.full_speed_mode = False
        self.full_speed_action.setChecked(False)
        self.controller.save_settings()
        self.update_gui_state()
        
    def toggle_full_speed_from_tray(self):
        full = self.full_speed_action.isChecked()
        self.controller.full_speed_mode = full
        if full:
            self.controller.automation_enabled = False
            self.controller.manual_speed_mode = False
            self.automation_action.setChecked(False)
        else:
            self.controller.automation_enabled = self.auto_toggle.isChecked()
            self.controller.manual_speed_mode = not self.controller.automation_enabled
            self.automation_action.setChecked(self.controller.automation_enabled)
        self.update_gui_state()
        
    def force_reconnect_action(self):
        if self.controller.worker:
            self.controller.worker.trigger_reconnect()
            
    def exit_application(self):
        self.tray_icon.hide()
        if self.controller.worker:
            if self.controller.worker.esphome_client:
                self.controller.worker.schedule_task(self.controller.worker.esphome_client.disconnect())
            self.controller.worker.stop_loop()
        QCoreApplication.quit()
        
    def setup_timer(self):
        self.timer = QTimer(self)
        self.timer.setInterval(MAIN_LOOP_INTERVAL)
        self.timer.timeout.connect(self.run_main_loop_task)
        self.timer.start()
        
    def run_main_loop_task(self):
        if self.controller.worker:
            self.controller.worker.schedule_task(self.controller.main_loop())
            
    def closeEvent(self, event):
        self.controller.save_settings()
        event.ignore()
        self.hide()


if __name__ == "__main__":
    try:
        QApplication.setAttribute(Qt.ApplicationAttribute.AA_EnableHighDpiScaling)
        QApplication.setAttribute(Qt.ApplicationAttribute.AA_UseHighDpiPixmaps)
    except Exception:
        pass
        
    app = QApplication(sys.argv)
    app.setStyle('Fusion')
    
    # Set global application window icon
    try:
        app_icon_path = get_resource_path(ICO_ICON_PATH)
        if os.path.exists(app_icon_path):
            app.setWindowIcon(QIcon(app_icon_path))
        else:
            app_icon_path = get_resource_path(ICON_PATH)
            if os.path.exists(app_icon_path):
                app.setWindowIcon(QIcon(app_icon_path))
    except Exception:
        pass
        
    controller = OptimizedFanController()
    worker = OptimizedAsyncioWorker()
    controller.set_worker(worker)
    
    gui = OptimizedFanGUI(controller)
    
    sys.exit(app.exec())
