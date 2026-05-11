using System.IO;
using System.Windows.Media.Animation;
using Intel.RealSense;

public class RealSenseService
{
    private Pipeline pipe;

    public void Start()
    {
        pipe = new Pipeline();
        var cfg = new Config();
        cfg.EnableStream(Intel.RealSense.Stream.Color, 640, 480);
        pipe.Start(cfg);
    }

    public VideoFrame GetFrame()
    {
        var frames = pipe.WaitForFrames();
        return frames.ColorFrame;
    }
}