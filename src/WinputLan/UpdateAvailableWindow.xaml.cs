using System.Windows;
using System.Windows.Input;

namespace WinputLan
{
    // Asks whether to download and install a validated update; DialogResult is true for "Sim".
    public partial class UpdateAvailableWindow : Window
    {
        public UpdateAvailableWindow(string newVersion, string installedVersion)
        {
            InitializeComponent();
            InstalledVersionText.Text = "v" + installedVersion;
            NewVersionText.Text = "v" + newVersion;
            MessageText.Text = "A versão " + newVersion + " do Winput LAN está disponível. Deseja baixar e instalar agora? O app fecha durante a instalação e reabre sozinho.";
            Loaded += (s, e) => YesButton.Focus();
        }

        private void Chrome_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) { DragMove(); }
        private void Yes_Click(object sender, RoutedEventArgs e) { DialogResult = true; }
        private void No_Click(object sender, RoutedEventArgs e) { DialogResult = false; }
    }
}
