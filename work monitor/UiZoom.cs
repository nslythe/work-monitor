using System;
using System.IO;
using System.Security;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Newtonsoft.Json;

namespace work_monitor
{
    /// <summary>
    /// Browser-style zoom shared by all windows. The scale lives in <see cref="AppSettings"/>
    /// and is applied through the application-level <c>UiScaleTransform</c>, so changing it
    /// rescales layout, text and controls together without touching each window separately.
    /// </summary>
    public static class UiZoom
    {
        // Discrete levels keep repeated presses predictable and make reset exact.
        private static readonly double[] Steps = { 0.8, 0.9, 1.0, 1.1, 1.25, 1.5, 1.75, 2.0 };

        private static AppSettings? _settings;
        private static int _stepIndex = Array.IndexOf(Steps, AppSettings.DefaultUiScale);

        /// <summary>Current zoom factor.</summary>
        public static double CurrentScale => Steps[_stepIndex];

        /// <summary>Attaches the saved settings and applies the stored zoom immediately.</summary>
        public static void Initialize(AppSettings settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _stepIndex = NearestStepIndex(settings.UiScale);
            Apply();
        }

        /// <summary>Moves one zoom step towards <paramref name="direction"/> (positive is in).</summary>
        public static void Step(int direction)
        {
            if (direction == 0)
            {
                return;
            }

            int step = direction > 0 ? 1 : -1;
            int index = Math.Max(0, Math.Min(Steps.Length - 1, _stepIndex + step));
            if (index == _stepIndex)
            {
                return;
            }

            _stepIndex = index;
            Commit();
        }

        /// <summary>Restores 100% zoom.</summary>
        public static void Reset()
        {
            int defaultIndex = Array.IndexOf(Steps, AppSettings.DefaultUiScale);
            if (_stepIndex == defaultIndex)
            {
                return;
            }

            _stepIndex = defaultIndex;
            Commit();
        }

        /// <summary>Handles Ctrl + +/-/0 from any window, including focused text boxes.</summary>
        public static void HandlePreviewKeyDown(KeyEventArgs e)
        {
            if (e.Handled || !HasControl())
            {
                return;
            }

            switch (e.Key)
            {
                case Key.OemPlus:
                case Key.Add:
                    Step(1);
                    e.Handled = true;
                    break;

                case Key.OemMinus:
                case Key.Subtract:
                    Step(-1);
                    e.Handled = true;
                    break;

                case Key.D0:
                case Key.NumPad0:
                    Reset();
                    e.Handled = true;
                    break;
            }
        }

        /// <summary>Handles Ctrl + mouse wheel from any window.</summary>
        public static void HandlePreviewMouseWheel(MouseWheelEventArgs e)
        {
            if (e.Handled || e.Delta == 0 || !HasControl())
            {
                return;
            }

            Step(e.Delta > 0 ? 1 : -1);
            e.Handled = true;
        }

        private static bool HasControl()
        {
            ModifierKeys modifiers = Keyboard.Modifiers;

            // AltGr reaches WPF as Control+Alt, so it must not be treated as a zoom shortcut.
            return (modifiers & ModifierKeys.Control) != 0 && (modifiers & ModifierKeys.Alt) == 0;
        }

        private static int NearestStepIndex(double scale)
        {
            int best = Array.IndexOf(Steps, AppSettings.DefaultUiScale);
            double bestDistance = double.MaxValue;

            for (int i = 0; i < Steps.Length; i++)
            {
                double distance = Math.Abs(Steps[i] - scale);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = i;
                }
            }

            return best;
        }

        private static void Apply()
        {
            // Freezables loaded from application resources are read-only, so replace the
            // transform with a fresh, unfrozen one. DynamicResource references in both
            // windows pick up the new instance automatically.
            Application.Current?.Resources["UiScaleTransform"] = new ScaleTransform(CurrentScale, CurrentScale);
        }

        private static void Commit()
        {
            Apply();

            if (_settings is null)
            {
                return;
            }

            _settings.UiScale = CurrentScale;

            try
            {
                SettingsStore.Save(_settings);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or SecurityException)
            {
                // Like the other settings writes, zoom is best effort and must not disrupt use.
            }
        }
    }
}
