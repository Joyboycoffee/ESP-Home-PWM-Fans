using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using ESPHomeFanController.Models;

namespace ESPHomeFanController
{
    public partial class App : Application
    {
        public App()
        {
            DispatcherUnhandledException += App_DispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            SessionEnding += App_SessionEnding;
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            _ = new MainWindow();
        }

        private void App_SessionEnding(object sender, SessionEndingCancelEventArgs e)
        {
            try
            {
                var settings = AppSettings.Load();
                using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(1000) };
                string authString = $"{settings.EspUsername}:{settings.EspPassword}";
                string base64 = Convert.ToBase64String(Encoding.ASCII.GetBytes(authString));
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", base64);

                string url = $"http://{settings.EspHost}:{settings.EspPort}/switch/full_speed_override/turn_on";
                using var content = new StringContent(string.Empty);
                var task1 = client.PostAsync(url, content);
                string fanUrl = $"http://{settings.EspHost}:{settings.EspPort}/fan/silent_fan/turn_on?speed_level=100";
                var task2 = client.PostAsync(fanUrl, content);
                System.Threading.Tasks.Task.WaitAll(new[] { task1, task2 }, TimeSpan.FromMilliseconds(1000));
            }
            catch { }
        }

        private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            LogException("DispatcherUnhandledException", e.Exception);
            e.Handled = true;
        }

        private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            if (e.ExceptionObject is Exception ex)
            {
                LogException("UnhandledException", ex);
            }
        }

        private static void LogException(string type, Exception ex)
        {
            try
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fan_controller_debug.log");
                string log = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{type}] {ex.Message}\n{ex.StackTrace}\n\n";
                File.AppendAllText(path, log);
            }
            catch { }
        }
    }
}
