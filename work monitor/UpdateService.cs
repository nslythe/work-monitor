using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace work_monitor
{
    /// <summary>
    /// A published release that is newer than the running application, including the
    /// installer asset the update dialog offers to download.
    /// </summary>
    internal sealed class UpdateInfo
    {
        public UpdateInfo(
            Version version,
            string versionText,
            string releaseName,
            string releaseNotes,
            string releasePageUrl,
            string? downloadUrl,
            string? assetName,
            string? assetDigest)
        {
            Version = version;
            VersionText = versionText;
            ReleaseName = releaseName;
            ReleaseNotes = releaseNotes;
            ReleasePageUrl = releasePageUrl;
            DownloadUrl = downloadUrl;
            AssetName = assetName;
            AssetDigest = assetDigest;
        }

        public Version Version { get; }

        public string VersionText { get; }

        public string ReleaseName { get; }

        public string ReleaseNotes { get; }

        public string ReleasePageUrl { get; }

        public string? DownloadUrl { get; }

        public string? AssetName { get; }

        /// <summary>GitHub's "sha256:..." digest for the installer, when the release provides one.</summary>
        public string? AssetDigest { get; }

        public bool HasInstaller => !string.IsNullOrEmpty(DownloadUrl);
    }

    /// <summary>
    /// Checks the public GitHub releases of Work Monitor and downloads the MSI of a newer
    /// release. Only the latest published (non-draft, non-prerelease) release is considered.
    /// </summary>
    internal static class UpdateService
    {
        private const string RepositoryOwner = "nslythe";
        private const string RepositoryName = "work-monitor";

        /// <summary>The public GitHub page of the Work Monitor project.</summary>
        public const string RepositoryUrl = $"https://github.com/{RepositoryOwner}/{RepositoryName}";

        private static readonly Uri LatestReleaseUri =
            new($"https://api.github.com/repos/{RepositoryOwner}/{RepositoryName}/releases/latest");

        private static readonly Uri ReleasesPageUri = new($"{RepositoryUrl}/releases");

        private static readonly Lazy<Version> LazyCurrentVersion = new(() =>
            NormalizeVersion(Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0)));

        private static readonly HttpClient Http = CreateHttpClient();

        public static Version CurrentVersion => LazyCurrentVersion.Value;

        public static string CurrentVersionText => FormatVersion(CurrentVersion);

        private static HttpClient CreateHttpClient()
        {
            // .NET Framework does not always negotiate TLS 1.2 with GitHub on older builds.
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd($"WorkMonitor/{CurrentVersionText}");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

            return client;
        }

        /// <summary>
        /// Returns the latest published release, or <c>null</c> when the repository has no
        /// published releases yet. Throws for network, API and parsing failures.
        /// </summary>
        public static async Task<UpdateInfo?> GetLatestReleaseAsync(CancellationToken cancellationToken)
        {
            using (var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUri))
            using (HttpResponseMessage response = await Http
                       .SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
                       .ConfigureAwait(false))
            {
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    // The "latest" endpoint answers 404 until a release is published.
                    return null;
                }

                if (!response.IsSuccessStatusCode)
                {
                    string reason = string.IsNullOrWhiteSpace(response.ReasonPhrase)
                        ? "request failed"
                        : response.ReasonPhrase!;
                    throw new HttpRequestException($"GitHub returned {(int)response.StatusCode} ({reason}).");
                }

                string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                return ParseRelease(json);
            }
        }

        private static UpdateInfo ParseRelease(string json)
        {
            JObject release = JObject.Parse(json);

            string? tag = release.Value<string>("tag_name");
            if (!TryParseVersion(tag, out Version version))
            {
                throw new JsonException($"The latest release has no valid version tag ('{tag}').");
            }

            string name = release.Value<string>("name") ?? tag ?? "";
            string notes = release.Value<string>("body") ?? "";
            string pageUrl = release.Value<string>("html_url") ?? ReleasesPageUri.ToString();
            string versionText = FormatVersion(version);

            JToken? asset = SelectInstallerAsset(release["assets"] as JArray);

            // Older releases may contain an installer built from a different version. Installing
            // that would be a no-op, so treat it as if the release had no installer.
            string? assetName = asset?.Value<string>("name");
            if (asset is not null &&
                assetName is not null &&
                assetName.IndexOf(versionText, StringComparison.OrdinalIgnoreCase) < 0)
            {
                asset = null;
            }

            return new UpdateInfo(
                version,
                versionText,
                name,
                notes,
                pageUrl,
                asset?.Value<string>("browser_download_url"),
                asset?.Value<string>("name"),
                asset?.Value<string>("digest"));
        }

        private static JToken? SelectInstallerAsset(JArray? assets)
        {
            if (assets is null)
            {
                return null;
            }

            JToken? fallback = null;

            foreach (JToken asset in assets)
            {
                string? name = asset.Value<string>("name");
                if (name is null || !name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (name.IndexOf("x64", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return asset;
                }

                fallback ??= asset;
            }

            return fallback;
        }

        public static bool IsNewer(Version candidate, Version current)
        {
            return NormalizeVersion(candidate) > NormalizeVersion(current);
        }

        /// <summary>
        /// Accepts tags such as "1.7.0", "v1.7.0" and "v1.7.0-beta.1" and returns the numeric
        /// version with a build component of at least zero.
        /// </summary>
        internal static bool TryParseVersion(string? text, out Version version)
        {
            version = new Version(0, 0, 0);

            if (text is null || string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            string value = text.Trim();

            if (value.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            {
                value = value.Substring(1);
            }

            int suffixIndex = value.IndexOfAny(new[] { '-', '+' });
            if (suffixIndex >= 0)
            {
                value = value.Substring(0, suffixIndex);
            }

            if (!Version.TryParse(value, out Version? parsed))
            {
                return false;
            }

            version = NormalizeVersion(parsed);
            return true;
        }

        internal static Version NormalizeVersion(Version version)
        {
            return new Version(version.Major, version.Minor, version.Build < 0 ? 0 : version.Build);
        }

        internal static string FormatVersion(Version version)
        {
            Version normalized = NormalizeVersion(version);
            return $"{normalized.Major}.{normalized.Minor}.{normalized.Build}";
        }

        /// <summary>
        /// Downloads the installer of a release to the temporary folder, reports progress from
        /// 0 to 1 and verifies the SHA-256 digest when GitHub publishes one.
        /// </summary>
        public static async Task<string> DownloadInstallerAsync(
            UpdateInfo update,
            IProgress<double>? progress,
            CancellationToken cancellationToken)
        {
            if (update is null)
            {
                throw new ArgumentNullException(nameof(update));
            }

            if (!update.HasInstaller)
            {
                throw new InvalidOperationException("The release does not include an installer.");
            }

            string path = Path.Combine(Path.GetTempPath(), $"WorkMonitor-Update-{update.VersionText}.msi");

            if (File.Exists(path))
            {
                File.Delete(path);
            }

            using (HttpResponseMessage response = await Http
                       .GetAsync(update.DownloadUrl!, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                       .ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();

                long? totalBytes = response.Content.Headers.ContentLength;

                using (Stream input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                {
                    byte[] buffer = new byte[81920];
                    long receivedBytes = 0;

                    while (true)
                    {
                        int read = await input.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
                        if (read == 0)
                        {
                            break;
                        }

                        await output.WriteAsync(buffer, 0, read, cancellationToken).ConfigureAwait(false);
                        receivedBytes += read;

                        if (totalBytes is > 0)
                        {
                            progress?.Report((double)receivedBytes / totalBytes.Value);
                        }
                    }
                }
            }

            VerifyDigest(path, update.AssetDigest);
            progress?.Report(1);

            return path;
        }

        private static void VerifyDigest(string path, string? digest)
        {
            if (digest is null || string.IsNullOrWhiteSpace(digest))
            {
                return;
            }

            int separator = digest.IndexOf(':');
            string algorithm = separator > 0 ? digest.Substring(0, separator) : "";
            string expected = separator > 0 ? digest.Substring(separator + 1) : digest;

            if (!string.Equals(algorithm, "sha256", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(expected))
            {
                return;
            }

            string actual;

            using (SHA256 sha256 = SHA256.Create())
            using (FileStream stream = File.OpenRead(path))
            {
                actual = ToHex(sha256.ComputeHash(stream));
            }

            if (!string.Equals(actual, expected.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    File.Delete(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // The checksum error below matters more than removing the bad file.
                }

                throw new InvalidDataException(
                    "The downloaded installer does not match the checksum published with the release.");
            }
        }

        private static string ToHex(byte[] bytes)
        {
            var builder = new StringBuilder(bytes.Length * 2);

            foreach (byte value in bytes)
            {
                builder.Append(value.ToString("x2", CultureInfo.InvariantCulture));
            }

            return builder.ToString();
        }

        /// <summary>Starts the downloaded MSI with the standard Windows Installer UI.</summary>
        public static bool TryLaunchInstaller(string path, out string? error)
        {
            try
            {
                if (!File.Exists(path))
                {
                    error = "The downloaded installer could not be found.";
                    return false;
                }

                Process.Start(new ProcessStartInfo
                {
                    FileName = "msiexec.exe",
                    Arguments = $"/i \"{path}\"",
                    UseShellExecute = true
                });

                error = null;
                return true;
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>Opens a URL in the default browser.</summary>
        public static void OpenUrl(string url)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(url))
                {
                    Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
                }
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
            {
                // Opening the page is a convenience; the dialog stays usable if it fails.
            }
        }
    }
}
