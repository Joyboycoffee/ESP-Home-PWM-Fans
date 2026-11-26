using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using LibreHardwareMonitor.Hardware;

namespace ESPHomeFanController.Services
{
    public class HardwareMonitorService : IDisposable
    {
        private Computer? _computer;
        private readonly CancellationTokenSource _cts = new();
        private Task? _pollingTask;
        private readonly object _pollLock = new();
        private DateTime _lastReopenAttempt = DateTime.MinValue;

        private float? _cachedCpuTemperature;
        private float? _cachedGpuTemperature;
        private float? _smoothedCpuTemperature;
        private float? _smoothedGpuTemperature;
        private const float SmoothingAlpha = 0.35f;
        private string _gpuName = "NVIDIA RTX 3070";
        private bool _isActive;

        public event Action<float>? CpuTemperatureUpdated;
        public event Action<float>? GpuTemperatureUpdated;
        public event Action<float>? TemperatureUpdated; // Backward compatibility for CPU subscribers
        public event Action<bool>? LhmStatusChanged;     // Keeps HW Monitor status badge working

        public bool IsLhmRunning => _isActive;
        public float? CurrentTemperature => _cachedCpuTemperature;
        public float? CurrentCpuTemperature => _cachedCpuTemperature;
        public float? CurrentGpuTemperature => _cachedGpuTemperature;
        public string GpuName => _gpuName;

        public HardwareMonitorService()
        {
            try
            {
                Directory.SetCurrentDirectory(AppContext.BaseDirectory);
            }
            catch { }

            EnsureRing0Opened();

            try
            {
                _computer = CreateComputer();
                _computer.Open();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HardwareMonitor] Failed to open Computer: {ex.Message}");
            }

            _pollingTask = Task.Run(() => PollingLoopAsync(_cts.Token));
        }

        private static Computer CreateComputer()
        {
            return new Computer
            {
                IsCpuEnabled = true,
                IsGpuEnabled = true,
                IsMotherboardEnabled = true, // Enables SuperIO (IT8689E) detection
                IsMemoryEnabled = false,
                IsNetworkEnabled = false,
                IsStorageEnabled = false,
                IsControllerEnabled = false
            };
        }

        private static void EnsureRing0Opened()
        {
            try
            {
                Directory.SetCurrentDirectory(AppContext.BaseDirectory);
                var ring0Type = typeof(Computer).Assembly.GetType("LibreHardwareMonitor.Hardware.Ring0");
                if (ring0Type != null)
                {
                    var isOpenP = ring0Type.GetProperty("IsOpen", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    bool isOpen = (bool)(isOpenP?.GetValue(null) ?? false);
                    if (!isOpen)
                    {
                        var openM = ring0Type.GetMethod("Open", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                        openM?.Invoke(null, null);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HardwareMonitor] EnsureRing0Opened error: {ex.Message}");
            }
        }

        private void TryReopenComputerIfStale()
        {
            if ((DateTime.Now - _lastReopenAttempt).TotalSeconds < 5) return;
            _lastReopenAttempt = DateTime.Now;

            Task.Run(() =>
            {
                try
                {
                    EnsureRing0Opened();
                    lock (_pollLock)
                    {
                        _computer?.Close();
                        _computer = CreateComputer();
                        _computer.Open();
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[HardwareMonitor] Reopen error: {ex.Message}");
                }
            });
        }

        private async Task PollingLoopAsync(CancellationToken token)
        {
            // Initial poll immediately
            PollSensors();

            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(1750, token);
                    PollSensors();
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[HardwareMonitor] Loop error: {ex.Message}");
                }
            }
        }

        public Task<float?> FetchCpuTemperatureAsync(CancellationToken cancellationToken = default)
        {
            if (_cachedCpuTemperature.HasValue)
            {
                return Task.FromResult(_cachedCpuTemperature);
            }

            PollSensors();
            return Task.FromResult(_cachedCpuTemperature);
        }

        private void PollSensors()
        {
            float? cpuTemp = null;
            float? gpuTemp = null;

            lock (_pollLock)
            {
                if (_computer != null)
                {
                    try
                    {
                        // 1. Primary: AMD Ryzen 5 7600X CPU SMU / MSR sensors
                        var cpuHardware = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);
                        if (cpuHardware != null)
                        {
                            cpuHardware.Update();
                            foreach (var sub in cpuHardware.SubHardware)
                            {
                                sub.Update();
                            }
                            var temp = ExtractCpuTemperature(cpuHardware);
                            if (temp.HasValue && temp.Value >= 5.0f && temp.Value <= 115.0f)
                            {
                                cpuTemp = temp;
                            }
                        }

                        // 2. Fallback 1: AMD Zen 4 integrated GPU (AMD Radeon(TM) Graphics) on the exact same CPU package!
                        // Reports 'GPU VR SoC' or 'GPU Core' (typically 42°C-50°C) without needing kernel driver!
                        if (!cpuTemp.HasValue)
                        {
                            var amdIgpu = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.GpuAmd && h.Name.Contains("Radeon", StringComparison.OrdinalIgnoreCase));
                            if (amdIgpu != null)
                            {
                                amdIgpu.Update();
                                foreach (var sub in amdIgpu.SubHardware) sub.Update();
                                var temp = ExtractAmdIgpuSocTemp(amdIgpu);
                                if (temp.HasValue && temp.Value >= 5.0f && temp.Value <= 115.0f)
                                {
                                    cpuTemp = temp;
                                }
                            }
                        }

                        // 3. Fallback 2: Motherboard SuperIO (e.g. ITE IT8689E) CPU sensor
                        if (!cpuTemp.HasValue)
                        {
                            var mobo = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Motherboard);
                            if (mobo != null)
                            {
                                mobo.Update();
                                foreach (var sub in mobo.SubHardware) sub.Update();
                                var temp = ExtractMotherboardCpuTemp(mobo);
                                if (temp.HasValue && temp.Value >= 5.0f && temp.Value <= 115.0f)
                                {
                                    cpuTemp = temp;
                                }
                            }
                        }

                        // 4. GPU Polling: Strictly prioritize dedicated NVIDIA GeForce RTX 3070 over integrated AMD Radeon graphics
                        var gpuHardware = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.GpuNvidia)
                                       ?? _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.GpuAmd && !h.Name.Contains("Radeon", StringComparison.OrdinalIgnoreCase))
                                       ?? _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.GpuIntel);
                        if (gpuHardware != null)
                        {
                            gpuHardware.Update();
                            foreach (var sub in gpuHardware.SubHardware)
                            {
                                sub.Update();
                            }
                            if (!string.IsNullOrEmpty(gpuHardware.Name))
                            {
                                _gpuName = CleanGpuName(gpuHardware.Name);
                            }
                            var temp = ExtractGpuTemperature(gpuHardware);
                            if (temp.HasValue && temp.Value >= 10.0f && temp.Value <= 120.0f)
                            {
                                gpuTemp = temp;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[HardwareMonitor] Native sensor poll error: {ex.Message}");
                    }
                }
            }

            // If CPU temperature is still unavailable, attempt a clean background reopen instead of latching onto fake values
            if (!cpuTemp.HasValue)
            {
                TryReopenComputerIfStale();
            }

            bool active = false;

            if (cpuTemp.HasValue && cpuTemp.Value >= 5.0f)
            {
                if (!_smoothedCpuTemperature.HasValue)
                {
                    _smoothedCpuTemperature = cpuTemp.Value;
                }
                else
                {
                    // Fast surge response if temperature increases by > 3°C
                    float delta = cpuTemp.Value - _smoothedCpuTemperature.Value;
                    float alpha = delta > 3.0f ? 0.85f : SmoothingAlpha;
                    _smoothedCpuTemperature = (_smoothedCpuTemperature.Value * (1.0f - alpha)) + (cpuTemp.Value * alpha);
                }

                float finalCpu = (float)Math.Round(_smoothedCpuTemperature.Value, 1);
                _cachedCpuTemperature = finalCpu;
                CpuTemperatureUpdated?.Invoke(finalCpu);
                TemperatureUpdated?.Invoke(finalCpu);
                active = true;
            }

            if (gpuTemp.HasValue && gpuTemp.Value >= 5.0f)
            {
                if (!_smoothedGpuTemperature.HasValue)
                {
                    _smoothedGpuTemperature = gpuTemp.Value;
                }
                else
                {
                    float delta = gpuTemp.Value - _smoothedGpuTemperature.Value;
                    float alpha = delta > 3.0f ? 0.85f : SmoothingAlpha;
                    _smoothedGpuTemperature = (_smoothedGpuTemperature.Value * (1.0f - alpha)) + (gpuTemp.Value * alpha);
                }

                float finalGpu = (float)Math.Round(_smoothedGpuTemperature.Value, 1);
                _cachedGpuTemperature = finalGpu;
                GpuTemperatureUpdated?.Invoke(finalGpu);
                active = true;
            }

            SetStatus(active);
        }

        private static float? ExtractCpuTemperature(IHardware cpuHardware)
        {
            var candidates = new List<ISensor>();

            void CollectSensors(IHardware hw)
            {
                foreach (var s in hw.Sensors)
                {
                    if (s.SensorType == SensorType.Temperature && s.Value.HasValue && s.Value.Value >= 5.0f && s.Value.Value <= 115.0f)
                    {
                        candidates.Add(s);
                    }
                }
                foreach (var sub in hw.SubHardware)
                {
                    CollectSensors(sub);
                }
            }

            CollectSensors(cpuHardware);

            if (candidates.Count == 0) return null;

            // Target AMD Ryzen 5 7600X: prioritize Core (Tctl/Tdie), CCD1 (Tdie), CPU Package, Core Average
            var sensor = candidates.FirstOrDefault(s => (s.Name.Contains("Core (Tctl/Tdie)", StringComparison.OrdinalIgnoreCase) ||
                                                        s.Name.Contains("Tctl/Tdie", StringComparison.OrdinalIgnoreCase) ||
                                                        s.Name.Contains("Tctl", StringComparison.OrdinalIgnoreCase) ||
                                                        s.Name.Contains("Tdie", StringComparison.OrdinalIgnoreCase)) && s.Value.HasValue && s.Value.Value >= 5.0f);
            if (sensor != null) return (float)Math.Round(sensor.Value!.Value, 1);

            sensor = candidates.FirstOrDefault(s => s.Name.Contains("CCD", StringComparison.OrdinalIgnoreCase) && s.Value.HasValue && s.Value.Value >= 5.0f);
            if (sensor != null) return (float)Math.Round(sensor.Value!.Value, 1);

            sensor = candidates.FirstOrDefault(s => (s.Name.Contains("CPU Package", StringComparison.OrdinalIgnoreCase) ||
                                                    s.Name.Contains("Package", StringComparison.OrdinalIgnoreCase)) && s.Value.HasValue && s.Value.Value >= 5.0f);
            if (sensor != null) return (float)Math.Round(sensor.Value!.Value, 1);

            sensor = candidates.FirstOrDefault(s => (s.Name.Contains("Core Average", StringComparison.OrdinalIgnoreCase) ||
                                                    s.Name.Contains("Average", StringComparison.OrdinalIgnoreCase)) && s.Value.HasValue && s.Value.Value >= 5.0f);
            if (sensor != null) return (float)Math.Round(sensor.Value!.Value, 1);

            sensor = candidates.FirstOrDefault(s => s.Name.Contains("Core", StringComparison.OrdinalIgnoreCase) && s.Value.HasValue && s.Value.Value >= 5.0f);
            if (sensor != null) return (float)Math.Round(sensor.Value!.Value, 1);

            return (float)Math.Round(candidates[0].Value!.Value, 1);
        }

        private static float? ExtractAmdIgpuSocTemp(IHardware igpu)
        {
            var candidates = new List<ISensor>();
            void Collect(IHardware h)
            {
                foreach (var s in h.Sensors)
                {
                    if (s.SensorType == SensorType.Temperature && s.Value.HasValue && s.Value.Value >= 5.0f && s.Value.Value <= 115.0f)
                    {
                        candidates.Add(s);
                    }
                }
                foreach (var sub in h.SubHardware) Collect(sub);
            }
            Collect(igpu);
            if (candidates.Count == 0) return null;

            var sensor = candidates.FirstOrDefault(s => (s.Name.Contains("VR SoC", StringComparison.OrdinalIgnoreCase) ||
                                                        s.Name.Contains("SoC", StringComparison.OrdinalIgnoreCase) ||
                                                        s.Name.Contains("Core", StringComparison.OrdinalIgnoreCase)) && s.Value.HasValue && s.Value.Value >= 5.0f);
            if (sensor != null && sensor.Value.HasValue) return (float)Math.Round(sensor.Value.Value, 1);

            return (float)Math.Round(candidates[0].Value!.Value, 1);
        }

        private static float? ExtractMotherboardCpuTemp(IHardware mobo)
        {
            var candidates = new List<ISensor>();
            void Collect(IHardware h)
            {
                foreach (var s in h.Sensors)
                {
                    if (s.SensorType == SensorType.Temperature && s.Value.HasValue && s.Value.Value >= 5.0f && s.Value.Value <= 115.0f)
                    {
                        candidates.Add(s);
                    }
                }
                foreach (var sub in h.SubHardware) Collect(sub);
            }
            Collect(mobo);
            if (candidates.Count == 0) return null;

            // Check for CPU sensor on SuperIO (IT8689E)
            var sensor = candidates.FirstOrDefault(s => s.Name.Equals("CPU", StringComparison.OrdinalIgnoreCase) ||
                                                        s.Name.Contains("CPU", StringComparison.OrdinalIgnoreCase));
            if (sensor != null && sensor.Value.HasValue) return (float)Math.Round(sensor.Value.Value, 1);

            // On IT8689E, Temperature #3 is typically CPU socket
            sensor = candidates.FirstOrDefault(s => s.Name.Contains("Temperature #3", StringComparison.OrdinalIgnoreCase));
            if (sensor != null && sensor.Value.HasValue) return (float)Math.Round(sensor.Value.Value, 1);

            return null;
        }

        private static float? ExtractGpuTemperature(IHardware gpuHardware)
        {
            var candidates = new List<ISensor>();

            void CollectSensors(IHardware hw)
            {
                foreach (var s in hw.Sensors)
                {
                    if (s.SensorType == SensorType.Temperature && s.Value.HasValue && s.Value.Value > 0)
                    {
                        candidates.Add(s);
                    }
                }
                foreach (var sub in hw.SubHardware)
                {
                    CollectSensors(sub);
                }
            }

            CollectSensors(gpuHardware);

            if (candidates.Count == 0) return null;

            // Target NVIDIA GeForce RTX 3070: prioritize GPU Core
            var sensor = candidates.FirstOrDefault(s => s.Name.Equals("GPU Core", StringComparison.OrdinalIgnoreCase) ||
                                                        s.Name.Contains("GPU Core", StringComparison.OrdinalIgnoreCase));
            if (sensor != null && sensor.Value.HasValue) return (float)Math.Round(sensor.Value.Value, 1);

            sensor = candidates.FirstOrDefault(s => s.Name.Contains("Core", StringComparison.OrdinalIgnoreCase));
            if (sensor != null && sensor.Value.HasValue) return (float)Math.Round(sensor.Value.Value, 1);

            sensor = candidates.FirstOrDefault(s => s.Name.Contains("GPU", StringComparison.OrdinalIgnoreCase));
            if (sensor != null && sensor.Value.HasValue) return (float)Math.Round(sensor.Value.Value, 1);

            return (float)Math.Round(candidates[0].Value!.Value, 1);
        }

        private static string CleanGpuName(string rawName)
        {
            if (string.IsNullOrWhiteSpace(rawName)) return "NVIDIA RTX 3070";
            return rawName.Replace("GeForce ", "").Trim();
        }

        private void SetStatus(bool running)
        {
            if (_isActive != running)
            {
                _isActive = running;
                LhmStatusChanged?.Invoke(running);
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            try
            {
                _pollingTask?.Wait(TimeSpan.FromMilliseconds(500));
            }
            catch { }
            _cts.Dispose();

            lock (_pollLock)
            {
                try
                {
                    _computer?.Close();
                }
                catch { }
                _computer = null;
            }
        }
    }
}
