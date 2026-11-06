using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using WinputLan.Controls;
using WinputLan.Core;

namespace WinputLan
{
    public sealed class EdgeLayoutResult
    {
        public ScreenEdge LocalEdge;
        public ScreenEdge RemoteEdge;
        public EdgeMonitor LocalMonitor;
        public EdgeMonitor RemoteMonitor;
        public int OffsetX;
        public int OffsetY;
        public bool ConnectEnabled;
    }

    // Arranges this PC's and the other PC's monitors. Each PC moves as a block, since Windows owns the layout inside it,
    // and always lands touching the other PC: the edges in contact are the ones that switch control.
    public partial class EdgeLayoutWindow : Window
    {
        private const double StageWidth = 812, StageHeight = 380, MaxScale = 0.085, CursorTravel = 58, WashDepth = 28;
        private const double StillCursorPhase = 0.12, CursorCycleSeconds = 2.6;
        private static readonly Color Accent = Color.FromRgb(0x79, 0xD8, 0x8B);
        private static readonly Color LocalTint = Color.FromRgb(0x2B, 0x42, 0x52), LocalIconTint = Color.FromRgb(0xAF, 0xC9, 0xE3);
        private static readonly Color RemoteTint = Color.FromRgb(0x3A, 0x39, 0x60), RemoteIconTint = Color.FromRgb(0xB9, 0xC6, 0xFF);
        private readonly List<EdgeMonitor> _local;
        private readonly string _remoteName;
        private readonly DispatcherTimer _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        private readonly DateTime _started = DateTime.UtcNow;
        private List<EdgeMonitor> _remote;
        private List<EdgeMonitor> _pendingRemote;
        private bool _remoteKnown;
        private int _localX, _localY, _remoteX, _remoteY;
        private double _scale, _originX, _originY;
        private bool _dragging, _draggingRemote;
        private Point _grab;
        private int _grabX, _grabY;
        private EdgeSnap _snap;
        private FrameworkElement _cursor;
        private Rectangle _trail, _seam;
        private EdgeContact _cursorContact;

        public event Action<EdgeLayoutResult> Saved;

        public EdgeLayoutWindow(List<EdgeMonitor> local, WinputConfig config, string localName, string remoteName)
        {
            InitializeComponent();
            _local = local != null && local.Count > 0 ? local : new List<EdgeMonitor> { new EdgeMonitor { Right = 1920, Bottom = 1080, Primary = true } };
            _remoteName = remoteName;
            _remoteKnown = config.RemoteMonitors != null && config.RemoteMonitors.Count > 0;
            // Until the other PC reports its monitors, a stand-in of a common size keeps the window usable.
            _remote = _remoteKnown ? config.RemoteMonitors : new List<EdgeMonitor> { new EdgeMonitor { Right = 1920, Bottom = 1080, Primary = true } };
            LocalLegendText.Text = localName;
            RemoteLegendText.Text = remoteName;
            ConnectCheckBox.IsChecked = config.EdgeConnectEnabled;
            if (config.EdgeLayoutSaved) { _remoteX = config.EdgeLayoutOffsetX; _remoteY = config.EdgeLayoutOffsetY; }
            else PlaceFromEdge(config.LocalEdge);
            Relayout();
            if (SystemParameters.ClientAreaAnimation)
            {
                _timer.Tick += (s, e) => PlaceCursor((DateTime.UtcNow - _started).TotalSeconds / CursorCycleSeconds);
                _timer.Start();
            }
            Closed += (s, e) => _timer.Stop();
        }

        // The other PC reported its monitors, possibly while this window is open.
        public void UpdateRemoteMonitors(List<EdgeMonitor> monitors)
        {
            if (_dragging) { _pendingRemote = monitors; return; }
            _remote = monitors;
            _remoteKnown = true;
            Relayout();
        }

        // Settings older than this window only have the edges: start from the primary monitors on those sides.
        private void PlaceFromEdge(ScreenEdge edge)
        {
            var primary = _local.FirstOrDefault(m => m.Primary) ?? _local[0];
            var other = _remote.FirstOrDefault(m => m.Primary) ?? _remote[0];
            _remoteX = primary.Left - other.Left;
            _remoteY = primary.Top - other.Top;
            if (edge == ScreenEdge.Left) _remoteX = primary.Left - other.Width - other.Left;
            else if (edge == ScreenEdge.Top) _remoteY = primary.Top - other.Height - other.Top;
            else if (edge == ScreenEdge.Bottom) _remoteY = primary.Bottom - other.Top;
            else _remoteX = primary.Right - other.Left;
        }

        // Snaps to a valid placement and fits the view with room for any arrangement of the two PCs.
        private void Relayout()
        {
            var snap = EdgeLayout.Find(_remote, _remoteX, _remoteY, _local, _localX, _localY);
            if (snap != null) { _remoteX = snap.X; _remoteY = snap.Y; }
            var width = _local.Max(m => m.Right) - _local.Min(m => m.Left) + _remote.Max(m => m.Right) - _remote.Min(m => m.Left);
            var height = _local.Max(m => m.Bottom) - _local.Min(m => m.Top) + _remote.Max(m => m.Bottom) - _remote.Min(m => m.Top);
            _scale = Math.Min(MaxScale, Math.Min((StageWidth - 48) / width, (StageHeight - 24) / height));
            Center();
            Draw();
        }

        private void Center()
        {
            var left = Math.Min(_local.Min(m => m.Left) + _localX, _remote.Min(m => m.Left) + _remoteX);
            var right = Math.Max(_local.Max(m => m.Right) + _localX, _remote.Max(m => m.Right) + _remoteX);
            var top = Math.Min(_local.Min(m => m.Top) + _localY, _remote.Min(m => m.Top) + _remoteY);
            var bottom = Math.Max(_local.Max(m => m.Bottom) + _localY, _remote.Max(m => m.Bottom) + _remoteY);
            _originX = (left + right) / 2.0 - StageWidth / 2 / _scale;
            _originY = (top + bottom) / 2.0 - StageHeight / 2 / _scale;
        }

        private double Sx(double x) { return Math.Round((x - _originX) * _scale); }
        private double Sy(double y) { return Math.Round((y - _originY) * _scale); }

        private void Draw()
        {
            Stage.Children.Clear();
            _cursor = null; _trail = null; _seam = null; _cursorContact = null;
            if (_dragging)
            {
                var moving = _draggingRemote ? _remote : _local;
                var other = _draggingRemote ? _local : _remote;
                int movingX = _draggingRemote ? _remoteX : _localX, movingY = _draggingRemote ? _remoteY : _localY;
                int otherX = _draggingRemote ? _localX : _remoteX, otherY = _draggingRemote ? _localY : _remoteY;
                _snap = EdgeLayout.Find(moving, movingX, movingY, other, otherX, otherY);
                foreach (var monitor in moving) DrawGhost(monitor, _snap.X, _snap.Y);
                foreach (var monitor in other) DrawTile(monitor, !_draggingRemote, otherX, otherY, false);
                DrawGuide();
                var preview = _draggingRemote ? EdgeLayout.Contacts(_local, _localX, _localY, _remote, _snap.X, _snap.Y) : EdgeLayout.Contacts(_local, _snap.X, _snap.Y, _remote, _remoteX, _remoteY);
                var previewBest = EdgeLayout.Best(preview);
                if (previewBest != null) DrawContact(previewBest, 0.75);
                foreach (var monitor in moving) DrawTile(monitor, _draggingRemote, movingX, movingY, true);
                UpdateSummary(previewBest, "Ao soltar: ", "Prévia");
                return;
            }
            foreach (var monitor in _local) DrawTile(monitor, false, _localX, _localY, false);
            foreach (var monitor in _remote) DrawTile(monitor, true, _remoteX, _remoteY, false);
            var best = EdgeLayout.Best(EdgeLayout.Contacts(_local, _localX, _localY, _remote, _remoteX, _remoteY));
            UpdateSummary(best, string.Empty, "Ativa");
            if (best == null) return;
            _seam = DrawContact(best, 1);
            _cursorContact = best;
            _trail = new Rectangle { IsHitTestVisible = false };
            Stage.Children.Add(_trail);
            _cursor = MakePointer();
            Stage.Children.Add(_cursor);
            PlaceCursor(StillCursorPhase);
        }

        private string MonitorName(EdgeMonitor monitor, bool remote)
        {
            if (remote && !_remoteKnown) return "Outro PC";
            if (monitor.Internal) return "Tela interna";
            var group = remote ? _remote : _local;
            return "Monitor " + (group.OrderByDescending(m => m.Primary).ThenBy(m => m.Left).ThenBy(m => m.Top).ToList().IndexOf(monitor) + 1);
        }

        private void DrawTile(EdgeMonitor monitor, bool remote, int offsetX, int offsetY, bool lifted)
        {
            double width = monitor.Width * _scale, height = monitor.Height * _scale;
            var name = MonitorName(monitor, remote);
            var size = remote && !_remoteKnown ? "tamanho estimado" : monitor.Width + " × " + monitor.Height;
            var iconTint = new SolidColorBrush(remote ? RemoteIconTint : LocalIconTint);
            var tile = new Border
            {
                Width = width, Height = height,
                Background = new SolidColorBrush(remote ? RemoteTint : LocalTint),
                BorderBrush = (Brush)FindResource("RailBrush"), BorderThickness = new Thickness(1),
                Cursor = Cursors.SizeAll, Tag = remote, ToolTip = name + " · " + size
            };
            if (lifted) tile.Effect = new DropShadowEffect { BlurRadius = 22, ShadowDepth = 6, Direction = 270, Opacity = 0.55, Color = Colors.Black };
            var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            row.Children.Add(new Icon { Data = (Geometry)FindResource(monitor.Internal ? "Icon.Laptop" : "Icon.Monitor"), Width = 30, Height = 30, StrokeThickness = 1.5, Foreground = iconTint, VerticalAlignment = VerticalAlignment.Center });
            // Small tiles keep only what fits; the tooltip always has it all.
            if (width >= 120 && height >= 44)
            {
                var labels = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                labels.Children.Add(new TextBlock { Text = name, FontSize = 13, FontWeight = FontWeights.SemiBold });
                if (height >= 62) labels.Children.Add(new TextBlock { Text = size, FontSize = 12, Foreground = (Brush)FindResource("MutedBrush"), Margin = new Thickness(0, 1, 0, 0) });
                if (monitor.Primary && (!remote || _remoteKnown) && height >= 84) labels.Children.Add(new TextBlock { Text = "PRINCIPAL", Style = (Style)FindResource("Text.Overline"), FontSize = 10, Foreground = iconTint, Margin = new Thickness(0, 3, 0, 0) });
                row.Children.Add(labels);
            }
            tile.Child = row;
            tile.MouseLeftButtonDown += Tile_MouseLeftButtonDown;
            Canvas.SetLeft(tile, Sx(monitor.Left + offsetX)); Canvas.SetTop(tile, Sy(monitor.Top + offsetY));
            Stage.Children.Add(tile);
        }

        private void DrawGhost(EdgeMonitor monitor, int offsetX, int offsetY)
        {
            var ghost = new Rectangle
            {
                Width = monitor.Width * _scale, Height = monitor.Height * _scale,
                Fill = new SolidColorBrush(Color.FromArgb(0x1F, Accent.R, Accent.G, Accent.B)),
                Stroke = new SolidColorBrush(Accent), StrokeThickness = 1.5, StrokeDashArray = new DoubleCollection { 4, 3 }
            };
            Canvas.SetLeft(ghost, Sx(monitor.Left + offsetX)); Canvas.SetTop(ghost, Sy(monitor.Top + offsetY));
            Stage.Children.Add(ghost);
        }

        private void DrawGuide()
        {
            var guide = new Line { Stroke = new SolidColorBrush(Color.FromArgb(0x80, Accent.R, Accent.G, Accent.B)), StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 3, 4 }, IsHitTestVisible = false };
            if (_snap.GuideVertical) { guide.X1 = guide.X2 = Sx(_snap.Guide); guide.Y1 = 0; guide.Y2 = StageHeight; }
            else { guide.Y1 = guide.Y2 = Sy(_snap.Guide); guide.X1 = 0; guide.X2 = StageWidth; }
            Stage.Children.Add(guide);
        }

        // The whole edge of each monitor triggers the switch, so the strip and the wash cover both edges end to end.
        private Rectangle DrawContact(EdgeContact contact, double opacity)
        {
            var firstBeforeLine = contact.FirstEdge == ScreenEdge.Right || contact.FirstEdge == ScreenEdge.Bottom;
            DrawWash(contact, contact.FirstStart, contact.FirstEnd, firstBeforeLine, opacity);
            DrawWash(contact, contact.SecondStart, contact.SecondEnd, !firstBeforeLine, opacity);
            var edgeStart = Math.Min(contact.FirstStart, contact.SecondStart);
            var length = (Math.Max(contact.FirstEnd, contact.SecondEnd) - edgeStart) * _scale;
            var line = contact.Vertical ? Sx(contact.Line) : Sy(contact.Line);
            var start = contact.Vertical ? Sy(edgeStart) : Sx(edgeStart);
            var seam = new Rectangle
            {
                Width = contact.Vertical ? 4 : length, Height = contact.Vertical ? length : 4, RadiusX = 2, RadiusY = 2, IsHitTestVisible = false, Opacity = opacity,
                Fill = (Brush)FindResource("AccentBrush"),
                Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 0, Opacity = 0.85, Color = Accent }
            };
            Canvas.SetLeft(seam, contact.Vertical ? line - 2 : start); Canvas.SetTop(seam, contact.Vertical ? start : line - 2);
            Stage.Children.Add(seam);
            return seam;
        }

        private void DrawWash(EdgeContact contact, int from, int to, bool beforeLine, double opacity)
        {
            var length = (to - from) * _scale;
            var line = contact.Vertical ? Sx(contact.Line) : Sy(contact.Line);
            var start = contact.Vertical ? Sy(from) : Sx(from);
            var clear = Color.FromArgb(0, Accent.R, Accent.G, Accent.B);
            var lit = Color.FromArgb(0x48, Accent.R, Accent.G, Accent.B);
            var wash = new Rectangle
            {
                Width = contact.Vertical ? WashDepth : length, Height = contact.Vertical ? length : WashDepth, IsHitTestVisible = false, Opacity = opacity,
                Fill = new LinearGradientBrush(beforeLine ? clear : lit, beforeLine ? lit : clear, contact.Vertical ? 0 : 90)
            };
            var across = beforeLine ? line - WashDepth : line;
            Canvas.SetLeft(wash, contact.Vertical ? across : start); Canvas.SetTop(wash, contact.Vertical ? start : across);
            Stage.Children.Add(wash);
        }

        private FrameworkElement MakePointer()
        {
            var path = new Path { Data = (Geometry)FindResource("Icon.MousePointer"), Fill = Brushes.White, Stroke = new SolidColorBrush(Color.FromRgb(0x10, 0x15, 0x19)), StrokeThickness = 1.75, StrokeLineJoin = PenLineJoin.Round };
            var host = new Canvas { Width = 24, Height = 24, IsHitTestVisible = false, Effect = new DropShadowEffect { BlurRadius = 6, ShadowDepth = 1, Opacity = 0.6, Color = Colors.Black } };
            host.Children.Add(path);
            return host;
        }

        // phase is in cycles: the pointer glides across the seam and back, so both directions read as live.
        private void PlaceCursor(double phase)
        {
            if (_cursor == null || _cursorContact == null) return;
            var angle = phase * Math.PI * 2;
            var position = Math.Sin(angle) * CursorTravel;
            var moving = Math.Cos(angle) >= 0 ? 1 : -1;
            var extent = Math.Min(46, Math.Abs(Math.Cos(angle)) * 60);
            var contact = _cursorContact;
            var sign = contact.FirstEdge == ScreenEdge.Right || contact.FirstEdge == ScreenEdge.Bottom ? 1 : -1;
            // Runs low on the shared stretch so the pointer passes under the tile's icon and labels, not over them.
            var along = contact.Start + (contact.End - contact.Start) * 0.84;
            double x, y;
            if (contact.Vertical) { x = Sx(contact.Line) + sign * position; y = Sy(along); }
            else { x = Sx(along); y = Sy(contact.Line) + sign * position; }
            Canvas.SetLeft(_cursor, x - 4); Canvas.SetTop(_cursor, y - 4);
            var from = Color.FromArgb(0, Accent.R, Accent.G, Accent.B);
            var to = Color.FromArgb(0xC0, Accent.R, Accent.G, Accent.B);
            var forward = sign * moving > 0;
            if (contact.Vertical)
            {
                _trail.Width = extent; _trail.Height = 3;
                _trail.Fill = new LinearGradientBrush(forward ? from : to, forward ? to : from, 0);
                Canvas.SetLeft(_trail, forward ? x - extent : x); Canvas.SetTop(_trail, y - 1.5);
            }
            else
            {
                _trail.Width = 3; _trail.Height = extent;
                _trail.Fill = new LinearGradientBrush(forward ? from : to, forward ? to : from, 90);
                Canvas.SetLeft(_trail, x - 1.5); Canvas.SetTop(_trail, forward ? y - extent : y);
            }
            if (_seam != null) _seam.Opacity = 0.7 + 0.3 * Math.Max(0, 1 - Math.Abs(position) / 24);
        }

        private void UpdateSummary(EdgeContact contact, string prefix, string badge)
        {
            SummaryBadgeText.Text = badge;
            if (contact == null) { SummaryText.Text = "Sem contato"; SummaryDetail.Text = string.Empty; return; }
            var remoteLabel = _remote.Count > 1 ? _remoteName + " (" + MonitorName(contact.Second, true) + ")" : _remoteName;
            SummaryText.Text = prefix + MonitorName(contact.First, false) + " · " + MainWindow.EdgeLabel(contact.FirstEdge) + "  ↔  " + remoteLabel + " · " + MainWindow.EdgeLabel(contact.SecondEdge);
            SummaryDetail.Text = _remoteKnown
                ? "Borda inteira ativa: " + (contact.FirstEnd - contact.FirstStart) + " px neste PC, " + (contact.SecondEnd - contact.SecondStart) + " px no outro"
                : "Os monitores reais aparecem ao conectar, com o outro PC na versão 0.7.0 ou mais nova.";
        }

        private void Tile_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            BeginDrag((bool)((FrameworkElement)sender).Tag, e.GetPosition(Stage));
            Stage.CaptureMouse();
            e.Handled = true;
        }

        private void Stage_MouseMove(object sender, MouseEventArgs e) { if (_dragging) DragTo(e.GetPosition(Stage)); }
        private void Stage_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) { if (_dragging) Stage.ReleaseMouseCapture(); }
        // Reached by a normal release and by anything else that takes the capture away, so a drag never gets stuck.
        private void Stage_LostMouseCapture(object sender, MouseEventArgs e) { EndDrag(); }

        private void BeginDrag(bool remote, Point point)
        {
            _dragging = true;
            _draggingRemote = remote;
            _grab = point;
            _grabX = remote ? _remoteX : _localX;
            _grabY = remote ? _remoteY : _localY;
            Draw();
        }

        private void DragTo(Point point)
        {
            var x = _grabX + (int)((point.X - _grab.X) / _scale);
            var y = _grabY + (int)((point.Y - _grab.Y) / _scale);
            if (_draggingRemote) { _remoteX = x; _remoteY = y; } else { _localX = x; _localY = y; }
            Draw();
        }

        private void EndDrag()
        {
            if (!_dragging) return;
            if (_draggingRemote) { _remoteX = _snap.X; _remoteY = _snap.Y; } else { _localX = _snap.X; _localY = _snap.Y; }
            _dragging = false;
            if (_pendingRemote != null) { var pending = _pendingRemote; _pendingRemote = null; UpdateRemoteMonitors(pending); return; }
            Center();
            Draw();
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            var best = EdgeLayout.Best(EdgeLayout.Contacts(_local, _localX, _localY, _remote, _remoteX, _remoteY));
            if (best == null) return;
            Saved?.Invoke(new EdgeLayoutResult
            {
                LocalEdge = best.FirstEdge, RemoteEdge = best.SecondEdge, LocalMonitor = best.First, RemoteMonitor = best.Second,
                OffsetX = _remoteX - _localX, OffsetY = _remoteY - _localY, ConnectEnabled = ConnectCheckBox.IsChecked == true
            });
            Close();
        }

        private void Chrome_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); }
        private void Close_Click(object sender, RoutedEventArgs e) { Close(); }
    }
}
