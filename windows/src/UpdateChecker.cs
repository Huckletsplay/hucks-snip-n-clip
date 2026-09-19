using System;
using System.IO;
using System.Net;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;

namespace QSnipAndClip
{
    [DataContract]
    internal sealed class GitHubRelease
    {
        [DataMember(Name = "tag_name")]
        public string TagName { get; set; }

        [DataMember(Name = "html_url")]
        public string HtmlUrl { get; set; }

        [DataMember(Name = "assets")]
        public GitHubReleaseAsset[] Assets { get; set; }
    }

    [DataContract]
    internal sealed class GitHubReleaseAsset
    {
        [DataMember(Name = "name")]
        public string Name { get; set; }

        [DataMember(Name = "browser_download_url")]
        public string DownloadUrl { get; set; }
    }

    internal sealed class UpdateCheckResult
    {
        public Version CurrentVersion { get; set; }
        public Version LatestVersion { get; set; }
        public string ReleasePageUrl { get; set; }
        public string InstallerUrl { get; set; }
        public string ChecksumUrl { get; set; }
        public string InstallerName { get; set; }

        public bool UpdateAvailable
        {
            get { return LatestVersion != null && CurrentVersion != null && LatestVersion.CompareTo(CurrentVersion) > 0; }
        }
    }

    internal static class UpdateChecker
    {
        internal const string LatestReleaseApiUrl =
            "https://api.github.com/repos/Huckletsplay/hucks-snip-n-clip/releases/latest";
        private const string InstallerSuffix = "-windows-x64-setup.exe";

        internal static UpdateCheckResult Check(Version currentVersion)
        {
            GitHubRelease release = DownloadRelease();
            Version latest = ParseVersion(release.TagName);
            UpdateCheckResult result = new UpdateCheckResult
            {
                CurrentVersion = currentVersion,
                LatestVersion = latest,
                ReleasePageUrl = release.HtmlUrl
            };

            foreach (GitHubReleaseAsset asset in release.Assets ?? new GitHubReleaseAsset[0])
            {
                if (asset == null || String.IsNullOrWhiteSpace(asset.Name)) continue;
                if (asset.Name.EndsWith(InstallerSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    result.InstallerName = asset.Name;
                    result.InstallerUrl = asset.DownloadUrl;
                }
            }

            if (!String.IsNullOrEmpty(result.InstallerName))
            {
                string checksumName = result.InstallerName + ".sha256";
                foreach (GitHubReleaseAsset asset in release.Assets ?? new GitHubReleaseAsset[0])
                    if (asset != null && String.Equals(asset.Name, checksumName, StringComparison.OrdinalIgnoreCase))
                        result.ChecksumUrl = asset.DownloadUrl;
            }

            return result;
        }

        internal static string DownloadAndVerify(UpdateCheckResult update)
        {
            if (update == null || String.IsNullOrWhiteSpace(update.InstallerUrl)
                || String.IsNullOrWhiteSpace(update.ChecksumUrl))
                throw new InvalidOperationException("This release does not include a verified Windows installer.");

            string directory = Path.Combine(Path.GetTempPath(), "HucksSnipNClip-Update");
            Directory.CreateDirectory(directory);
            string installerPath = Path.Combine(directory, Path.GetFileName(update.InstallerName));
            string checksumText;
            using (WebClient client = CreateClient())
            {
                checksumText = client.DownloadString(update.ChecksumUrl);
                client.DownloadFile(update.InstallerUrl, installerPath);
            }

            string expected = ParseChecksum(checksumText);
            string actual;
            using (SHA256 sha = SHA256.Create())
            using (FileStream input = File.OpenRead(installerPath))
                actual = ToHex(sha.ComputeHash(input));

            if (!String.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(installerPath); } catch { }
                throw new InvalidDataException("The downloaded installer did not match its published SHA-256 checksum.");
            }

            return installerPath;
        }

        internal static Version ParseVersion(string tag)
        {
            if (String.IsNullOrWhiteSpace(tag)) throw new InvalidDataException("The release has no version tag.");
            string value = tag.Trim();
            if (value.StartsWith("v", StringComparison.OrdinalIgnoreCase)) value = value.Substring(1);
            Version version;
            if (!Version.TryParse(value, out version))
                throw new InvalidDataException("The release version is not recognized: " + tag);
            return version;
        }

        internal static string ParseChecksum(string text)
        {
            if (String.IsNullOrWhiteSpace(text)) throw new InvalidDataException("The checksum file is empty.");
            string token = text.Trim().Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)[0];
            if (token.Length != 64) throw new InvalidDataException("The published SHA-256 checksum is malformed.");
            foreach (char character in token)
                if (!Uri.IsHexDigit(character)) throw new InvalidDataException("The published SHA-256 checksum is malformed.");
            return token.ToLowerInvariant();
        }

        private static GitHubRelease DownloadRelease()
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            byte[] json;
            using (WebClient client = CreateClient()) json = client.DownloadData(LatestReleaseApiUrl);
            using (MemoryStream stream = new MemoryStream(json))
            {
                DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(GitHubRelease));
                GitHubRelease release = serializer.ReadObject(stream) as GitHubRelease;
                if (release == null) throw new InvalidDataException("GitHub returned an unreadable release response.");
                return release;
            }
        }

        private static WebClient CreateClient()
        {
            WebClient client = new WebClient();
            client.Headers[HttpRequestHeader.UserAgent] = "HucksSnipNClip-Windows-Updater";
            client.Headers[HttpRequestHeader.Accept] = "application/vnd.github+json";
            return client;
        }

        private static string ToHex(byte[] bytes)
        {
            StringBuilder result = new StringBuilder(bytes.Length * 2);
            foreach (byte value in bytes) result.Append(value.ToString("x2"));
            return result.ToString();
        }
    }
}
