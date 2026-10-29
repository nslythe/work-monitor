using System;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace work_monitor
{
    /// <summary>
    /// Provides the window icon that matches the current work state. The window icon is also
    /// the taskbar icon, so switching it makes the state visible outside the window.
    /// </summary>
    internal static class AppIcons
    {
        private const string WorkingResource = "pack://application:,,,/app.ico";
        private const string NotWorkingResource = "pack://application:,,,/app.notworking.ico";
        private const int WindowIconSize = 32;

        private static ImageSource? _workingWindowIcon;
        private static ImageSource? _notWorkingWindowIcon;

        /// <summary>Returns the cached window icon for <paramref name="state"/>.</summary>
        public static ImageSource GetWindowIcon(WorkState state)
        {
            return state == WorkState.Working
                ? _workingWindowIcon ??= LoadWindowIcon(WorkingResource)
                : _notWorkingWindowIcon ??= LoadWindowIcon(NotWorkingResource);
        }

        private static ImageSource LoadWindowIcon(string resourceUri)
        {
            var decoder = new IconBitmapDecoder(
                new Uri(resourceUri, UriKind.Absolute),
                BitmapCreateOptions.None,
                BitmapCacheOption.OnLoad);

            // Pick the frame closest to the taskbar icon size; Windows scales it as needed for
            // the title bar and Alt+Tab.
            BitmapFrame frame = decoder.Frames
                .OrderBy(candidate => Math.Abs(candidate.PixelWidth - WindowIconSize))
                .First();

            frame.Freeze();
            return frame;
        }
    }
}
