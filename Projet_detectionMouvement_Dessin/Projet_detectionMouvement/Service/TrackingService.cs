using System.Linq;

public class TrackingService
{
    public (float x, float y) GetCenter(List<PointModel> points)
    {
        float x = points.Average(p => p.x);
        float y = points.Average(p => p.y);

        return (x, y);
    }
}