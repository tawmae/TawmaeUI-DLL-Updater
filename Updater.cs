using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TawmaeUIUpdater
{
    // ==============================================================
    public sealed class DllFile
    {
        public DllFile(byte[] bytes, Version version)
        {
            Bytes = bytes;
            Version = version;
            Sha256 = bytes == null ? null : Hash(bytes);
        }

        public byte[] Bytes { get; }
        public Version Version { get; }
        public string Sha256 { get; }

        public static string Hash(byte[] bytes)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }
    }

    // ==============================================================
    public sealed class RunState
    {
        public RunState(int[] ids, bool visible)
        {
            Ids = ids;
            Visible = visible;
        }

        public int[] Ids { get; }
        public bool Visible { get; }
        public bool Running => Ids.Length > 0;
    }

    // ==============================================================
    public static class Updater
    {
        public const string DllName = "TawmaeUI.dll";
        public const string StreamerBotExe = "Streamer.bot.exe";
        public const string SiteUrl = "https://www.tawmae.xyz";
        public const string RepoUrl = "https://github.com/tawmae/TawmaeUI-DLL-Updater";
        public static readonly Uri Source = new Uri("https://tawmae.xyz/downloads/TawmaeUI.dll");
        public static readonly Version AppVersion = typeof(Updater).Assembly.GetName().Version;

        const int MaxBytes = 16 * 1024 * 1024;
        static readonly byte[] NameMark = Encoding.Unicode.GetBytes(DllName);
        static readonly byte[] VersionMark = { 0xBD, 0x04, 0xEF, 0xFE };
        static readonly HttpClient Http = CreateClient();

        // ==============================================================
        static HttpClient CreateClient()
        {
            var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate };
            var http = new HttpClient(handler);
            http.DefaultRequestHeaders.UserAgent.ParseAdd("TawmaeUI-DLL-Updater/" + Show(AppVersion));
            http.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue { NoCache = true };
            return http;
        }

        // ==============================================================
        public static string Show(Version version)
        {
            if (version == null)
                return "-";
            return version.Revision > 0 ? version.ToString(4) : version.ToString(3);
        }

        // ==============================================================
        public static string FindStreamerBot(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;
            try
            {
                string folder = Path.GetFullPath(path.Trim().Trim('"'));
                if (File.Exists(folder))
                    folder = Path.GetDirectoryName(folder);
                folder = TrimFolder(folder);
                if (File.Exists(Path.Combine(folder, StreamerBotExe)))
                    return folder;
                string parent = Path.GetDirectoryName(folder);
                if (parent != null
                    && string.Equals(Path.GetFileName(folder), "dlls", StringComparison.OrdinalIgnoreCase)
                    && File.Exists(Path.Combine(parent, StreamerBotExe)))
                    return TrimFolder(parent);
            }
            catch (Exception)
            {
            }
            return null;
        }

        // ==============================================================
        static string TrimFolder(string folder)
        {
            string root = Path.GetPathRoot(folder) ?? "";
            return folder.Length > root.Length ? folder.TrimEnd('\\', '/') : folder;
        }

        // ==============================================================
        public static string DllPath(string folder)
        {
            return Path.Combine(folder, "dlls", DllName);
        }

        // ==============================================================
        public static Version ReadVersion(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 1024 || bytes[0] != 'M' || bytes[1] != 'Z')
                return null;
            int pe = BitConverter.ToInt32(bytes, 0x3C);
            if (pe < 0 || pe > bytes.Length - 4 || BitConverter.ToUInt32(bytes, pe) != 0x00004550)
                return null;
            if (IndexOf(bytes, NameMark) < 0)
                return null;
            int at = IndexOf(bytes, VersionMark);
            if (at < 0 || at > bytes.Length - 16)
                return null;
            uint high = BitConverter.ToUInt32(bytes, at + 8);
            uint low = BitConverter.ToUInt32(bytes, at + 12);
            return new Version((int)(high >> 16), (int)(high & 0xFFFF), (int)(low >> 16), (int)(low & 0xFFFF));
        }

        // ==============================================================
        static int IndexOf(byte[] data, byte[] mark)
        {
            int last = data.Length - mark.Length;
            for (int i = Array.IndexOf(data, mark[0]); i >= 0 && i <= last; i = Array.IndexOf(data, mark[0], i + 1))
            {
                int j = 1;
                while (j < mark.Length && data[i + j] == mark[j])
                    j++;
                if (j == mark.Length)
                    return i;
            }
            return -1;
        }

        // ==============================================================
        public static DllFile ReadLocal(string path)
        {
            if (!File.Exists(path))
                return null;
            try
            {
                byte[] bytes = File.ReadAllBytes(path);
                return new DllFile(bytes, ReadVersion(bytes));
            }
            catch (Exception)
            {
            }
            try
            {
                var info = FileVersionInfo.GetVersionInfo(path);
                var version = new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart);
                return new DllFile(null, version == new Version(0, 0, 0, 0) ? null : version);
            }
            catch (Exception)
            {
                return new DllFile(null, null);
            }
        }

        // ==============================================================
        public static async Task<DllFile> DownloadAsync(Uri source, IProgress<double> progress, CancellationToken cancel)
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(60));
                try
                {
                    using (var response = await Http.GetAsync(source, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false))
                    using (timeout.Token.Register(response.Dispose))
                    {
                        if (!response.IsSuccessStatusCode)
                            throw new HttpRequestException("tawmae.xyz answered with error " + (int)response.StatusCode + ".");
                        long total = response.Content.Headers.ContentLength ?? -1;
                        if (total > MaxBytes)
                            throw new InvalidDataException("tawmae.xyz sent a file that is too big for a TawmaeUI.dll.");

                        using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        using (var buffer = new MemoryStream(total > 0 ? (int)total : 4 * 1024 * 1024))
                        {
                            var chunk = new byte[81920];
                            int read;
                            while ((read = await stream.ReadAsync(chunk, 0, chunk.Length, timeout.Token).ConfigureAwait(false)) > 0)
                            {
                                buffer.Write(chunk, 0, read);
                                if (buffer.Length > MaxBytes)
                                    throw new InvalidDataException("tawmae.xyz sent a file that is too big for a TawmaeUI.dll.");
                                if (total > 0)
                                    progress?.Report((double)buffer.Length / total);
                            }
                            if (total > 0 && buffer.Length != total)
                                throw new IOException("The download stopped early.");

                            byte[] bytes = buffer.ToArray();
                            var version = ReadVersion(bytes);
                            if (version == null)
                                throw new InvalidDataException("tawmae.xyz sent a file that is not a TawmaeUI.dll.");
                            return new DllFile(bytes, version);
                        }
                    }
                }
                catch (Exception) when (cancel.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cancel);
                }
                catch (Exception ex) when (timeout.IsCancellationRequested)
                {
                    throw new TimeoutException("tawmae.xyz took too long to answer.", ex);
                }
            }
        }

        // ==============================================================
        public static bool IsRunning(string folder)
        {
            return Inspect(folder).Running;
        }

        // ==============================================================
        public static RunState Inspect(string folder)
        {
            string exe = Path.Combine(folder, StreamerBotExe);
            var ids = new List<int>();
            bool visible = false;
            var processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(StreamerBotExe));
            try
            {
                foreach (var process in processes)
                {
                    string image = Native.ImagePath(process.Id, out bool gone);
                    if (gone || (image != null && !SamePath(image, exe)))
                        continue;
                    ids.Add(process.Id);
                    try
                    {
                        visible |= process.MainWindowHandle != IntPtr.Zero;
                    }
                    catch (Exception)
                    {
                    }
                }
                return new RunState(ids.ToArray(), visible);
            }
            finally
            {
                foreach (var process in processes)
                    process.Dispose();
            }
        }

        // ==============================================================
        static bool SamePath(string a, string b)
        {
            try
            {
                return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
            }
        }

        // ==============================================================
        public static void Cleanup(string folder)
        {
            string target = DllPath(folder);
            string old = target + ".old";
            TryDelete(target + ".download");
            if (!File.Exists(old))
                return;
            if (File.Exists(target))
            {
                TryDelete(old);
                return;
            }
            try
            {
                File.Move(old, target);
            }
            catch (Exception)
            {
            }
        }

        // ==============================================================
        public static void Install(string folder, DllFile file)
        {
            string dir = Path.Combine(folder, "dlls");
            string target = Path.Combine(dir, DllName);
            string temp = target + ".download";
            string old = target + ".old";

            Directory.CreateDirectory(dir);
            Cleanup(folder);
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough))
                {
                    stream.Write(file.Bytes, 0, file.Bytes.Length);
                    stream.Flush(true);
                }

                if (File.Exists(target))
                {
                    File.SetAttributes(target, File.GetAttributes(target) & ~FileAttributes.ReadOnly);
                    using (new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    {
                    }
                    File.Move(target, old);
                    try
                    {
                        File.Move(temp, target);
                    }
                    catch (Exception)
                    {
                        File.Move(old, target);
                        throw;
                    }
                }
                else
                {
                    File.Move(temp, target);
                }
            }
            finally
            {
                TryDelete(temp);
                if (File.Exists(target))
                    TryDelete(old);
            }

            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    if (DllFile.Hash(File.ReadAllBytes(target)) != file.Sha256)
                        throw new InvalidDataException("The new TawmaeUI.dll doesn't match the download. Run the updater again.");
                    return;
                }
                catch (IOException) when (attempt < 5)
                {
                    Thread.Sleep(200);
                }
            }
        }

        // ==============================================================
        static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception)
            {
            }
        }

        // ==============================================================
        public static bool IsElevated()
        {
            using (var identity = WindowsIdentity.GetCurrent())
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }

        // ==============================================================
        public static void StartStreamerBot(string folder)
        {
            Process.Start(new ProcessStartInfo(Path.Combine(folder, StreamerBotExe)) { WorkingDirectory = folder, UseShellExecute = true });
        }

        // ==============================================================
        public static void ShowInExplorer(string folder)
        {
            string dll = DllPath(folder);
            if (File.Exists(dll))
                Process.Start("explorer.exe", "/select,\"" + dll + "\"");
            else
                Process.Start("explorer.exe", "\"" + folder + "\"");
        }

        // ==============================================================
        public static void OpenLink(string url)
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
    }
}
