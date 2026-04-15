using System;
using System.Collections.Generic;

namespace PowerPointSharing
{
    public class InkStrokeData
    {
        public string StrokeId { get; set; } = string.Empty;
        public List<double[]> Points { get; set; } = new List<double[]>();
        public string Color { get; set; } = "#FF0000";
        public double Width { get; set; } = 3;
        public double Opacity { get; set; } = 1;
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    }
}
