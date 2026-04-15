using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace PowerPointSharing
{
    public class OverlayStroke
    {
        public List<Point> NormalizedPoints { get; set; } = new List<Point>();
        public Color Color { get; set; } = Colors.Red;
        public double Thickness { get; set; } = 3;
    }
}
