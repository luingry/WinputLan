using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WinputLan.Core;

namespace WinputLan.Controls
{
    // One box per code symbol. Editing rules live in AccessCodeMask; this control only renders slots and moves focus.
    public sealed class AccessCodeInput : UserControl
    {
        private readonly AccessCodeMask _mask = new AccessCodeMask();
        private readonly TextBox[] _boxes = new TextBox[AccessCode.Length];

        public AccessCodeInput()
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            for (var i = 0; i < _boxes.Length; i++)
            {
                if (i == AccessCode.Length / 2) panel.Children.Add(new TextBlock { Text = "–", FontSize = 24, Margin = new Thickness(6, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)Application.Current.FindResource("MutedBrush") });
                var index = i;
                var box = new TextBox
                {
                    Width = 50, Height = 58, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(0),
                    FontSize = 26, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center,
                    VerticalContentAlignment = VerticalAlignment.Center, CharacterCasing = CharacterCasing.Upper,
                    IsUndoEnabled = false, ContextMenu = null
                };
                AutomationProperties.SetName(box, "Caractere " + (i + 1) + " do código de acesso");
                InputMethod.SetIsInputMethodEnabled(box, false);
                box.PreviewTextInput += (s, e) => { e.Handled = true; Apply(_mask.Input(index, e.Text)); };
                box.PreviewKeyDown += (s, e) => OnKey(index, e);
                box.GotKeyboardFocus += (s, e) => box.SelectAll();
                box.PreviewMouseLeftButtonDown += (s, e) => { if (!box.IsKeyboardFocusWithin) { e.Handled = true; box.Focus(); } };
                DataObject.AddPastingHandler(box, (s, e) =>
                {
                    e.CancelCommand();
                    var text = e.DataObject.GetDataPresent(DataFormats.UnicodeText) ? (string)e.DataObject.GetData(DataFormats.UnicodeText) : null;
                    Apply(_mask.Input(index, text));
                });
                _boxes[i] = box;
                panel.Children.Add(box);
            }
            Content = panel;
        }

        public event EventHandler Submitted;

        public string Code { get { return _mask.Value; } }
        public bool IsComplete { get { return _mask.IsComplete; } }

        public void Clear() { _mask.Clear(); Render(); }

        public void FocusFirstEmpty() { _boxes[_mask.FirstEmpty()].Focus(); }

        private void OnKey(int index, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Back: e.Handled = true; Apply(_mask.Backspace(index)); break;
                case Key.Delete: e.Handled = true; Apply(_mask.Delete(index)); break;
                case Key.Left: e.Handled = true; Focus(index - 1); break;
                case Key.Right: e.Handled = true; Focus(index + 1); break;
                case Key.Home: e.Handled = true; Focus(0); break;
                case Key.End: e.Handled = true; Focus(_boxes.Length - 1); break;
                case Key.Space: e.Handled = true; break;
                case Key.Enter: if (_mask.IsComplete) { e.Handled = true; Submitted?.Invoke(this, EventArgs.Empty); } break;
            }
        }

        private void Apply(int focusIndex) { Render(); Focus(focusIndex); }

        private void Render()
        {
            for (var i = 0; i < _boxes.Length; i++)
            {
                var text = _mask[i].HasValue ? _mask[i].Value.ToString() : string.Empty;
                if (_boxes[i].Text != text) _boxes[i].Text = text;
            }
        }

        private void Focus(int index)
        {
            index = Math.Max(0, Math.Min(_boxes.Length - 1, index));
            _boxes[index].Focus();
            _boxes[index].SelectAll();
        }
    }
}
