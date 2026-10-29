using System.IO;

namespace work_monitor
{
    /// <summary>
    /// Replaces a file with a completed temporary file, so readers never see a partial write.
    /// </summary>
    internal static class AtomicFile
    {
        public static void Replace(string tempPath, string destinationPath)
        {
            if (File.Exists(destinationPath))
            {
                File.Replace(tempPath, destinationPath, null);
            }
            else
            {
                File.Move(tempPath, destinationPath);
            }
        }
    }
}
