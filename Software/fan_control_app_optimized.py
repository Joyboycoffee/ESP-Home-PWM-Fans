import sys
import os
import json
import asyncio
import threading
import traceback
import time
import aiohttp
from aioesphomeapi import APIClient, APIConnectionError
from PyQt6.QtWidgets import (
    QApplication, QMainWindow, QWidget, QVBoxLayout, QHBoxLayout,
    QLabel, QLineEdit, QPushButton, QSystemTrayIcon, QMenu, QStyle, QMessageBox, QSlider,
    QSpacerItem, QSizePolicy, QProgressBar, QScrollArea
)
from PyQt6.QtGui import QIcon, QAction, QPalette, QColor, QFont, QPainter, QPen, QBrush
from PyQt6.QtCore import Qt, pyqtSignal, QObject, QTimer, QCoreApplication, QThread, QSize

# Constants for the ESPHome device and settings file
ESPHOME_HOST = "192.168.1.40"
ESPHOME_PORT = 6053
ESPHOME_PASSWORD = ""
SETTINGS_FILE = "fan_settings.json"
TEMP_DATA_URL = "http://localhost:8085/data.json"
ICON_PATH = "logo.png"
ICO_ICON_PATH = "app_icon.ico"

# Function to get the correct path for bundled resources
def get_resource_path(relative_path):
    """Get absolute path to resource, works for dev and for PyInstaller"""
    try:
        # PyInstaller creates a temp folder and stores path in _MEIPASS
        base_path = sys._MEIPASS
    except Exception:
        base_path = os.path.abspath(".")
    return os.path.join(base_path, relative_path)

# OPTIMIZATION: Balanced intervals for good performance
MAIN_LOOP_INTERVAL = 3000         # 3 seconds as requested
BLINK_INTERVAL_SLOW = 1500        # 1.5 seconds for connection
BLINK_INTERVAL_MEDIUM = 2000      # 2 seconds for status
BLINK_INTERVAL_FAST = 1800        # 1.8 seconds for logic
OHM_CHECK_INTERVAL = 15           # 15 seconds for OHM checks
TEMP_CHANGE_THRESHOLD = 0.5       # 0.5°C for better responsiveness


class OptimizedAsyncioWorker(QObject):
    """Optimized worker class with reduced resource usage."""
    fan_speed_signal = pyqtSignal(int)
    status_signal = pyqtSignal(str)
    esphome_client_ready = pyqtSignal(object)

    def __init__(self, parent=None):
        super().__init__(parent)
        self.loop = asyncio.new_event_loop()
        self._reconnect_lock = threading.Lock()
        self._last_reconnect_attempt = 0
        self._reconnect_cooldown = 10  # Start with 10 seconds, will be reduced on failures
        self._connection_failures = 0  # Track consecutive failures
        threading.Thread(target=self.run_loop, daemon=True).start()

    def run_loop(self):
        """Starts the asyncio event loop on this thread."""
        asyncio.set_event_loop(self.loop)
        self.loop.run_forever()

    def stop_loop(self):
        """Stops the asyncio event loop."""
        self.loop.call_soon_threadsafe(self.loop.stop)

    def schedule_task(self, coro):
        """Schedules a coroutine to run on the asyncio event loop."""
        if self.loop.is_running():
            return asyncio.run_coroutine_threadsafe(coro, self.loop)

    async def async_connect(self, parent_controller=None):
        """Creates and connects the ESPHome client with improved retry logic."""
        current_time = time.time()
        if current_time - self._last_reconnect_attempt < self._reconnect_cooldown:
            return
            
        with self._reconnect_lock:
            try:
                self._last_reconnect_attempt = current_time
                self.status_signal.emit("Finding ESPHome device...")
                esphome_client = OptimizedESPHomeClient(ESPHOME_HOST, ESPHOME_PORT, ESPHOME_PASSWORD, parent_controller)
                if await esphome_client.connect():
                    self.esphome_client_ready.emit(esphome_client)
                    self.status_signal.emit("Connected to ESPHome device")
                    # Reset on successful connection
                    self._reconnect_cooldown = 10
                    self._connection_failures = 0
                else:
                    self.status_signal.emit("Connection failed - will retry")
                    self._connection_failures += 1
                    # Fast retries for first few failures, then slow down
                    if self._connection_failures <= 3:
                        self._reconnect_cooldown = 5  # 5 seconds for first 3 failures
                    elif self._connection_failures <= 6:
                        self._reconnect_cooldown = 15  # 15 seconds for next 3 failures
                    else:
                        self._reconnect_cooldown = 30  # 30 seconds after that
            except Exception as e:
                self.status_signal.emit(f"Connection error: {str(e)} - will retry")
                self._connection_failures += 1
                # Same logic for exceptions
                if self._connection_failures <= 3:
                    self._reconnect_cooldown = 5  # 5 seconds for first 3 failures
                elif self._connection_failures <= 6:
                    self._reconnect_cooldown = 15  # 15 seconds for next 3 failures
                else:
                    self._reconnect_cooldown = 30  # 30 seconds after that


class OptimizedESPHomeClient:
    """Optimized ESPHome client with connection pooling and caching."""
    def __init__(self, host, port, password, parent_controller=None):
        self.host = host
        self.port = port
        self.password = password
        self.client = None
        self.fan_entity = None
        self.is_connected = False
        self._connection_lock = asyncio.Lock()
        self._last_command_time = 0
        self._command_cooldown = 3.0  # Increased from 2.0 to 3.0 seconds
        self._last_successful_speed = None  # Cache last known good speed
        self._parent_controller = parent_controller

    async def connect(self):
        async with self._connection_lock:
            try:
                # Clean up any existing connection first
                if self.client:
                    try:
                        await self.client.disconnect()
                    except:
                        pass
                    self.client = None
                    
                self.client = APIClient(self.host, self.port, self.password)
                await self.client.connect(login=True)
                print("Connected to ESPHome device")
                self.is_connected = True
                await self.find_fan_entity()
                return self.is_connected and self.fan_entity is not None
            except Exception as e:
                print(f"Connection error: {e}")
                self.is_connected = False
                self.client = None
                self.fan_entity = None
                return False

    async def find_fan_entity(self):
        """Finds the 'silent_fan' entity."""
        try:
            entities_data = await self.client.list_entities_services()
            entities = []
            if isinstance(entities_data, tuple):
                entities.extend(entities_data[0])
            elif isinstance(entities_data, list):
                entities.extend(entities_data)

            for entity in entities:
                if getattr(entity, 'object_id', None) == "silent_fan":
                    self.fan_entity = entity
                    print(f"Found fan entity: {self.fan_entity.name}")
                    return True
            print("Fan with object_id 'silent_fan' not found.")
            return False
        except Exception as e:
            print(f"Error finding fan entity: {e}")
            self.fan_entity = None
            return False

    async def set_fan_speed(self, speed_percent):
        """Optimized fan speed command with caching and cooldown."""
        current_time = time.time()
        
        # OPTIMIZATION: Skip if same speed and within cooldown period
        if (self._last_successful_speed == speed_percent and 
            current_time - self._last_command_time < self._command_cooldown):
            return True
            
        if not self.client or not self.fan_entity:
            success = await self.connect()
            if not success:
                return False

        try:
            self._last_command_time = current_time
            print(f"Sending fan speed command: {speed_percent}%")
            
            cmd_result = None
            try:
                cmd_result = self.client.fan_command(
                    key=self.fan_entity.key,
                    state=speed_percent > 0,
                    speed_level=speed_percent
                )
            except TypeError:
                try:
                    cmd_result = self.client.fan_command(self.fan_entity.key, speed_percent > 0, speed_percent)
                except Exception as e:
                    print(f"Error calling fan_command (fallback): {e}")
                    raise

            if asyncio.iscoroutine(cmd_result):
                await cmd_result
                self._last_successful_speed = speed_percent
                return True

            if isinstance(cmd_result, asyncio.Future):
                await cmd_result
                self._last_successful_speed = speed_percent
                return True

            if cmd_result is None:
                # OPTIMIZATION: Reduced verification attempts and increased delay
                try:
                    for _ in range(2):  # Reduced from 4 to 2 attempts
                        entities_data = await self.client.list_entities_services()
                        ents = entities_data[0] if isinstance(entities_data, tuple) else entities_data
                        for e in ents:
                            if getattr(e, "object_id", None) == getattr(self.fan_entity, "object_id", "silent_fan"):
                                state = getattr(e, "state", None)
                                speed_attr = getattr(e, "speed", None) or getattr(e, "speed_level", None) or getattr(e, "percentage", None)
                                if speed_attr == speed_percent or state in ("on", "ON", True):
                                    self._last_successful_speed = speed_percent
                                    return True
                        await asyncio.sleep(1.0)  # Increased from 0.5 to 1.0 second
                    self._last_successful_speed = speed_percent
                    return True
                except Exception as e:
                    print(f"Verification error: {e}")
                    self._last_successful_speed = speed_percent
                    return True

            if callable(cmd_result):
                loop = asyncio.get_running_loop()
                await loop.run_in_executor(None, cmd_result)
                self._last_successful_speed = speed_percent
                return True

            print(f"Unexpected fan_command return type: {type(cmd_result)}")
            return False

        except Exception as e:
            print(f"Error sending fan command: {e}")
            self.is_connected = False
            # Signal disconnection to GUI
            if hasattr(self, '_parent_controller') and self._parent_controller:
                if self._parent_controller.main_window:
                    self._parent_controller.main_window.set_connection_disconnected()
            return False

    async def disconnect(self):
        """Gracefully disconnects from the ESPHome device."""
        if self.client:
            try:
                await self.client.disconnect()
            except Exception as e:
                print(f"Error during disconnect: {e}")
            finally:
                self.client = None
                self.is_connected = False
                self.fan_entity = None


class OptimizedFanController(QObject):
    """Optimized fan controller with intelligent caching and reduced polling."""
    def __init__(self, parent=None):
        super().__init__(parent)
        self.main_window = None
        self.worker = None
        self.esphome = None
        self.manual_speed_mode = False
        self.full_speed_mode = False
        self.manual_speed_value = 0
        self.last_fan_speed = 0
        self.automation_enabled = False
        
        # OPTIMIZATION: Enhanced caching
        self._temp_cache = None
        self._temp_cache_time = 0
        self._temp_cache_duration = 2  # Cache temperature for 2 seconds
        self._last_significant_temp_change = 0
        self._last_connection_check = 0
        self._connection_check_interval = 9  # Check connection health every 9 seconds (3 main loop cycles)
        
        self.thresholds = [
            (30, 0), (40, 25), (50, 50), (60, 75), (70, 100)
        ]
        self.load_settings()
        self._last_command_time = 0
        self._command_cooldown = 3.0  # Increased cooldown
        print("OptimizedFanController initialized.")

    def set_worker(self, worker):
        self.worker = worker
        self.worker.esphome_client_ready.connect(self.set_esphome_client)
        self.worker.status_signal.connect(self.update_gui_status)
        self.worker.schedule_task(self.worker.async_connect(self))

    def set_esphome_client(self, client):
        self.esphome = client
        print("ESPHomeClient received and set in OptimizedFanController.")

    def update_gui_status(self, message):
        if self.main_window:
            self.main_window.update_status_message(message)

    def load_settings(self):
        """Loads thresholds and automation state from JSON file."""
        global ESPHOME_HOST
        if os.path.exists(SETTINGS_FILE):
            try:
                with open(SETTINGS_FILE, 'r') as f:
                    settings = json.load(f)
                    self.thresholds = settings.get("thresholds", self.thresholds)
                    self.automation_enabled = settings.get("automation_enabled", False)
                    # Load saved ESP host
                    saved_host = settings.get("esp_host", ESPHOME_HOST)
                    ESPHOME_HOST = saved_host
                    if self.main_window:
                        geo = settings.get("geometry")
                        if geo:
                            self.main_window.restoreGeometry(bytes.fromhex(geo))
                    print("Settings loaded successfully.")
            except (IOError, json.JSONDecodeError) as e:
                print(f"Error loading settings file: {e}")
        else:
            print("Settings file not found. Using default values.")

    def save_settings(self):
        """Saves current settings to JSON file."""
        settings = {
            "thresholds": self.thresholds,
            "automation_enabled": self.automation_enabled
        }
        if self.main_window:
            settings["geometry"] = self.main_window.saveGeometry().toHex().data().decode("utf-8")

        try:
            with open(SETTINGS_FILE, 'w') as f:
                json.dump(settings, f, indent=4)
            print("Settings saved successfully.")
        except IOError as e:
            print(f"Error saving settings file: {e}")

    def update_thresholds_from_gui(self, new_thresholds):
        """Updates internal thresholds and saves them."""
        self.thresholds = new_thresholds
        self.save_settings()

    def update_manual_speed(self, speed):
        self.manual_speed_value = speed
        if self.worker and self.esphome:
            self.worker.schedule_task(self.esphome.set_fan_speed(self.manual_speed_value))

    async def get_cpu_gpu_temps(self):
        """Optimized temperature fetching with intelligent caching."""
        current_time = time.time()
        
        # OPTIMIZATION: Use cached temperature if still valid
        if (self._temp_cache is not None and 
            current_time - self._temp_cache_time < self._temp_cache_duration):
            return self._temp_cache
            
        try:
            # OPTIMIZATION: Shorter timeout to prevent hanging
            timeout = aiohttp.ClientTimeout(total=2)
            async with aiohttp.ClientSession(timeout=timeout) as session:
                async with session.get(TEMP_DATA_URL) as response:
                    if response.status == 200:
                        data = await response.json()
                        
                        for computer in data['Children'][0]['Children']:
                            if 'AMD Ryzen' in computer['Text']:
                                for section in computer['Children']:
                                    if section['Text'] == 'Temperatures':
                                        for temp in section['Children']:
                                            if temp['Text'] == 'CPU Package':
                                                temp_str = temp['Value'].split(' ')[0]
                                                temp = float(temp_str)
                                                
                                                # OPTIMIZATION: Cache the temperature
                                                self._temp_cache = temp
                                                self._temp_cache_time = current_time
                                                
                                                # Update GUI that OHM is working
                                                if self.main_window:
                                                    self.main_window.update_ohm_status(True)
                                                
                                                print(f"Found CPU temperature: {temp}°C")
                                                return temp
                        
                        print("CPU temperature not found in data")
                        # Update GUI that OHM is not working properly
                        if self.main_window:
                            self.main_window.update_ohm_status(False)
                        return None
                    else:
                        print(f"HTTP Error: {response.status}")
                        # Update GUI that OHM is not working properly
                        if self.main_window:
                            self.main_window.update_ohm_status(False)
                        return None
        except Exception as e:
            print(f"Error fetching temperature: {e}")
            # Update GUI that OHM is not working properly
            if self.main_window:
                self.main_window.update_ohm_status(False)
            return None

    async def main_loop(self):
        """Optimized main loop with intelligent updates and reconnection logic."""
        if not self.esphome:
            # Try to reconnect if we don't have an ESPHome client
            if self.worker:
                self.worker.schedule_task(self.worker.async_connect(self))
            return

        # Periodic connection health check
        current_time = time.time()
        if current_time - self._last_connection_check > self._connection_check_interval:
            self._last_connection_check = current_time
            
            # Check if ESPHome connection is still healthy
            if self.esphome and not self.esphome.is_connected:
                print("ESPHome connection lost, attempting reconnection...")
                # Connection was lost, try to reconnect
                if self.main_window:
                    self.main_window.set_connection_connecting()
                self.worker.schedule_task(self.worker.async_connect(self))
                self.esphome = None  # Clear the old client
                return
            elif not self.esphome:
                print("No ESPHome client, attempting connection...")
                # No client at all, try to connect
                if self.main_window:
                    self.main_window.set_connection_connecting()
                self.worker.schedule_task(self.worker.async_connect(self))
                return
            
        try:
            if self.full_speed_mode:
                if self.last_fan_speed != 100:
                    success = await self.esphome.set_fan_speed(100)
                    if success:
                        self.last_fan_speed = 100
                        if self.main_window:
                            self.main_window.update_current_speed(100)
                        print("Full speed mode active")
                return

            if self.manual_speed_mode:
                if self.last_fan_speed != self.manual_speed_value:
                    success = await self.esphome.set_fan_speed(self.manual_speed_value)
                    if success:
                        self.last_fan_speed = self.manual_speed_value
                        if self.main_window:
                            self.main_window.update_current_speed(self.manual_speed_value)
                        print(f"Manual mode - Speed: {self.manual_speed_value}%")
                return

            if self.automation_enabled:
                cpu_temp = await self.get_cpu_gpu_temps()
                if cpu_temp is not None:
                    current_time = time.time()
                    
                    # Process temperature and update GUI every time
                    print(f"Processing temperature: {cpu_temp}°C")
                    target_speed = 0
                    
                    sorted_thresholds = sorted(self.thresholds, key=lambda x: x[0], reverse=True)
                    
                    for temp, speed in sorted_thresholds:
                        if cpu_temp >= temp:
                            target_speed = speed
                            print(f"Temperature {cpu_temp}°C >= {temp}°C -> Setting fan to {speed}%")
                            # Always update GUI with current logic
                            if self.main_window:
                                self.main_window.update_logic(cpu_temp, temp, speed)
                                self.main_window.update_cpu_temperature(cpu_temp)
                            break
                    
                    if self.last_fan_speed != target_speed:
                        success = await self.esphome.set_fan_speed(target_speed)
                        if success:
                            self.last_fan_speed = target_speed
                            self.manual_speed_mode = False
                            if self.main_window:
                                self.main_window.update_current_speed(target_speed)
                            self.update_gui_status(f"Auto: {cpu_temp:.1f}°C → {target_speed}% fan")
                        else:
                            # ESPHome command failed, try to reconnect
                            if self.main_window:
                                self.main_window.set_connection_connecting()
                            self.worker.schedule_task(self.worker.async_connect(self))

        except Exception as e:
            print(f"Error in main loop: {e}")
            import traceback
            traceback.print_exc()

    def shutdown_app(self):
        if self.esphome:
            self.worker.schedule_task(self.esphome.disconnect())
        self.worker.stop_loop()


class ToggleSwitch(QPushButton):
    """Optimized toggle switch with reduced paint events."""
    def __init__(self, parent=None):
        super().__init__(parent)
        self.setCheckable(True)
        self._on_color1 = QColor("#8a2be2")
        self._on_color2 = QColor("#ff1493")
        self._off_color = QColor("#334155")
        self._knob_color = QColor("#ffffff")
        self.setCursor(Qt.CursorShape.PointingHandCursor)
        self.setFixedSize(60, 30)
        self.setStyleSheet("border: none;")

    def sizeHint(self):
        return QSize(60, 30)

    def paintEvent(self, event):
        radius = self.height() // 2
        knob_margin = 3
        painter = QPainter(self)
        painter.setRenderHint(QPainter.RenderHint.Antialiasing)

        track_rect = self.rect()
        painter.setPen(Qt.PenStyle.NoPen)
        
        if self.isChecked():
            painter.setBrush(QBrush(self._on_color1))
            painter.drawRoundedRect(0, 0, self.width(), self.height(), radius, radius)
            painter.setBrush(QBrush(self._on_color2))
            painter.drawRoundedRect(self.width()//3, 0, self.width()*2//3, self.height(), radius, radius)
        else:
            painter.setBrush(QBrush(self._off_color))
            painter.drawRoundedRect(0, 0, self.width(), self.height(), radius, radius)

        knob_d = self.height() - 2*knob_margin
        if self.isChecked():
            x = self.width() - knob_margin - knob_d
        else:
            x = knob_margin

        shadow_color = QColor(0, 0, 0, 40)
        painter.setBrush(QBrush(shadow_color))
        painter.drawEllipse(x+1, knob_margin+1, knob_d, knob_d)
        
        painter.setBrush(QBrush(self._knob_color))
        painter.setPen(QPen(QColor(200, 200, 200), 1))
        painter.drawEllipse(x, knob_margin, knob_d, knob_d)


class OptimizedFanGUI(QMainWindow):
    """Optimized GUI with reduced CPU usage and smart updates."""
    def __init__(self, controller, parent=None):
        super().__init__(parent)
        self.controller = controller
        self.controller.main_window = self
        
        # OPTIMIZATION: Lazy initialization and caching
        self._ohm_status_cache = None
        self._ohm_status_cache_time = 0
        self._last_temp_update = 0
        
        self.init_ui()
        self.setup_tray_icon()
        self.setup_timer()
        # Load settings first to restore window geometry
        self.controller.load_settings()
        self.load_window_geometry()  # Load geometry after UI is created
        self.load_esp_ip_from_settings()  # Load saved ESP IP
        self.update_gui_state()
        self.check_ohm_status()
        # Start hidden in system tray instead of showing window
        self.hide()

    def init_ui(self):
        # Window setup
        self.setWindowTitle("ESP Home Fan Controller")
        # Set proper minimum size to prevent UI overlap
        self.setMinimumSize(580, 720)  # Increased minimum width and height
        # Set maximum width to prevent excessive stretching
        self.setMaximumWidth(800)
        
        # Set window icon
        try:
            icon_path = get_resource_path(ICO_ICON_PATH)
            if os.path.exists(icon_path):
                self.setWindowIcon(QIcon(icon_path))
            else:
                # Fallback to PNG if ICO not found
                png_icon_path = get_resource_path(ICON_PATH)
                if os.path.exists(png_icon_path):
                    self.setWindowIcon(QIcon(png_icon_path))
                else:
                    print("WARNING: No icon files found for window icon")
        except Exception as e:
            print(f"Error setting window icon: {e}")
        
        self.set_dark_theme()

        # Create scroll area
        scroll_area = QScrollArea()
        scroll_area.setWidgetResizable(True)
        scroll_area.setHorizontalScrollBarPolicy(Qt.ScrollBarPolicy.ScrollBarAlwaysOff)
        scroll_area.setVerticalScrollBarPolicy(Qt.ScrollBarPolicy.ScrollBarAsNeeded)
        scroll_area.setStyleSheet("""
            QScrollArea {
                background-color: #0f172a;
                border: none;
            }
            QScrollBar:vertical {
                background-color: #334155;
                width: 12px;
                border-radius: 6px;
                border: none;
            }
            QScrollBar::handle:vertical {
                background-color: #8a2be2;
                border-radius: 6px;
                min-height: 20px;
            }
            QScrollBar::handle:vertical:hover {
                background-color: #ff1493;
            }
            QScrollBar::add-line:vertical {
                height: 0px;
                subcontrol-position: bottom;
                subcontrol-origin: margin;
            }
            QScrollBar::sub-line:vertical {
                height: 0px;
                subcontrol-position: top;
                subcontrol-origin: margin;
            }
            QScrollBar::up-arrow:vertical, QScrollBar::down-arrow:vertical {
                width: 0px;
                height: 0px;
                background: none;
            }
            QScrollBar::add-page:vertical, QScrollBar::sub-page:vertical {
                background: none;
            }
        """)
        self.setCentralWidget(scroll_area)

        main_widget = QWidget()
        main_widget.setSizePolicy(QSizePolicy.Policy.Preferred, QSizePolicy.Policy.Minimum)
        scroll_area.setWidget(main_widget)
        main_layout = QVBoxLayout(main_widget)
        # Reduce margins for better space utilization while maintaining responsive padding
        main_layout.setContentsMargins(20, 20, 20, 20)

        # Title
        title_font = QFont("Segoe UI", 18, QFont.Weight.Bold)
        label_title = QLabel("ESP Home Fan Controller")
        label_title.setFont(title_font)
        label_title.setAlignment(Qt.AlignmentFlag.AlignCenter)
        label_title.setStyleSheet("color: white; margin-bottom: 10px;")
        
        title_line = QWidget()
        title_line.setFixedHeight(3)
        title_line.setStyleSheet("background-color: qlineargradient(x1:0, y1:0, x2:1, y2:0, stop:0 #8a2be2, stop:1 #ff1493); border-radius: 1px;")
        
        main_layout.addWidget(label_title)
        main_layout.addWidget(title_line)
        main_layout.addSpacing(20)
        
        # Create UI components
        self.create_esp_connection_panel(main_layout)
        main_layout.addSpacing(20)
        
        self.create_cpu_temp_display(main_layout)
        main_layout.addSpacing(20)
        
        self.create_temp_speed_controls(main_layout)
        main_layout.addSpacing(20)
        
        self.create_bottom_controls(main_layout)
        main_layout.addStretch()
        
    def create_panel(self, title, content_widget):
        panel_widget = QWidget()
        panel_layout = QVBoxLayout(panel_widget)
        panel_layout.setContentsMargins(20, 20, 20, 20)
        panel_layout.setSpacing(15)
        panel_widget.setStyleSheet("""
            QWidget {
                background-color: #1e293b;
                border-radius: 12px;
            }
        """)

        title_label = QLabel(title)
        title_label.setFont(QFont("Segoe UI", 14, QFont.Weight.Bold))
        title_label.setStyleSheet("color: white;")
        
        panel_layout.addWidget(title_label)
        panel_layout.addWidget(content_widget)
        return panel_widget

    def create_esp_connection_panel(self, layout):
        """Create the ESP device connection panel"""
        esp_panel = QWidget()
        esp_panel.setStyleSheet("""
            QWidget {
                background-color: #1e293b;
                border-radius: 12px;
                padding: 15px 20px;
            }
        """)
        
        esp_layout = QVBoxLayout(esp_panel)
        esp_layout.setContentsMargins(0, 0, 0, 0)
        esp_layout.setSpacing(12)
        
        # Title
        title_label = QLabel("ESP Device Connection")
        title_label.setStyleSheet("color: white; font-size: 16px; font-weight: 700;")
        
        # Connection row
        conn_row = QHBoxLayout()
        conn_row.setSpacing(10)
        
        # IP Address input
        ip_label = QLabel("ESP IP :")
        ip_label.setStyleSheet("color: white; font-size: 14px; font-weight: 600;")
        ip_label.setMinimumWidth(80)
        ip_label.setMaximumWidth(100)
        
        self.ip_input = QLineEdit(ESPHOME_HOST)
        self.ip_input.setFixedHeight(35)
        self.ip_input.setMinimumWidth(200)  # Minimum width for IP input
        self.ip_input.setPlaceholderText("Enter ESP device IP (e.g., 192.168.1.42)")
        self.ip_input.setStyleSheet("""
            QLineEdit {
                background-color: #0f172a;
                border: 2px solid #8a2be2;
                border-radius: 8px;
                padding: 8px 12px;
                color: white;
                font-size: 14px;
                font-family: 'Segoe UI';
            }
            QLineEdit:focus {
                border: 2px solid #ff1493;
                background-color: #1e293b;
            }
            QLineEdit::placeholder {
                color: #94a3b8;
            }
        """)
        
        # Connect button
        self.connect_btn = QPushButton("Connect to ESP")
        self.connect_btn.setFixedHeight(35)
        self.connect_btn.setFixedWidth(140)
        self.connect_btn.clicked.connect(self.connect_to_esp_device)
        self.connect_btn.setStyleSheet("""
            QPushButton {
                background-color: #8a2be2;
                color: white;
                border: none;
                border-radius: 8px;
                font-size: 14px;
                font-weight: 600;
                padding: 8px 16px;
            }
            QPushButton:hover {
                background-color: #ff1493;
            }
            QPushButton:pressed {
                background-color: #6a1b9a;
            }
            QPushButton:disabled {
                background-color: #334155;
                color: #94a3b8;
            }
        """)
        
        # Connection status
        self.connection_status_label = QLabel("Status: Not connected")
        self.connection_status_label.setStyleSheet("color: #ef4444; font-size: 12px; font-weight: 500;")
        
        # Add to layout with stretch factors
        conn_row.addWidget(ip_label, 0)  # Don't stretch label
        conn_row.addWidget(self.ip_input, 1)  # Allow input to stretch
        conn_row.addWidget(self.connect_btn, 0)  # Don't stretch button
        
        esp_layout.addWidget(title_label)
        esp_layout.addLayout(conn_row)
        esp_layout.addWidget(self.connection_status_label)
        
        layout.addWidget(esp_panel)
    
    def create_cpu_temp_display(self, layout):
        """Create the compact CPU temperature display panel"""
        temp_panel = QWidget()
        temp_panel.setStyleSheet("""
            QWidget {
                background-color: #1e293b;
                border-radius: 12px;
                padding: 15px 20px;
            }
        """)
        
        temp_layout = QHBoxLayout(temp_panel)
        temp_layout.setContentsMargins(0, 0, 0, 0)
        temp_layout.setSpacing(15)
        
        title_label = QLabel("CPU Temperature:")
        title_label.setStyleSheet("color: white; font-size: 20px; font-weight: 700;")
        
        self.cpu_temp_label = QLabel("--°C")
        self.cpu_temp_label.setStyleSheet("color: #8a2be2; font-size: 18px; font-weight: bold;")
        
        # Add widgets with proper stretch factors
        temp_layout.addWidget(title_label, 0)  # Don't stretch title
        temp_layout.addWidget(self.cpu_temp_label, 0)  # Don't stretch temp value
        temp_layout.addStretch(1)  # Add flexible space
        
        layout.addWidget(temp_panel)
    
    def load_window_geometry(self):
        """Load window geometry from settings"""
        try:
            if os.path.exists(SETTINGS_FILE):
                with open(SETTINGS_FILE, 'r') as f:
                    settings = json.load(f)
                    geometry = settings.get("window_geometry")
                    if geometry:
                        # Restore window geometry
                        self.restoreGeometry(bytes.fromhex(geometry))
                    else:
                        # Set default size if no geometry saved
                        self.resize(600, 750)  # Better proportions for new UI
                        self.center_window()
            else:
                # Set default size for first run
                self.resize(600, 750)  # Better proportions for new UI
                self.center_window()
        except Exception as e:
            print(f"Error loading window geometry: {e}")
            # Fallback to default size
            self.resize(600, 750)  # Better proportions for new UI
            self.center_window()
    
    def center_window(self):
        """Center the window on screen"""
        screen = QApplication.primaryScreen().geometry()
        size = self.geometry()
        x = (screen.width() - size.width()) // 2
        y = (screen.height() - size.height()) // 2
        self.move(x, y)
    
    def save_window_geometry(self):
        """Save current window geometry"""
        try:
            settings = {}
            if os.path.exists(SETTINGS_FILE):
                with open(SETTINGS_FILE, 'r') as f:
                    settings = json.load(f)
            
            # Save current geometry
            settings["window_geometry"] = self.saveGeometry().toHex().data().decode("utf-8")
            
            with open(SETTINGS_FILE, 'w') as f:
                json.dump(settings, f, indent=4)
        except Exception as e:
            print(f"Error saving window geometry: {e}")
    
    def create_temp_speed_controls(self, layout):
        """Create the temperature and speed control rows"""
        controls_widget = QWidget()
        controls_layout = QVBoxLayout(controls_widget)
        controls_layout.setSpacing(12)
        controls_layout.setContentsMargins(0, 0, 0, 0)
        
        self.threshold_inputs = []
        
        for i, (temp, speed) in enumerate(self.controller.thresholds):
            row_widget = self.create_temp_speed_row(i + 1, temp, speed)
            controls_layout.addWidget(row_widget)
            
        layout.addWidget(controls_widget)
    
    def create_temp_speed_row(self, index, temp_value, speed_value):
        """Create a single temperature/speed control row"""
        row_widget = QWidget()
        row_widget.setStyleSheet("""
            QWidget {
                background-color: #1e293b;
                border-radius: 8px;
                padding: 12px 16px;
            }
        """)
        
        row_layout = QHBoxLayout(row_widget)
        row_layout.setSpacing(16)
        
        temp_label = QLabel(f"Temp {index}:")
        temp_label.setStyleSheet("color: white; font-size: 13px; font-weight: 600;")
        temp_label.setMinimumWidth(70)
        temp_label.setMaximumWidth(90)
        
        temp_icon = QLabel("°C")
        temp_icon.setStyleSheet("color: #8a2be2; font-size: 14px; font-weight: bold;")
        temp_icon.setFixedWidth(25)
        temp_icon.setAlignment(Qt.AlignmentFlag.AlignCenter)
        
        temp_input = QLineEdit(str(temp_value))
        temp_input.setFixedHeight(32)
        temp_input.setMinimumWidth(60)
        temp_input.setMaximumWidth(100)
        temp_input.setStyleSheet("""
            QLineEdit {
                background-color: #0f172a;
                border: 1px solid #8a2be2;
                border-radius: 6px;
                padding: 4px 8px;
                color: white;
                font-size: 13px;
            }
            QLineEdit:focus {
                border: 2px solid #8a2be2;
            }
        """)
        
        temp_unit = QLabel("°C")
        temp_unit.setStyleSheet("color: #8a2be2; font-size: 13px; font-weight: 600;")
        temp_unit.setFixedWidth(30)
        
        speed_label = QLabel(f"Speed {index}:")
        speed_label.setStyleSheet("color: white; font-size: 13px; font-weight: 600;")
        speed_label.setMinimumWidth(75)
        speed_label.setMaximumWidth(95)
        
        speed_icon = QLabel("%")
        speed_icon.setStyleSheet("color: #ff1493; font-size: 14px; font-weight: bold;")
        speed_icon.setFixedWidth(25)
        speed_icon.setAlignment(Qt.AlignmentFlag.AlignCenter)
        
        speed_input = QLineEdit(str(speed_value))
        speed_input.setFixedHeight(32)
        speed_input.setMinimumWidth(60)
        speed_input.setMaximumWidth(100)
        speed_input.setStyleSheet("""
            QLineEdit {
                background-color: #0f172a;
                border: 1px solid #ff1493;
                border-radius: 6px;
                padding: 4px 8px;
                color: white;
                font-size: 13px;
            }
            QLineEdit:focus {
                border: 2px solid #ff1493;
            }
        """)
        
        speed_unit = QLabel("%")
        speed_unit.setStyleSheet("color: #ff1493; font-size: 13px; font-weight: 600;")
        speed_unit.setFixedWidth(30)
        
        # Add widgets with proper stretch factors for responsive layout
        row_layout.addWidget(temp_label, 0)
        row_layout.addWidget(temp_icon, 0)
        row_layout.addWidget(temp_input, 0)
        row_layout.addWidget(temp_unit, 0)
        row_layout.addStretch(1)  # Add flexible space in the middle
        row_layout.addWidget(speed_label, 0)
        row_layout.addWidget(speed_icon, 0)
        row_layout.addWidget(speed_input, 0)
        row_layout.addWidget(speed_unit, 0)
        
        self.threshold_inputs.append((temp_input, speed_input))
        
        return row_widget
    
    def create_bottom_controls(self, layout):
        """Create bottom section with save button"""
        save_button = QPushButton("Save Thresholds")
        save_button.clicked.connect(self.save_settings)
        save_button.setFixedHeight(40)
        save_button.setStyleSheet("""
            QPushButton {
                background-color: #6a1b9a;
                color: white;
                border: none;
                padding: 10px 20px;
                border-radius: 8px;
                font-size: 14px;
                font-weight: 600;
            }
            QPushButton:hover { background-color: #8a2be2; }
            QPushButton:pressed { background-color: #5b1790; }
        """)
        layout.addWidget(save_button)
        layout.addSpacing(25)
        
        layout.addWidget(self.create_panel("Fan Speed Control", self.create_unified_speed_ui()))
        layout.addSpacing(25)
        layout.addWidget(self.create_panel("Automation Control", self.create_automation_control_ui()))
        layout.addSpacing(25)
        layout.addWidget(self.create_panel("Status", self.create_optimized_status_ui()))

    def create_unified_speed_ui(self):
        content_widget = QWidget()
        layout = QVBoxLayout(content_widget)
        layout.setContentsMargins(0, 0, 0, 0)
        layout.setSpacing(15)
        
        self.speed_label = QLabel("Current Speed: 0%")
        self.speed_label.setAlignment(Qt.AlignmentFlag.AlignCenter)
        self.speed_label.setStyleSheet("color: white; font-size: 16px; font-weight: bold;")
        
        self.speed_slider = QSlider(Qt.Orientation.Horizontal)
        self.speed_slider.setMinimum(0)
        self.speed_slider.setMaximum(100)
        self.speed_slider.setValue(0)
        self.speed_slider.setFixedHeight(40)
        self.speed_slider.sliderPressed.connect(self.on_slider_pressed)
        self.speed_slider.valueChanged.connect(self.on_speed_slider_changed)
        
        self.speed_slider.setStyleSheet("""
            QSlider::groove:horizontal {
                height: 12px;
                background-color: qlineargradient(x1:0, y1:0, x2:1, y2:0, 
                    stop:0 #334155, stop:0.01 #334155,
                    stop:0.01 #8a2be2, stop:0.5 #8a2be2, stop:1 #ff1493);
                margin: 12px 10px;
                border-radius: 6px;
            }
            QSlider::handle:horizontal {
                background: #ffffff;
                width: 22px;
                height: 22px;
                margin: -8px -8px;
                border-radius: 12px;
                border: 3px solid #8a2be2;
            }
            QSlider::handle:horizontal:hover {
                background: #f0f0f0;
                border: 3px solid #ff1493;
            }
            QSlider::handle:horizontal:pressed {
                background: #e0e0e0;
            }
        """)
        
        self.mode_label = QLabel("Manual Control")
        self.mode_label.setAlignment(Qt.AlignmentFlag.AlignCenter)
        self.mode_label.setStyleSheet("color: #94a3b8; font-size: 12px;")
        
        layout.addWidget(self.speed_label)
        layout.addWidget(self.speed_slider)
        layout.addWidget(self.mode_label)
        return content_widget

    def create_automation_control_ui(self):
        content_widget = QWidget()
        layout = QVBoxLayout(content_widget)
        layout.setContentsMargins(0, 0, 0, 0)
        layout.setSpacing(10)

        auto_row = QHBoxLayout()
        auto_label = QLabel("Automation Control:")
        auto_label.setStyleSheet("color: white; font-size: 14px;")
        
        self.automation_toggle = ToggleSwitch()
        self.automation_toggle.setChecked(self.controller.automation_enabled)
        self.automation_toggle.clicked.connect(self.toggle_automation_from_gui)
        
        self.automation_status_label = QLabel("Automation Disabled")
        self.automation_status_label.setStyleSheet("color: #94a3b8; font-size: 12px;")
        if self.controller.automation_enabled:
            self.automation_status_label.setText("Automation Enabled")
            self.automation_status_label.setStyleSheet("color: #22c55e; font-size: 12px;")
        
        auto_row.addWidget(auto_label)
        auto_row.addStretch()
        auto_row.addWidget(self.automation_toggle)
        
        layout.addLayout(auto_row)
        layout.addWidget(self.automation_status_label)
        return content_widget
    
    def create_optimized_status_ui(self):
        """OPTIMIZED: Status UI with minimal blinking and smart updates"""
        content_widget = QWidget()
        layout = QVBoxLayout(content_widget)
        layout.setContentsMargins(0, 0, 0, 0)
        layout.setSpacing(8)
        
        # Connection Status Row
        connection_row = QHBoxLayout()
        self.connection_dot = QLabel("●")
        self.connection_dot.setStyleSheet("color: #ef4444; font-size: 18px;")
        self.connection_label = QLabel("Connection: Connecting...")
        self.connection_label.setStyleSheet("color: #94a3b8; font-size: 12px;")
        
        connection_row.addWidget(self.connection_dot)
        connection_row.addWidget(self.connection_label)
        connection_row.addStretch()
        
        # Status Row
        status_row = QHBoxLayout()
        self.status_dot = QLabel("●")
        self.status_dot.setStyleSheet("color: #8a2be2; font-size: 18px;")
        self.status_label = QLabel("Status: Ready")
        self.status_label.setStyleSheet("color: #94a3b8; font-size: 12px;")
        
        status_row.addWidget(self.status_dot)
        status_row.addWidget(self.status_label)
        status_row.addStretch()
        
        # Logic Row
        logic_row = QHBoxLayout()
        self.logic_dot = QLabel("●")
        self.logic_dot.setStyleSheet("color: #ff1493; font-size: 18px;")
        self.logic_label = QLabel("Logic: Waiting...")
        self.logic_label.setStyleSheet("color: #94a3b8; font-size: 12px;")
        
        logic_row.addWidget(self.logic_dot)
        logic_row.addWidget(self.logic_label)
        logic_row.addStretch()

        # OPTIMIZATION: Slower blinking timers
        self.connection_blink_timer = QTimer(self)
        self.connection_blink_timer.setInterval(BLINK_INTERVAL_SLOW)
        self.connection_blink_timer.timeout.connect(self.toggle_connection_dot)
        self.connection_dot_visible = True
        
        self.status_blink_timer = QTimer(self)
        self.status_blink_timer.setInterval(BLINK_INTERVAL_MEDIUM)
        self.status_blink_timer.timeout.connect(self.toggle_status_dot)
        self.status_dot_visible = True
        
        self.logic_blink_timer = QTimer(self)
        self.logic_blink_timer.setInterval(BLINK_INTERVAL_FAST)
        self.logic_blink_timer.timeout.connect(self.toggle_logic_dot)
        self.logic_dot_visible = True
        
        self.settings_timer = QTimer(self)
        self.settings_timer.setSingleShot(True)
        self.settings_timer.timeout.connect(self.hide_settings_message)
        
        self.set_connection_connecting()
        self.logic_blink_timer.start()
        
        layout.addLayout(connection_row)
        layout.addLayout(status_row)
        layout.addLayout(logic_row)
        
        return content_widget
        
    def toggle_connection_dot(self):
        """OPTIMIZED: Less frequent blinking"""
        if self.connection_dot_visible:
            self.connection_dot.setStyleSheet("color: #0f172a; font-size: 18px;")
            self.connection_dot_visible = False
        else:
            current_color = self.connection_dot.property("current_color") or "red"
            color_code = "#22c55e" if current_color == "green" else "#ef4444"
            self.connection_dot.setStyleSheet(f"color: {color_code}; font-size: 18px;")
            self.connection_dot_visible = True
    
    def toggle_status_dot(self):
        if self.status_dot_visible:
            self.status_dot.setStyleSheet("color: #0f172a; font-size: 18px;")
            self.status_dot_visible = False
        else:
            self.status_dot.setStyleSheet("color: #8a2be2; font-size: 18px;")
            self.status_dot_visible = True
    
    def toggle_logic_dot(self):
        if self.logic_dot_visible:
            self.logic_dot.setStyleSheet("color: #0f172a; font-size: 18px;")
            self.logic_dot_visible = False
        else:
            self.logic_dot.setStyleSheet("color: #ff1493; font-size: 18px;")
            self.logic_dot_visible = True
    
    def set_connection_connecting(self):
        self.connection_dot.setProperty("current_color", "red")
        self.connection_label.setText("Connection: Connecting...")
        if self.connection_blink_timer.thread() == self.thread():
            self.connection_blink_timer.start()
    
    def set_connection_connected(self):
        self.connection_blink_timer.stop()
        self.connection_dot.setProperty("current_color", "green")
        self.connection_dot.setStyleSheet("color: #22c55e; font-size: 18px;")
        self.connection_label.setText("Connection: ESPHome device connected")
        self.connection_dot_visible = True
    
    def set_connection_disconnected(self):
        self.connection_blink_timer.stop()
        self.connection_dot.setProperty("current_color", "red")
        self.connection_dot.setStyleSheet("color: #ef4444; font-size: 18px;")
        self.connection_label.setText("Connection: Disconnected")
        self.connection_dot_visible = True
    
    def update_status(self, message):
        self.status_label.setText(f"Status: {message}")
        self.status_dot.setStyleSheet("color: #8a2be2; font-size: 18px;")
    
    def update_status_with_settings(self, message):
        base_status = "Open Hardware Monitor working properly"
        self.status_label.setText(f"Status: {base_status}; {message}")
        self.settings_timer.start(5000)
    
    def hide_settings_message(self):
        self.check_ohm_status()
    
    def update_logic(self, temp, threshold, speed):
       
        if self.controller.automation_enabled:
              self.logic_label.setText(f"Logic: {temp}°C ≥ {threshold}°C → Fan: {speed}%")
        else:
            self.logic_label.setText(f"Logic: Waiting for automation")
    
    def update_ohm_status(self, is_working):
        """Update OHM status from temperature fetching results"""
        current_time = time.time()
        self._ohm_status_cache = is_working
        self._ohm_status_cache_time = current_time
        
        if is_working:
            self.update_status("Open Hardware Monitor working properly")
        else:
            self.update_status("Please run Open Hardware Monitor to fetch temperatures from your PC")
    
    def check_ohm_status(self):
        """Initial check for Open Hardware Monitor accessibility"""
        current_time = time.time()
        if (self._ohm_status_cache is None or 
            current_time - self._ohm_status_cache_time > OHM_CHECK_INTERVAL):
            
            try:
                import urllib.request
                try:
                    urllib.request.urlopen("http://localhost:8085/data.json", timeout=1)
                    self.update_ohm_status(True)
                except:
                    self.update_ohm_status(False)
                self._ohm_status_cache_time = current_time
            except:
                self.update_ohm_status(False)
                self._ohm_status_cache_time = current_time
        else:
            # Use cached status
            if self._ohm_status_cache:
                self.update_status("Open Hardware Monitor working properly")
            else:
                self.update_status("Please run Open Hardware Monitor to fetch temperatures from your PC")

    def set_dark_theme(self):
        palette = QPalette()
        palette.setColor(QPalette.ColorRole.Window, QColor("#0f172a"))
        palette.setColor(QPalette.ColorRole.WindowText, QColor(255, 255, 255))
        palette.setColor(QPalette.ColorRole.Base, QColor("#1e293b"))
        palette.setColor(QPalette.ColorRole.AlternateBase, QColor("#334155"))
        palette.setColor(QPalette.ColorRole.ToolTipBase, QColor(255, 255, 255))
        palette.setColor(QPalette.ColorRole.ToolTipText, QColor(255, 255, 255))
        palette.setColor(QPalette.ColorRole.Text, QColor(255, 255, 255))
        palette.setColor(QPalette.ColorRole.Button, QColor("#334155"))
        palette.setColor(QPalette.ColorRole.ButtonText, QColor(255, 255, 255))
        palette.setColor(QPalette.ColorRole.BrightText, QColor(255, 0, 0))
        palette.setColor(QPalette.ColorRole.Link, QColor("#8a2be2"))
        palette.setColor(QPalette.ColorRole.Highlight, QColor("#8a2be2"))
        palette.setColor(QPalette.ColorRole.HighlightedText, QColor(0, 0, 0))
        self.setPalette(palette)
        
    def setup_tray_icon(self):
        self.tray_icon = QSystemTrayIcon(self)
        self.tray_icon.setToolTip("ESP Home Fan Controller")

        # Try to use the ICO file first, then PNG as fallback
        icon_set = False
        try:
            ico_path = get_resource_path(ICO_ICON_PATH)
            if os.path.exists(ico_path):
                self.tray_icon.setIcon(QIcon(ico_path))
                icon_set = True
                print(f"System tray icon loaded from: {ico_path}")
        except Exception as e:
            print(f"Error loading ICO icon: {e}")
        
        if not icon_set:
            try:
                png_path = get_resource_path(ICON_PATH)
                if os.path.exists(png_path):
                    self.tray_icon.setIcon(QIcon(png_path))
                    icon_set = True
                    print(f"System tray icon loaded from: {png_path}")
            except Exception as e:
                print(f"Error loading PNG icon: {e}")
        
        if not icon_set:
            print("WARNING: No icon files found. Using default system icon.")
            self.tray_icon.setIcon(QApplication.style().standardIcon(QStyle.StandardPixmap.SP_ComputerIcon))

        tray_menu = QMenu()
        
        # Add Show/Hide action at the top
        show_hide_action = QAction("Show/Hide Window", self)
        show_hide_action.triggered.connect(self.toggle_window_visibility)
        tray_menu.addAction(show_hide_action)
        
        tray_menu.addSeparator()
        
        self.automation_action = QAction("Enable Automation", self)
        self.automation_action.setCheckable(True)
        self.automation_action.setChecked(self.controller.automation_enabled)
        self.automation_action.triggered.connect(self.toggle_automation)
        tray_menu.addAction(self.automation_action)

        self.full_speed_action = QAction("Full Speed", self)
        self.full_speed_action.setCheckable(True)
        self.full_speed_action.setChecked(self.controller.full_speed_mode)
        self.full_speed_action.triggered.connect(self.toggle_full_speed)
        tray_menu.addAction(self.full_speed_action)

        tray_menu.addSeparator()

        exit_action = QAction("Exit", self)
        exit_action.triggered.connect(self.exit_app)
        tray_menu.addAction(exit_action)

        self.tray_icon.setContextMenu(tray_menu)
        self.tray_icon.activated.connect(self.handle_tray_click)
        self.tray_icon.show()
        
        # Show startup notification
        self.tray_icon.showMessage(
            "ESP Home Fan Controller",
            "Fan controller is now running in the background. Click the icon to open.",
            QSystemTrayIcon.MessageIcon.Information,
            3000  # Show for 3 seconds
        )

    def handle_tray_click(self, reason):
        if reason == QSystemTrayIcon.ActivationReason.Trigger:
            self.toggle_window_visibility()
    
    def toggle_window_visibility(self):
        """Toggle between showing and hiding the main window"""
        if self.isVisible():
            self.hide()
        else:
            self.show()
            self.raise_()
            self.activateWindow()

    def toggle_automation(self):
        self.controller.automation_enabled = self.automation_action.isChecked()
        if self.controller.automation_enabled:
            self.controller.manual_speed_mode = False
            self.controller.full_speed_mode = False
            self.full_speed_action.setChecked(False)
        self.update_gui_state()
        self.controller.save_settings()

    def toggle_full_speed(self):
        self.controller.full_speed_mode = self.full_speed_action.isChecked()
        if self.controller.full_speed_mode:
            self.controller.automation_enabled = False
            self.controller.manual_speed_mode = False
            self.automation_action.setChecked(False)
            self.speed_slider.blockSignals(True)
            self.speed_slider.setValue(100)
            self.speed_slider.blockSignals(False)
            if self.controller.worker and self.controller.esphome:
                self.controller.worker.schedule_task(self.controller.esphome.set_fan_speed(100))
        self.update_gui_state()

    def update_gui_state(self):
        if hasattr(self, 'automation_toggle'):
            self.automation_toggle.setChecked(self.controller.automation_enabled)
        if hasattr(self, 'automation_action'):
            self.automation_action.setChecked(self.controller.automation_enabled)
        if hasattr(self, 'full_speed_action'):
            self.full_speed_action.setChecked(self.controller.full_speed_mode)
        
        if hasattr(self, 'automation_status_label'):
            if self.controller.automation_enabled:
                self.automation_status_label.setText("Automation Enabled")
                self.automation_status_label.setStyleSheet("color: #22c55e; font-size: 12px;")
            else:
                self.automation_status_label.setText("Automation Disabled")
                self.automation_status_label.setStyleSheet("color: #94a3b8; font-size: 12px;")
                
        if hasattr(self, 'mode_label'):
            if self.controller.automation_enabled:
                self.mode_label.setText("Automatic Control")
                self.mode_label.setStyleSheet("color: #22c55e; font-size: 12px;")
            else:
                self.mode_label.setText("Manual Control")
                self.mode_label.setStyleSheet("color: #ff1493; font-size: 12px;")
                
        if hasattr(self, 'logic_label'):
            if self.controller.automation_enabled:
                # Logic will be updated by update_logic method when temperature changes
                pass  
            else:
                self.logic_label.setText("Logic: Waiting for automation")

    def save_settings(self):
        new_thresholds = []
        for temp_input, speed_input in self.threshold_inputs:
            try:
                temp = int(temp_input.text())
                speed = int(speed_input.text())
                if not (0 <= speed <= 100):
                    raise ValueError("Speed must be between 0 and 100.")
                new_thresholds.append((temp, speed))
            except ValueError as e:
                QMessageBox.warning(self, "Invalid Input", f"Please enter valid numbers for temperatures and speeds. {e}")
                return
        self.controller.update_thresholds_from_gui(new_thresholds)
        self.update_status_message("Settings saved!")

    def update_status_message(self, message):
        if "Connected" in message:
            self.set_connection_connected()
            # Update ESP connection status
            if hasattr(self, 'connection_status_label'):
                self.connection_status_label.setText(f"Status: Connected to {ESPHOME_HOST}")
                self.connection_status_label.setStyleSheet("color: #22c55e; font-size: 12px; font-weight: 500;")
        elif "Connection error" in message or "Error" in message or "Disconnected" in message:
            self.set_connection_disconnected()
            # Update ESP connection status
            if hasattr(self, 'connection_status_label'):
                self.connection_status_label.setText("Status: Connection failed")
                self.connection_status_label.setStyleSheet("color: #ef4444; font-size: 12px; font-weight: 500;")
        elif "Finding" in message or "Connecting" in message:
            self.set_connection_connecting()
            # Update ESP connection status
            if hasattr(self, 'connection_status_label'):
                self.connection_status_label.setText(f"Status: Connecting to {ESPHOME_HOST}...")
                self.connection_status_label.setStyleSheet("color: #fbbf24; font-size: 12px; font-weight: 500;")
        
        if "saved" in message or "Saved" in message:
            self.update_status_with_settings(message)
        elif not any(word in message for word in ["Connected", "Connection", "Finding", "Connecting", "Auto:"]):
            self.update_status(message)

    def exit_app(self):
        self.tray_icon.hide()
        self.controller.shutdown_app()
        QCoreApplication.quit()

    def setup_timer(self):
        """Setup main loop timer"""
        self.timer = QTimer(self)
        self.timer.setInterval(MAIN_LOOP_INTERVAL)  # 3 seconds as requested
        self.timer.timeout.connect(self.run_main_loop_task)
        self.timer.start()

    def run_main_loop_task(self):
        if self.controller.worker and self.controller.esphome:
            self.controller.worker.schedule_task(self.controller.main_loop())

    def toggle_automation_from_gui(self):
        is_checked = self.automation_toggle.isChecked()
        self.controller.automation_enabled = is_checked
        self.controller.manual_speed_mode = not is_checked
        self.controller.full_speed_mode = False
        self.update_gui_state()
        self.controller.save_settings()

    def closeEvent(self, event):
        # Save both settings and window geometry
        self.controller.save_settings()
        self.save_window_geometry()
        event.ignore()
        self.hide()

    def moveEvent(self, event):
        """Save window geometry when moved"""
        if hasattr(self, '_move_timer'):
            self._move_timer.stop()
        self._move_timer = QTimer()
        self._move_timer.setSingleShot(True)
        self._move_timer.timeout.connect(self.save_window_geometry)
        self._move_timer.start(1000)  # Save geometry after 1 second
        super().moveEvent(event)

    def resizeEvent(self, event):
        """Save window geometry when resized"""
        if hasattr(self, '_resize_timer'):
            self._resize_timer.stop()
        self._resize_timer = QTimer()
        self._resize_timer.setSingleShot(True)
        self._resize_timer.timeout.connect(self.save_window_geometry)
        self._resize_timer.start(1000)  # Save geometry after 1 second
        super().resizeEvent(event)

    def on_slider_pressed(self):
        self.controller.automation_enabled = False
        self.controller.manual_speed_mode = True
        self.controller.full_speed_mode = False
        self.update_gui_state()
        
    def on_speed_slider_changed(self, value):
        if self.controller.automation_enabled:
            self.controller.automation_enabled = False
            self.controller.manual_speed_mode = True
            self.controller.full_speed_mode = False
            self.update_gui_state()
        
        if self.controller.manual_speed_mode:
            self.speed_label.setText(f"Current Speed: {value}%")
            self.controller.manual_speed_value = value
            
            if self.controller.worker and self.controller.esphome:
                self.controller.worker.schedule_task(self.controller.esphome.set_fan_speed(value))
        
    def update_current_speed(self, value):
        """Updates the current speed display"""
        if hasattr(self, 'speed_label'):
            self.speed_label.setText(f"Current Speed: {value}%")
        if hasattr(self, 'speed_slider'):
            self.speed_slider.blockSignals(True)
            self.speed_slider.setValue(value)
            self.speed_slider.blockSignals(False)
    
    def update_cpu_temperature(self, temp):
        """Update the CPU temperature display"""
        if hasattr(self, 'cpu_temp_label'):
            self.cpu_temp_label.setText(f"{temp:.1f}°C")
    
    def connect_to_esp_device(self):
        """Handle ESP device connection button click"""
        ip_address = self.ip_input.text().strip()
        
        # Validate IP address format
        if not self.validate_ip_address(ip_address):
            QMessageBox.warning(self, "Invalid IP Address", 
                              "Please enter a valid IP address (e.g., 192.168.X.XX)")
            return
        
        # Update the global host and reconnect
        global ESPHOME_HOST
        ESPHOME_HOST = ip_address
        
        # Update connection status
        self.connection_status_label.setText(f"Status: Connecting to {ip_address}...")
        self.connection_status_label.setStyleSheet("color: #fbbf24; font-size: 12px; font-weight: 500;")
        
        # Disable button during connection
        self.connect_btn.setEnabled(False)
        self.connect_btn.setText("Connecting...")
        
        # Disconnect current client and reconnect with new IP
        if self.controller.esphome:
            self.controller.worker.schedule_task(self.controller.esphome.disconnect())
            self.controller.esphome = None
        
        # Trigger reconnection with new IP
        if self.controller.worker:
            self.controller.worker.schedule_task(self.controller.worker.async_connect(self.controller))
        
        # Re-enable button after a delay
        QTimer.singleShot(3000, self.reset_connect_button)
        
        # Save the new IP to settings
        self.save_esp_ip_to_settings(ip_address)
    
    def validate_ip_address(self, ip):
        """Validate IP address format"""
        try:
            parts = ip.split('.')
            if len(parts) != 4:
                return False
            for part in parts:
                if not (0 <= int(part) <= 255):
                    return False
            return True
        except ValueError:
            return False
    
    def reset_connect_button(self):
        """Reset connect button to normal state"""
        self.connect_btn.setEnabled(True)
        self.connect_btn.setText("Connect to ESP")
    
    def save_esp_ip_to_settings(self, ip_address):
        """Save ESP IP address to settings file"""
        try:
            settings = {}
            if os.path.exists(SETTINGS_FILE):
                with open(SETTINGS_FILE, 'r') as f:
                    settings = json.load(f)
            
            settings["esp_host"] = ip_address
            
            with open(SETTINGS_FILE, 'w') as f:
                json.dump(settings, f, indent=4)
        except Exception as e:
            print(f"Error saving ESP IP to settings: {e}")
    
    def load_esp_ip_from_settings(self):
        """Load ESP IP address from settings file"""
        try:
            if os.path.exists(SETTINGS_FILE):
                with open(SETTINGS_FILE, 'r') as f:
                    settings = json.load(f)
                    saved_ip = settings.get("esp_host", ESPHOME_HOST)
                    if hasattr(self, 'ip_input'):
                        self.ip_input.setText(saved_ip)
                    return saved_ip
        except Exception as e:
            print(f"Error loading ESP IP from settings: {e}")
        return ESPHOME_HOST


if __name__ == "__main__":
    # Enable High DPI scaling
    try:
        QApplication.setAttribute(Qt.ApplicationAttribute.AA_EnableHighDpiScaling)
        QApplication.setAttribute(Qt.ApplicationAttribute.AA_UseHighDpiPixmaps)
    except Exception:
        pass

    app = QApplication(sys.argv)
    app.setStyle('Fusion')
    
    # Set application icon globally
    try:
        app_icon_path = get_resource_path(ICO_ICON_PATH)
        if os.path.exists(app_icon_path):
            app.setWindowIcon(QIcon(app_icon_path))
            print(f"Application icon set: {app_icon_path}")
        else:
            app_icon_path = get_resource_path(ICON_PATH)
            if os.path.exists(app_icon_path):
                app.setWindowIcon(QIcon(app_icon_path))
                print(f"Application icon set: {app_icon_path}")
    except Exception as e:
        print(f"Error setting application icon: {e}")
    
    controller = OptimizedFanController()
    worker = OptimizedAsyncioWorker()
    controller.set_worker(worker)
    
    gui = OptimizedFanGUI(controller)
    
    sys.exit(app.exec())