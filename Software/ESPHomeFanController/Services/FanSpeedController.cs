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
        private bool _userOverrideExplicitlySet;

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
        }

        private void OnConnectionRestored()
        {
            _lastDispatchedSpeed = -1;

            // If user did not manually turn on Full Speed Override, ensure it's cleared on reconnect
            if (!_userOverrideExplicitlySet)
            {
                _settings.FullSpeedOverride = false;
                _settings.Save();
            }

            EvaluateSpeedImmediately();
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
                    await Task.Delay(3000, token);
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
                    logicText = $"Auto Mode: {cpuTemp.Value:F1}°C -> {targetSpeed}% Speed";
                }
                else
                {
                    targetSpeed = _lastDispatchedSpeed >= 0 ? _lastDispatchedSpeed : 50;
                    logicText = "Auto Mode: Waiting for CPU temperature sensor...";
                }
            }

            LogicStatusUpdated?.Invoke(logicText);
            TargetSpeedUpdated?.Invoke(targetSpeed);

            if (_espService.IsConnected && (_lastDispatchedSpeed != targetSpeed || _settings.FullSpeedOverride))
            {
                bool success = await _espService.SetFanSpeedAsync(targetSpeed, token);
                if (success)
                {
                    _lastDispatchedSpeed = targetSpeed;
                }
            }
        }

        public int CalculateStepSpeed(float cpuTemp)
        {
            var sorted = _settings.Thresholds.OrderBy(p => p.Temperature).ToList();
            if (sorted.Count == 0) return 50;

            if (cpuTemp <= sorted[0].Temperature)
            {
                return sorted[0].Speed;
            }

            if (cpuTemp >= sorted[^1].Temperature)
            {
                return sorted[^1].Speed;
            }

            // Step/Staircase curve logic:
            // If temp is between threshold[i] and threshold[i+1], use the higher threshold speed
            for (int i = 0; i < sorted.Count - 1; i++)
            {
                if (cpuTemp > sorted[i].Temperature && cpuTemp <= sorted[i + 1].Temperature)
                {
                    return sorted[i + 1].Speed;
                }
            }

            return sorted[^1].Speed;
        }

        public void Dispose()
        {
            _espService.ConnectionRestored -= OnConnectionRestored;
            Stop();
        }
    }
}
