using Tartarus.Vision;

namespace Tartarus.Capture;

/// <summary>A way of getting the game's picture: <see cref="WindowCapture"/>, or where Windows refuses that, <see cref="ScreenCopyCapture"/>.</summary>
public interface IGameCapture : IDisposable
{
    IntPtr Hwnd { get; }

    /// <summary>Frames hold the window's client area only.</summary>
    bool CropToClient { get; }

    /// <summary>Where the last grabbed frame sits on screen (physical pixels).</summary>
    PixelRect LastBounds { get; }

    /// <summary>Raised when the captured window closes, on no particular thread.</summary>
    event Action? Closed;

    /// <summary>What's unusual about the frames, each thing once; raised on the thread that grabs them.</summary>
    event Action<string>? Note;

    /// <summary>The latest picture, null if there's none now. Off the UI thread: it may wait on the graphics card.</summary>
    Frame? Grab(PixelPool? pool = null);
}
