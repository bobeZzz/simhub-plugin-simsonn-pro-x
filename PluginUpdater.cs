using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using Newtonsoft.Json.Linq;

namespace SimsonnPedals
{
    public sealed class ReleaseInfo
    {
        public Version Version { get; set; }
        public string Tag { get; set; }
        public string Name { get; set; }
        public string Notes { get; set; }
        public string PageUrl { get; set; }
        public string DownloadUrl { get; set; }
        /// <summary>Lower-case hex SHA-256 GitHub reports for the asset, or null on older releases.</summary>
        public string Sha256 { get; set; }
    }

    /// <summary>
    /// Updates the plugin from the GitHub releases of its repository. The running DLL can't be overwritten, but
    /// Windows allows renaming it, so the new file takes its place and SimHub loads it on the next start.
    /// </summary>
    public static class PluginUpdater
    {
        public const string Repository = "bobeZzz/simhub-plugin-simsonn-pro-x";
        public const string AssetName = "SimsonnPedals.dll";
        private const string OldSuffix = ".old-";
        private const string NewSuffix = ".new";

        public static string RepositoryUrl => "https://github.com/" + Repository;

        public static Version CurrentVersion => Normalize(typeof(PluginUpdater).Assembly.GetName().Version);

        /// <summary>The DLL SimHub loads at startup.</summary>
        public static string PluginPath
        {
            get
            {
                // Prefer SimHub's folder over Assembly.Location, which points elsewhere if SimHub ever shadow-copies plugins.
                var inSimHub = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, AssetName);
                return File.Exists(inSimHub) ? inSimHub : typeof(PluginUpdater).Assembly.Location;
            }
        }

        /// <summary>Newest published release, or null if the repository has none.</summary>
        public static ReleaseInfo GetLatestRelease()
        {
            string json;
            try
            {
                json = CreateClient().DownloadString($"https://api.github.com/repos/{Repository}/releases/latest");
            }
            catch (WebException ex) when ((ex.Response as HttpWebResponse)?.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            var release = JObject.Parse(json);
            var tag = (string)release["tag_name"] ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version))
                throw new InvalidDataException($"Release tag \"{tag}\" is not a version number.");

            var asset = (release["assets"] as JArray)?
                .FirstOrDefault(a => string.Equals((string)a["name"], AssetName, StringComparison.OrdinalIgnoreCase));
            var digest = (string)asset?["digest"];
            return new ReleaseInfo
            {
                Version = Normalize(version),
                Tag = tag,
                Name = (string)release["name"],
                Notes = (string)release["body"],
                PageUrl = (string)release["html_url"],
                DownloadUrl = (string)asset?["browser_download_url"],
                Sha256 = digest != null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                    ? digest.Substring(7).ToLowerInvariant()
                    : null,
            };
        }

        /// <summary>
        /// Downloads <paramref name="release"/>, checks it and puts it in place of the plugin DLL.
        /// Takes effect when SimHub restarts. Asks for administrator rights only if SimHub's folder isn't writable.
        /// </summary>
        public static void Install(ReleaseInfo release, string pluginPath = null)
        {
            if (release?.DownloadUrl == null) throw new InvalidOperationException($"The release has no {AssetName} attached.");
            pluginPath = pluginPath ?? PluginPath;

            var data = CreateClient(api: false).DownloadData(release.DownloadUrl);
            if (release.Sha256 != null)
            {
                string actual;
                using (var sha = SHA256.Create())
                    actual = string.Concat(sha.ComputeHash(data).Select(b => b.ToString("x2")));
                if (actual != release.Sha256)
                    throw new InvalidDataException("The download is corrupted (checksum mismatch). Nothing was changed.");
            }

            // Check it from a temporary file before it goes anywhere near SimHub's folder.
            var staged = Path.Combine(Path.GetTempPath(), $"SimsonnPedals-{release.Version}.dll");
            File.WriteAllBytes(staged, data);
            try
            {
                AssemblyName name;
                try
                {
                    name = AssemblyName.GetAssemblyName(staged);
                }
                catch (BadImageFormatException)
                {
                    throw new InvalidDataException("The download is not a plugin DLL. Nothing was changed.");
                }
                if (!string.Equals(name.Name, "SimsonnPedals", StringComparison.OrdinalIgnoreCase) || Normalize(name.Version) != release.Version)
                    throw new InvalidDataException($"The download is {name.Name} {name.Version}, not SimsonnPedals {release.Version}. Nothing was changed.");

                try
                {
                    Swap(staged, pluginPath);
                }
                catch (UnauthorizedAccessException)
                {
                    SwapElevated(staged, pluginPath);
                }
            }
            finally
            {
                TryDelete(staged);
            }
            SimHub.Logging.Current.Info($"[SimsonnPedals] Installed {release.Version}; it loads when SimHub restarts");
        }

        /// <summary>Removes the previous DLLs left behind by updates. They are only locked until SimHub restarts.</summary>
        public static void CleanUpOldFiles(string pluginPath = null)
        {
            pluginPath = pluginPath ?? PluginPath;
            try
            {
                var dir = Path.GetDirectoryName(pluginPath);
                var name = Path.GetFileName(pluginPath);
                foreach (var file in Directory.GetFiles(dir, name + OldSuffix + "*").Concat(Directory.GetFiles(dir, name + NewSuffix)))
                    TryDelete(file);
            }
            catch (Exception)
            {
                // Leftovers are harmless; try again next start.
            }
        }

        /// <summary>Asks SimHub to restart itself, as its own safe-mode restart does. False if this SimHub can't.</summary>
        public static bool TryRestartSimHub()
        {
            // MainWindow.Exit(restart, isSystemShutdown, restartArguments) is internal to SimHub, so look for it
            // rather than depend on it; if a SimHub update renames it, the user restarts by hand instead.
            var window = Application.Current?.MainWindow;
            var exit = window?.GetType().GetMethod("Exit", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
                null, new[] { typeof(bool), typeof(bool), typeof(string) }, null);
            if (exit == null) return false;
            try
            {
                exit.Invoke(window, new object[] { true, false, "show" });
                return true;
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Error("[SimsonnPedals] Could not restart SimHub", ex);
                return false;
            }
        }

        private static void Swap(string staged, string pluginPath)
        {
            var next = pluginPath + NewSuffix;
            File.Copy(staged, next, true);
            var old = pluginPath + OldSuffix + DateTime.Now.Ticks;
            // Renaming works even while SimHub has the DLL loaded; overwriting it does not.
            File.Move(pluginPath, old);
            try
            {
                File.Move(next, pluginPath);
            }
            catch
            {
                File.Move(old, pluginPath);
                throw;
            }
        }

        private static void SwapElevated(string staged, string pluginPath)
        {
            string Quote(string s) => "'" + s.Replace("'", "''") + "'";
            var old = pluginPath + OldSuffix + DateTime.Now.Ticks;
            var script = "$ErrorActionPreference = 'Stop'; " +
                $"Move-Item -LiteralPath {Quote(pluginPath)} -Destination {Quote(old)}; " +
                $"try {{ Copy-Item -LiteralPath {Quote(staged)} -Destination {Quote(pluginPath)} }} " +
                $"catch {{ Move-Item -LiteralPath {Quote(old)} -Destination {Quote(pluginPath)}; exit 1 }}";
            var info = new ProcessStartInfo("powershell.exe",
                "-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)))
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            };

            Process process;
            try
            {
                process = Process.Start(info);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                throw new UnauthorizedAccessException("SimHub's folder needs administrator rights to update, and the request was declined.");
            }
            process.WaitForExit();
            if (process.ExitCode != 0)
                throw new IOException("Could not replace the plugin DLL. Nothing was changed.");
        }

        private static WebClient CreateClient(bool api = true)
        {
            // SimHub may target a framework version that defaults to TLS 1.0; GitHub needs 1.2 or later.
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var client = new WebClient();
            client.Headers[HttpRequestHeader.UserAgent] = $"SimsonnPedals/{CurrentVersion}";
            if (api) client.Headers[HttpRequestHeader.Accept] = "application/vnd.github+json";
            return client;
        }

        private static Version Normalize(Version v) => new Version(v.Major, v.Minor, Math.Max(v.Build, 0));

        private static void TryDelete(string path)
        {
            try { File.Delete(path); }
            catch (Exception) { /* still locked or already gone */ }
        }
    }
}
