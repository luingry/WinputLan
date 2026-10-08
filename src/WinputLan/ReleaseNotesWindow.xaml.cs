using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using WinputLan.Core;

namespace WinputLan
{
    public partial class ReleaseNotesWindow : Window
    {
        public ReleaseNotesWindow(string version)
        {
            InitializeComponent();
            TitleText.Text = "Release notes v" + version;
            Title = TitleText.Text;
            var sections = ReleaseNotes.Parse(ReadChangelog(), version);
            if (sections.Count == 0) { NotesPanel.Children.Add(new TextBlock { Text = "Nenhuma nota disponível para esta versão.", Style = (Style)FindResource("Text.Secondary"), TextWrapping = TextWrapping.Wrap }); return; }
            for (var i = 0; i < sections.Count; i++)
            {
                var section = sections[i];
                if (section.Heading.Length > 0)
                    NotesPanel.Children.Add(new TextBlock { Text = section.Heading.ToUpperInvariant(), Style = (Style)FindResource("Text.Overline"), Margin = new Thickness(0, i == 0 ? 0 : 14, 0, 6) });
                foreach (var item in section.Items) NotesPanel.Children.Add(Bullet(item));
            }
        }

        private static string ReadChangelog()
        {
            using (var stream = typeof(ReleaseNotesWindow).Assembly.GetManifestResourceStream("WinputLan.CHANGELOG.md"))
            {
                if (stream == null) return "";
                using (var reader = new StreamReader(stream)) return reader.ReadToEnd();
            }
        }

        private UIElement Bullet(string markdown)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.Children.Add(new TextBlock { Text = "•", Foreground = (Brush)FindResource("AccentBrush"), FontSize = 13 });
            var text = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 13, LineHeight = 19, Foreground = (Brush)FindResource("TextBrush") };
            // Minimal inline Markdown: `code` and **bold**; links keep only their label.
            markdown = Regex.Replace(markdown, "\\[([^\\]]+)\\]\\([^)]*\\)", "$1");
            foreach (var part in Regex.Split(markdown, "(`[^`]+`|\\*\\*[^*]+\\*\\*)"))
            {
                if (part.Length == 0) continue;
                if (part.Length > 2 && part[0] == '`') text.Inlines.Add(new Run(part.Substring(1, part.Length - 2)) { FontFamily = new FontFamily("Consolas"), Foreground = (Brush)FindResource("AccentStrongBrush") });
                else if (part.Length > 4 && part.StartsWith("**")) text.Inlines.Add(new Run(part.Substring(2, part.Length - 4)) { FontWeight = FontWeights.SemiBold });
                else text.Inlines.Add(new Run(part));
            }
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);
            return grid;
        }

        private void Chrome_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) { DragMove(); }
        private void Close_Click(object sender, RoutedEventArgs e) { Close(); }
    }
}
