using System;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace WinputLan.Controls
{
    // Draws a Lucide outline icon (24×24 stroke geometry from App.xaml) scaled to the element size, stroked with the inherited foreground.
    public sealed class Icon : FrameworkElement
    {
        public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
            "Data", typeof(Geometry), typeof(Icon), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
            typeof(Icon), new FrameworkPropertyMetadata(SystemColors.ControlTextBrush, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

        // In icon units (the 24-unit Lucide grid), so it scales with the icon like the library's stroke-width.
        public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
            "StrokeThickness", typeof(double), typeof(Icon), new FrameworkPropertyMetadata(2.0, FrameworkPropertyMetadataOptions.AffectsRender));

        static Icon()
        {
            WidthProperty.OverrideMetadata(typeof(Icon), new FrameworkPropertyMetadata(16.0));
            HeightProperty.OverrideMetadata(typeof(Icon), new FrameworkPropertyMetadata(16.0));
            FocusableProperty.OverrideMetadata(typeof(Icon), new FrameworkPropertyMetadata(false));
            SnapsToDevicePixelsProperty.OverrideMetadata(typeof(Icon), new FrameworkPropertyMetadata(false));
        }

        public Geometry Data { get { return (Geometry)GetValue(DataProperty); } set { SetValue(DataProperty, value); } }
        public Brush Foreground { get { return (Brush)GetValue(ForegroundProperty); } set { SetValue(ForegroundProperty, value); } }
        public double StrokeThickness { get { return (double)GetValue(StrokeThicknessProperty); } set { SetValue(StrokeThicknessProperty, value); } }

        protected override void OnRender(DrawingContext dc)
        {
            if (Data == null || Foreground == null) return;
            var scale = Math.Min(ActualWidth, ActualHeight) / 24.0;
            if (scale <= 0) return;
            var pen = new Pen(Foreground, StrokeThickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            dc.PushTransform(new MatrixTransform(scale, 0, 0, scale, (ActualWidth - 24 * scale) / 2, (ActualHeight - 24 * scale) / 2));
            dc.DrawGeometry(null, pen, Data);
            dc.Pop();
        }
    }
}
