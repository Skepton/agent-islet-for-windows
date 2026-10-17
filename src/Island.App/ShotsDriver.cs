using System.IO;
using System.Windows.Media;
using Island.Core;

namespace Island.App;

/// <summary>
/// <c>--shots &lt;folder&gt;</c>: draws each island view (the usage strip, the thin bar, every card) into a PNG using the
/// demo data, then exits. The island is kept off every screen and drawn straight to a bitmap, so the pictures never
/// contain the user's desktop and can be remade whenever the look changes (<c>scripts/make-screenshots.ps1</c>).
/// </summary>
public sealed class ShotsDriver
{
    private static readonly Color Backdrop = Color.FromRgb(70, 76, 90);

    private readonly OverlayController _overlay;
    private readonly DemoDriver _demo;
    private readonly string _folder;

    public ShotsDriver(OverlayController overlay, DemoDriver demo, string folder)
    {
        _overlay = overlay;
        _demo = demo;
        _folder = folder;
    }

    /// <returns>The names of the files written.</returns>
    public async Task<IReadOnlyList<string>> RunAsync()
    {
        Directory.CreateDirectory(_folder);
        var written = new List<string>();

        async Task Shoot(string name, Action? scene)
        {
            _overlay.Reset();
            scene?.Invoke();
            await Task.Delay(700); // let the card view-model and its layout settle
            _overlay.Capture(Path.Combine(_folder, name + ".png"), Backdrop);
            written.Add(name + ".png");
        }

        // The first reading opens the numbers for a few seconds: that is the strip. Once it has closed again, only the bar is left.
        _overlay.OnUsage(DemoDriver.UsageUpdateFor(fiveHour: 28, sevenDay: 55));
        await Task.Delay(700);
        _overlay.Capture(Path.Combine(_folder, "usage-strip.png"), Backdrop);
        written.Add("usage-strip.png");

        await Task.Delay(UsageReveal.AfterChange + TimeSpan.FromSeconds(1.5));
        _overlay.Capture(Path.Combine(_folder, "usage-bar.png"), Backdrop);
        written.Add("usage-bar.png");

        await Shoot("permission-card", _demo.Permission);
        await Shoot("question-card", _demo.NeedsInput);
        await Shoot("finished-card", _demo.Finished);
        await Shoot("new-project-card", _demo.NewProject);
        await Shoot("usage-milestone-card", _demo.Milestone35);
        await Shoot("usage-limit-card", _demo.Milestone90);
        await Shoot("several-sessions", _demo.ThreeSessions);
        return written;
    }
}
