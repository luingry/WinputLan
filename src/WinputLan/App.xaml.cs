using System;
using System.IO;
using System.Windows;
using WinputLan.Core;
using WinputLan.Runtime;

namespace WinputLan
{
    public partial class App : Application
    {
        public static WinputConfig Config { get; private set; }
        public static AppConfigStore ConfigStore { get; private set; }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            var appDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinputLan");
            ConfigStore = new AppConfigStore(appDirectory);
            Config = ConfigStore.LoadOrCreate();
            MainWindow = new MainWindow(Config, ConfigStore);
            MainWindow.Show();
        }
    }
}
