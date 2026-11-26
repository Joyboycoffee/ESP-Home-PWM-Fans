using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ESPHomeFanController.Models;

namespace ESPHomeFanController.Services
{
    public class FanSpeedController : IDisposable
    {
        private readonly EspHomeService _espService;
        private readonly HardwareMonitorService _hwService;
        private readonly AppSettings _settings;

        private CancellationTokenSource? _loopCts;
        private Task? _loopTask;
        private int _lastDispatchedSpeed = -1;
        private int _currentTargetSpeed = 50;
        private DateTime _lastDispatchTime = DateTime.MinValue;
        private bool _userOverrideExplicitlySet;
        private readonly SemaphoreSlim _evalLock = new(1, 1);

        public event Action<string>? LogicStatusUpdated;
        public event Action<int>? TargetSpeedUpdated;

        public bool AutomationEnabled
        {
            get => _settings.AutomationEnabled;
            set
            {
                _settings.AutomationEnabled = value;
                _settings.Save();
                _lastDispatchedSpeed = -1;
                EvaluateSpeedImmediately();
            }
        }

        public bool FullSpeedOverride
        {
            get => _settings.FullSpeedOverride;
            set
            {
                _userOverrideExplicitlySet = value;
                _settings.FullSpeedOverride = value;
                _settings.Save();
                _ = _espService.SetFullSpeedOverrideAsync(value);
                _lastDispatchedSpeed = -1;
                EvaluateSpeedImmediately();
            }
        }

        public int ManualSpeed
        {
            get => _settings.ManualSpeed;
            set
            {
                _settings.ManualSpeed = Math.Clamp(value, 0, 100);
                _settings.Save();
                if (!_settings.AutomationEnabled && !_settings.FullSpeedOverride)
                {
                    _lastDispatchedSpeed = -1;
                    EvaluateSpeedImmediately();
                }
            }
        }

        public List<CurvePoint> Thresholds => _settings.Thresholds;

        public FanSpeedController(EspHomeService espService, HardwareMonitorService hwService, AppSettings settings)
        {
            _espService = espService;
            _hwService = hwService;
            _settings = settings;

            _espService.ConnectionRestored += OnConnectionRestored;
            _espService.FanStateChanged += OnDeviceFanStateChanged;
            _espService.FullSpeedOverrideDetectedOnDevice += OnFullSpeedOverrideDetected;
        }

        private void OnConnectionRestored()
        {
            _lastDispatchedSpeed = -1;
            _lastDispatchTime = DateTime.MinValue;

            // If user did not manually turn on Full Speed Override, ensure it's cleared on reconnect
            if (!_userOverrideExplicitlySet && !_settings.FullSpeedOverride)
            {
                _settings.FullSpeedOverride = false;
                _ = _espService.SetFullSpeedOverrideAsync(false);
            }

            EvaluateSpeedImmediately();
        }

        private void OnDeviceFanStateChanged(bool isOn, int actualSpeed)
        {
            // Auto-heal: If user has NOT enabled Full Speed Override, but the ESP reports running at 100%
            // while target speed is less than 100%, the ESP tripped safety override (e.g. WiFi hiccup or reboot)
            if (!_settings.FullSpeedOverride && isOn && actualSpeed == 100 && _currentTargetSpeed < 100)
            {
                Debug.WriteLine($"[FanSpeedController] Device full-speed mismatch detected (actual: {actualSpeed}%, target: {_currentTargetSpeed}%). Auto-healing!");
                _ = AutoHealAsync();
            }
        }

        private void OnFullSpeedOverrideDetected(bool deviceOverrideActive)
        {
            // Auto-heal: If hardware switch on ESP is ON while user settings have it OFF, disarm it immediately
            if (deviceOverrideActive && !_settings.FullSpeedOverride)
            {
                Debug.WriteLine("[FanSpeedController] Hardware full_speed_override detected ON on ESP. Disarming!");
                _ = AutoHealAsync();
            }
        }

        private async Task AutoHealAsync()
        {
            if (!_espService.IsConnected) return;

            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _espService.SetFullSpeedOverrideAsync(false, cts.Token);
                bool success = await _espService.SetFanSpeedAsync(_currentTargetSpeed, cts.Token);
                if (success)
                {
                    _lastDispatchedSpeed = _currentTargetSpeed;
                    _lastDispatchTime = DateTime.UtcNow;
                    Debug.WriteLine($"[FanSpeedController] Auto-healed: override disarmed, speed set to {_currentTargetSpeed}%");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FanSpeedController] AutoHeal error: {ex.Message}");
            }
        }

        public void Start()
        {
            Stop();
            _loopCts = new CancellationTokenSource();
            _loopTask = Task.Run(() => ControlLoopAsync(_loopCts.Token));
        }

        public void Stop()
        {
            _loopCts?.Cancel();
            try
            {
                _loopTask?.Wait(TimeSpan.FromSeconds(1));
            }
            catch { }
            _loopCts?.Dispose();
            _loopCts = null;
            _loopTask = null;
        }

        public void UpdateThresholds(List<CurvePoint> newPoints)
        {
            _settings.Thresholds = newPoints.OrderBy(p => p.Temperature).ToList();
            _settings.Save();
            _lastDispatchedSpeed = -1;
            EvaluateSpeedImmediately();
        }

        private async Task ControlLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await EvaluateAndApplySpeedAsync(token);
                    await Task.Delay(2500, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Controller] Error: {ex.Message}");
                    await Task.Delay(2500, token);
                }
            }
        }

        public void EvaluateSpeedImmediately()
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await EvaluateAndApplySpeedAsync(CancellationToken.None);
                }
                catch { }
            });
        }

        private async Task EvaluateAndApplySpeedAsync(CancellationToken token)
        {
            if (!await _evalLock.WaitAsync(500, token)) return;

            try
            {
                float? cpuTemp = await _hwService.FetchCpuTemperatureAsync(token);
                int targetSpeed;
                string logicText;

                if (_settings.FullSpeedOverride)
                {
                    targetSpeed = 100;
                    logicText = "Full Speed Override Active: 100% Speed";
                }
                else if (!_settings.AutomationEnabled)
                {
                    targetSpeed = _settings.ManualSpeed;
                    logicText = $"Manual Mode: {targetSpeed}% Speed";
                }
                else
                {
                    if (cpuTemp.HasValue)
                    {
                        targetSpeed = CalculateStepSpeed(cpuTemp.Value);
                        logicText = targetSpeed == 0
                            ? $"Auto Mode: {cpuTemp.Value:F1}°C → Off (0%)"
                            : $"Auto Mode: {cpuTemp.Value:F1}°C → {targetSpeed}% Speed";
                    }
                    else
                    {
                        targetSpeed = _lastDispatchedSpeed >= 0 ? _lastDispatchedSpeed : 50;
                        logicText = "Auto Mode: Waiting for CPU temperature sensor...";
                    }
                }

                _currentTargetSpeed = targetSpeed;
                LogicStatusUpdated?.Invoke(logicText);
                TargetSpeedUpdated?.Invoke(targetSpeed);

                bool speedChanged = (_lastDispatchedSpeed != targetSpeed);
                // Heartbeat: Re-assert speed every 8 seconds even if speed hasn't changed
                // This guarantees the ESP never gets stuck in full speed if it reboots or reconnects
                bool heartbeatDue = (DateTime.UtcNow - _lastDispatchTime) > TimeSpan.FromSeconds(8);

                if (_espService.IsConnected && (speedChanged || heartbeatDue || _settings.FullSpeedOverride))
                {
                    bool success = await _espService.SetFanSpeedAsync(targetSpeed, token);
                    if (success)
                    {
                        _lastDispatchedSpeed = targetSpeed;
                        _lastDispatchTime = DateTime.UtcNow;
                    }

                    // Always ensure hardware override is cleared if user didn't request full speed
                    if (!_settings.FullSpeedOverride)
                    {
                        await _espService.SetFullSpeedOverrideAsync(false, token);
                    }
                }
            }
            finally
            {
                _evalLock.Release();
            }
        }

        public int CalculateStepSpeed(float cpuTemp)
        {
            var sorted = _settings.Thresholds.OrderBy(p => p.Temperature).ToList();
            if (sorted.Count == 0) return 50;

            // Below first threshold (e.g. < 55°C): 0% speed (Fan OFF)
            if (cpuTemp < sorted[0].Temperature)
            {
                return 0;
            }

            if (cpuTemp >= sorted[^1].Temperature)
            {
                return sorted[^1].Speed;
            }

            for (int i = 0; i < sorted.Count - 1; i++)
            {
                if (cpuTemp >= sorted[i].Temperature && cpuTemp < sorted[i + 1].Temperature)
                {
                    return sorted[i].Speed;
                }
            }

            return sorted[^1].Speed;
        }

        public void Dispose()
        {
            _espService.ConnectionRestored -= OnConnectionRestored;
            _espService.FanStateChanged -= OnDeviceFanStateChanged;
            _espService.FullSpeedOverrideDetectedOnDevice -= OnFullSpeedOverrideDetected;
            Stop();
            _evalLock.Dispose();
        }
    }
}
