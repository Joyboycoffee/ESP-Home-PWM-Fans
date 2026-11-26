using System;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ESPHomeFanController.Services
{
    public class EspHomeService : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly SemaphoreSlim _httpLock = new(1, 1);

        private string _host = "192.168.1.40";
        private int _port = 80;
        private string _username = "admin";
        private string _password = "admin";

        private bool _isConnected;
        private int _consecutiveFailures;
        private int _lastCommandedSpeed = -1;
        private DateTime _lastCommandTime = DateTime.MinValue;
        private DateTime _lastTelemetryPoll = DateTime.MinValue;
        private readonly TimeSpan _minCommandInterval = TimeSpan.FromMilliseconds(200);

        private CancellationTokenSource? _workerCts;
        private Task? _backgroundTask;

        public event Action<string>? ConnectionStatusChanged;
        public event Action<bool, int>? FanStateChanged;
        public event Action<string, float?>? TelemetryUpdated;
        public event Action? ConnectionRestored;
        public event Action<bool>? FullSpeedOverrideDetectedOnDevice;

        public bool IsConnected => _isConnected;
        public string Host => _host;
        public int Port => _port;

        public EspHomeService()
        {
            var handler = new SocketsHttpHandler
            {
                ConnectTimeout = TimeSpan.FromMilliseconds(1500),
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                MaxConnectionsPerServer = 2
            };
            _httpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromMilliseconds(2000)
            };
            UpdateAuthHeader();
        }

        private void UpdateAuthHeader()
        {
            if (!string.IsNullOrEmpty(_username))
            {
                string authString = $"{_username}:{_password}";
                string base64 = Convert.ToBase64String(Encoding.ASCII.GetBytes(authString));
                _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", base64);
            }
            else
            {
                _httpClient.DefaultRequestHeaders.Authorization = null;
            }
        }

        public void Start(string host, int port = 80, string username = "admin", string password = "admin")
        {
            _host = host.Trim();
            _port = port > 0 ? port : 80;
            _username = username;
            _password = password;
            UpdateAuthHeader();

            Stop();

            _workerCts = new CancellationTokenSource();
            _backgroundTask = Task.Run(() => SupervisorLoopAsync(_workerCts.Token));
        }

        public async Task<bool> ForceReconnectAsync(string? host = null, int? port = null, string? username = null, string? password = null)
        {
            if (!string.IsNullOrWhiteSpace(host)) _host = host.Trim();
            if (port.HasValue && port.Value > 0) _port = port.Value;
            if (username != null) _username = username;
            if (password != null) _password = password;
            UpdateAuthHeader();

            SetStatus("Connecting");
            _consecutiveFailures = 0;

            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                bool success = await PollDeviceStateAsync(cts.Token);
                if (success)
                {
                    _isConnected = true;
                    _lastCommandedSpeed = -1;
                    SetStatus("Connected");
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            using var ovCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                            await SetFullSpeedOverrideAsync(false, ovCts.Token);
                        }
                        catch { }
                    });
                    ConnectionRestored?.Invoke();
                    return true;
                }
                else
                {
                    _isConnected = false;
                    SetStatus("Disconnected");
                    return false;
                }
            }
            catch
            {
                _isConnected = false;
                SetStatus("Disconnected");
                return false;
            }
        }

        public void Stop()
        {
            _workerCts?.Cancel();
            try
            {
                _backgroundTask?.Wait(TimeSpan.FromSeconds(1));
            }
            catch { }
            _workerCts?.Dispose();
            _workerCts = null;
            _backgroundTask = null;
            _isConnected = false;
        }

        private async Task SupervisorLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    bool isResponding = await PollDeviceStateAsync(token);
                    if (isResponding)
                    {
                        bool wasDisconnected = !_isConnected || _consecutiveFailures > 0;
                        _consecutiveFailures = 0;

                        if (!_isConnected)
                        {
                            _isConnected = true;
                            _lastCommandedSpeed = -1;
                            SetStatus("Connected");
                        }

                        if (wasDisconnected)
                        {
                            // Recovered from a hiccup or reconnect! Clear override and re-sync
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                                    await SetFullSpeedOverrideAsync(false, cts.Token);
                                }
                                catch { }
                            });

                            ConnectionRestored?.Invoke();
                        }

                        await Task.Delay(2500, token);
                    }
                    else
                    {
                        _consecutiveFailures++;
                        // If 2 consecutive failures occur, mark as disconnected
                        if (_consecutiveFailures >= 2 && _isConnected)
                        {
                            _isConnected = false;
                            _lastCommandedSpeed = -1;
                            SetStatus("Disconnected");
                        }
                        await Task.Delay(1500, token);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[EspHome] Loop error: {ex.Message}");
                    _consecutiveFailures++;
                    if (_consecutiveFailures >= 2 && _isConnected)
                    {
                        _isConnected = false;
                        _lastCommandedSpeed = -1;
                        SetStatus("Disconnected");
                    }
                    await Task.Delay(1500, token);
                }
            }
        }

        private async Task<bool> PollDeviceStateAsync(CancellationToken token)
        {
            if (!await _httpLock.WaitAsync(1000, token))
            {
                return _isConnected; // If busy with a command, assume still connected
            }

            try
            {
                string baseUrl = $"http://{_host}:{_port}";

                // 1. Poll Fan State
                string fanUrl = $"{baseUrl}/fan/silent_fan";
                using var fanResponse = await _httpClient.GetAsync(fanUrl, token);
                if (!fanResponse.IsSuccessStatusCode)
                {
                    return false;
                }

                string json = await fanResponse.Content.ReadAsStringAsync(token);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                string state = root.TryGetProperty("state", out var st) ? st.GetString() ?? "" : "";
                int speedLevel = 0;
                if (root.TryGetProperty("speed_level", out var sl))
                {
                    speedLevel = sl.GetInt32();
                }

                bool isOn = state.Equals("ON", StringComparison.OrdinalIgnoreCase);
                FanStateChanged?.Invoke(isOn, isOn ? speedLevel : 0);

                // 2. Poll Full Speed Override switch state on device to detect if hardware tripped
                try
                {
                    string overrideUrl = $"{baseUrl}/switch/full_speed_override";
                    using var overrideResp = await _httpClient.GetAsync(overrideUrl, token);
                    if (overrideResp.IsSuccessStatusCode)
                    {
                        string ovJson = await overrideResp.Content.ReadAsStringAsync(token);
                        using var ovDoc = JsonDocument.Parse(ovJson);
                        if (ovDoc.RootElement.TryGetProperty("state", out var ovState))
                        {
                            bool isOverrideActive = ovState.GetString()?.Equals("ON", StringComparison.OrdinalIgnoreCase) ?? false;
                            FullSpeedOverrideDetectedOnDevice?.Invoke(isOverrideActive);
                        }
                    }
                }
                catch { }

                // 3. Stagger Uptime and WiFi telemetry polling to only run every 15 seconds
                // This avoids overloading the ESP8266's limited CPU and lwIP socket pool
                if (DateTime.UtcNow - _lastTelemetryPoll > TimeSpan.FromSeconds(15))
                {
                    _lastTelemetryPoll = DateTime.UtcNow;
                    string uptimeStr = "--";
                    float? wifiDbm = null;

                    try
                    {
                        string uptimeUrl = $"{baseUrl}/sensor/uptime";
                        using var uptimeResp = await _httpClient.GetAsync(uptimeUrl, token);
                        if (uptimeResp.IsSuccessStatusCode)
                        {
                            string utJson = await uptimeResp.Content.ReadAsStringAsync(token);
                            using var utDoc = JsonDocument.Parse(utJson);
                            if (utDoc.RootElement.TryGetProperty("value", out var utVal))
                            {
                                double seconds = utVal.GetDouble();
                                int d = (int)(seconds / 86400);
                                int h = (int)((seconds % 86400) / 3600);
                                int m = (int)((seconds % 3600) / 60);
                                uptimeStr = d > 0 ? $"{d}d {h}h {m}m" : $"{h}h {m}m";
                            }
                        }
                    }
                    catch { }

                    try
                    {
                        string wifiUrl = $"{baseUrl}/sensor/wifi_signal_strength";
                        using var wifiResp = await _httpClient.GetAsync(wifiUrl, token);
                        if (wifiResp.IsSuccessStatusCode)
                        {
                            string wifiJson = await wifiResp.Content.ReadAsStringAsync(token);
                            using var wifiDoc = JsonDocument.Parse(wifiJson);
                            if (wifiDoc.RootElement.TryGetProperty("value", out var wifiVal))
                            {
                                wifiDbm = (float)Math.Round(wifiVal.GetDouble(), 0);
                            }
                        }
                    }
                    catch { }

                    TelemetryUpdated?.Invoke(uptimeStr, wifiDbm);
                }

                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[EspHome] Poll exception: {ex.Message}");
                return false;
            }
            finally
            {
                _httpLock.Release();
            }
        }

        public async Task<bool> SetFanSpeedAsync(int speedPercent, CancellationToken token = default)
        {
            if (!_isConnected) return false;

            if (_lastCommandedSpeed == speedPercent && DateTime.UtcNow - _lastCommandTime < _minCommandInterval)
            {
                return true;
            }

            if (!await _httpLock.WaitAsync(1500, token))
            {
                return false;
            }

            try
            {
                string baseUrl = $"http://{_host}:{_port}";
                // If 0%, call turn_off to ensure PWM duty cycle is 0% and fan stops spinning completely
                string url = speedPercent == 0
                    ? $"{baseUrl}/fan/silent_fan/turn_off"
                    : $"{baseUrl}/fan/silent_fan/turn_on?speed_level={speedPercent}";

                using var content = new StringContent(string.Empty);
                using var res = await _httpClient.PostAsync(url, content, token);

                if (res.IsSuccessStatusCode)
                {
                    _lastCommandedSpeed = speedPercent;
                    _lastCommandTime = DateTime.UtcNow;
                    _consecutiveFailures = 0;
                    return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[EspHome] SetSpeed failed: {ex.Message}");
                return false;
            }
            finally
            {
                _httpLock.Release();
            }
        }

        public async Task<bool> SetFullSpeedOverrideAsync(bool enable, CancellationToken token = default)
        {
            if (!await _httpLock.WaitAsync(1500, token))
            {
                return false;
            }

            try
            {
                return await SetFullSpeedOverrideInternalAsync(enable, token);
            }
            finally
            {
                _httpLock.Release();
            }
        }

        private async Task<bool> SetFullSpeedOverrideInternalAsync(bool enable, CancellationToken token)
        {
            try
            {
                string action = enable ? "turn_on" : "turn_off";
                string url = $"http://{_host}:{_port}/switch/full_speed_override/{action}";
                using var content = new StringContent(string.Empty);
                using var response = await _httpClient.PostAsync(url, content, token);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[EspHome] Override failed: {ex.Message}");
                return false;
            }
        }

        private void SetStatus(string status)
        {
            ConnectionStatusChanged?.Invoke(status);
        }

        public void Dispose()
        {
            Stop();
            _httpClient.Dispose();
            _httpLock.Dispose();
        }
    }
}
