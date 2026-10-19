using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ESPHomeFanController.Models
{
    public class CurvePoint
    {
        [JsonPropertyName("temp")]
        public int Temperature { get; set; }

        [JsonPropertyName("speed")]
        public int Speed { get; set; }

        public CurvePoint() { }

        public CurvePoint(int temp, int speed)
        {
            Temperature = temp;
            Speed = Math.Clamp(speed, 0, 100);
        }
    }

    public class AppSettings
    {
        [JsonPropertyName("thresholds")]
        public List<CurvePoint> Thresholds { get; set; } = new()
        {
            new CurvePoint(30, 0),
            new CurvePoint(40, 25),
            new CurvePoint(50, 50),
            new CurvePoint(60, 75),
            new CurvePoint(70, 100)
        };

        [JsonPropertyName("automation_enabled")]
        public bool AutomationEnabled { get; set; } = false;

        [JsonPropertyName("esp_host")]
        public string EspHost { get; set; } = "192.168.1.40";

        [JsonPropertyName("esp_port")]
        public int EspPort { get; set; } = 80;

        [JsonPropertyName("esp_username")]
        public string EspUsername { get; set; } = "admin";

        [JsonPropertyName("esp_password")]
        public string EspPassword { get; set; } = "admin";

        [JsonPropertyName("manual_speed")]
        public int ManualSpeed { get; set; } = 0;

        [JsonPropertyName("full_speed_override")]
        public bool FullSpeedOverride { get; set; } = false;

        [JsonPropertyName("window_left")]
        public double WindowLeft { get; set; } = 100;

        [JsonPropertyName("window_top")]
        public double WindowTop { get; set; } = 100;

        [JsonPropertyName("window_width")]
        public double WindowWidth { get; set; } = 980;

        [JsonPropertyName("window_height")]
        public double WindowHeight { get; set; } = 640;

        private static readonly string SettingsFilePath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "fan_settings.json"
        );

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(SettingsFilePath))
                {
                    string json = File.ReadAllText(SettingsFilePath);
                    var settings = JsonSerializer.Deserialize<AppSettings>(json);
                    if (settings != null)
                    {
                        if (settings.Thresholds == null || settings.Thresholds.Count == 0)
                        {
                            settings.Thresholds = new List<CurvePoint>
                            {
                                new CurvePoint(30, 0),
                                new CurvePoint(40, 25),
                                new CurvePoint(50, 50),
                                new CurvePoint(60, 75),
                                new CurvePoint(70, 100)
                            };
                        }
                        return settings;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Settings] Load error: {ex.Message}");
            }

            return new AppSettings();
        }

        public void Save()
        {
            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(this, options);
                File.WriteAllText(SettingsFilePath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Settings] Save error: {ex.Message}");
            }
        }
    }
}
