using CabinetNC.Domain.Geometry;
using CabinetNC.Domain.Parts;
var cases = new (string, double, Point2, Point2)[] {
  ("mid", 15.5, new(7.75, 83.333), new(7.75, 166.667)),
  ("full", 15.5, new(7.75, 0), new(7.75, 250)),
  ("shortH", 15.5, new(0, 125), new(15.5, 125)),
};
foreach (var (name, w, a, b) in cases) {
  var f = new PanelFeature { FeatureId=name, Kind="grooveVertical", WidthMm=w, Path=new[]{a,b} };
  var o = GrooveGeometry.DisplayOutline(f);
  Console.WriteLine($"{name}: n={o.Count} spanX={(o.Count>0?o.Max(p=>p.X)-o.Min(p=>p.X):0):0.###} spanY={(o.Count>0?o.Max(p=>p.Y)-o.Min(p=>p.Y):0):0.###}");
}
