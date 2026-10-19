using System;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ESPHomeFanController.Services
{
    public class HardwareMonitorService
    {
        private readonly HttpClient _httpClient;
        private readonly string _lhmUrl = "http://localhost:8085/data.json";
        private float? _cachedTemperature;
        private DateTime _lastFetchTime = DateTime.MinValue;
        private readonly TimeSpan _cacheDuration = TimeSpan.FromSeconds(1.5);
        private bool _isLhmRunning;

        public event Action<float>? TemperatureUpdated;
        public event Action<bool>? LhmStatusChanged;

        public bool IsLhmRunning => _isLhmRunning;
        public float? CurrentTemperature => _cachedTemperature;

        public HardwareMonitorService()
        {
            var handler = new SocketsHttpHandler
            {
                ConnectTimeout = TimeSpan.FromMilliseconds(800),
                PooledConnectionLifetime = TimeSpan.FromMinutes(5)
            };
            _httpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromMilliseconds(1200)
            };
        }

        public async Task<float?> FetchCpuTemperatureAsync(CancellationToken cancellationToken = default)
        {
            if (DateTime.UtcNow - _lastFetchTime < _cacheDuration && _cachedTemperature.HasValue)
            {
                return _cachedTemperature;
            }

            float? temp = await FetchFromLhmAsync(cancellationToken);

            if (!temp.HasValue)
            {
                // Fallback to WMI if LHM is not providing data
                temp = FetchFromWmi();
            }

            if (temp.HasValue)
            {
                _cachedTemperature = temp.Value;
                _lastFetchTime = DateTime.UtcNow;
                TemperatureUpdated?.Invoke(temp.Value);
            }

            return _cachedTemperature;
        }

        private async Task<float?> FetchFromLhmAsync(CancellationToken cancellationToken)
        {
            try
            {
                using var response = await _httpClient.GetAsync(_lhmUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                    using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

                    float? extractedTemp = ExtractCpuTemperature(doc.RootElement);
                    if (extractedTemp.HasValue)
                    {
                        SetLhmStatus(true);
                        return extractedTemp;
                    }
                }
            }
            catch
            {
                // LHM not running or timed out
            }

            SetLhmStatus(false);
            return null;
        }

        private void SetLhmStatus(bool running)
        {
            if (_isLhmRunning != running)
            {
                _isLhmRunning = running;
                LhmStatusChanged?.Invoke(running);
            }
        }

        private float? ExtractCpuTemperature(JsonElement element)
        {
            try
            {
                if (element.TryGetProperty("Children", out var computers) && computers.ValueKind == JsonValueKind.Array)
                {
                    foreach (var computer in computers.EnumerateArray())
                    {
                        if (computer.TryGetProperty("Children", out var hardwareList) && hardwareList.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var hardware in hardwareList.EnumerateArray())
                            {
                                string text = hardware.TryGetProperty("Text", out var t) ? t.GetString() ?? "" : "";
                                if (text.Contains("CPU", StringComparison.OrdinalIgnoreCase) ||
                                    text.Contains("Ryzen", StringComparison.OrdinalIgnoreCase) ||
                                    text.Contains("Core", StringComparison.OrdinalIgnoreCase) ||
                                    text.Contains("Intel", StringComparison.OrdinalIgnoreCase) ||
                                    text.Contains("AMD", StringComparison.OrdinalIgnoreCase))
                                {
                                    if (hardware.TryGetProperty("Children", out var sensorGroups) && sensorGroups.ValueKind == JsonValueKind.Array)
                                    {
                                        foreach (var group in sensorGroups.EnumerateArray())
                                        {
                                            string groupText = group.TryGetProperty("Text", out var gt) ? gt.GetString() ?? "" : "";
                                            if (groupText.Equals("Temperatures", StringComparison.OrdinalIgnoreCase))
                                            {
                                                if (group.TryGetProperty("Children", out var sensors) && sensors.ValueKind == JsonValueKind.Array)
                                                {
                                                    float? fallbackCoreTemp = null;
                                                    foreach (var sensor in sensors.EnumerateArray())
                                                    {
                                                        string sensorName = sensor.TryGetProperty("Text", out var st) ? st.GetString() ?? "" : "";
                                                        string valStr = sensor.TryGetProperty("Value", out var v) ? v.GetString() ?? "" : "";

                                                        if (TryParseTemperature(valStr, out float val))
                                                        {
                                                            // Prioritize Package / Tctl/Tdie
                                                            if (sensorName.Contains("Package", StringComparison.OrdinalIgnoreCase) ||
                                                                sensorName.Contains("Tctl", StringComparison.OrdinalIgnoreCase) ||
                                                                sensorName.Contains("Tdie", StringComparison.OrdinalIgnoreCase) ||
                                                                sensorName.Contains("Average", StringComparison.OrdinalIgnoreCase))
                                                            {
                                                                return val;
                                                            }

                                                            if (!fallbackCoreTemp.HasValue && sensorName.Contains("Core", StringComparison.OrdinalIgnoreCase))
                                                            {
                                                                fallbackCoreTemp = val;
                                                            }
                                                        }
                                                    }

                                                    if (fallbackCoreTemp.HasValue)
                                                        return fallbackCoreTemp;
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[LHM Parse Error] {ex.Message}");
            }

            return null;
        }

        private static bool TryParseTemperature(string raw, out float temp)
        {
            temp = 0;
            if (string.IsNullOrWhiteSpace(raw)) return false;

            string cleaned = raw.Split(' ')[0].Trim();
            return float.TryParse(cleaned, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out temp);
        }

        private float? FetchFromWmi()
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");
                foreach (ManagementObject obj in searcher.Get())
                {
                    double kelvinTimes10 = Convert.ToDouble(obj["CurrentTemperature"]);
                    double celsius = (kelvinTimes10 / 10.0) - 273.15;
                    if (celsius > 10 && celsius < 120)
                    {
                        return (float)Math.Round(celsius, 1);
                    }
                }
            }
            catch
            {
                // WMI ThermalZone not supported on all motherboards
            }

            return null;
        }
    }
}
