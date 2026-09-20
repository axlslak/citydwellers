using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace CityDwellers.DataMigration
{
    // Files are hashed with writes/deletion excluded. Cleanup uses the very
    // handle that was verified, so there is no close/hash/path-delete race.
    internal static class VerifiedSource
    {
        private const uint GenericRead = 0x80000000;
        private const uint DeleteAccess = 0x00010000;
        private const uint OpenExisting = 3;
        private const uint OpenReparsePoint = 0x00200000;
        private const uint SequentialScan = 0x08000000;
        private const uint ReparsePoint = 0x00000400;

        internal static string Sha256(Stream stream)
        {
            using (SHA256 sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }

        internal static void RejectReparsePath(string path, string sourceRoot)
        {
            string current = Path.GetFullPath(path);
            string boundary = Path.GetFullPath(sourceRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            while (!string.IsNullOrEmpty(current) &&
                (string.Equals(current.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), boundary, StringComparison.OrdinalIgnoreCase) ||
                 current.StartsWith(boundary + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            {
                FileAttributes attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Refusing a symbolic link/junction/reparse point: " + current);
                string parent = Path.GetDirectoryName(current);
                if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) break;
                current = parent;
            }
        }

        internal static FileStream OpenRead(string path, string sourceRoot)
        {
            RejectReparsePath(path, sourceRoot);
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                128 * 1024, FileOptions.SequentialScan);
        }

        internal static void DeleteVerified(string path, string sourceRoot, long expectedLength, string expectedSha256)
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
            {
                // Mono/POSIX cannot mark the verified .NET file handle for
                // deletion. Bots are explicitly offline and the SQL lease
                // excludes new runtimes: re-open/recheck the named file before
                // unlinking it while both verified handles are still open.
                // Administrative changes to the source must stay stopped too.
                using (FileStream first = OpenRead(path, sourceRoot))
                {
                    if (first.Length != expectedLength ||
                        !string.Equals(Sha256(first), expectedSha256, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("Source changed; retained without deletion: " + path);
                    using (FileStream recheck = OpenRead(path, sourceRoot))
                    {
                        if (recheck.Length != expectedLength ||
                            !string.Equals(Sha256(recheck), expectedSha256, StringComparison.OrdinalIgnoreCase))
                            throw new IOException("Source changed on cleanup recheck; retained: " + path);
                        File.Delete(path);
                    }
                }
                return;
            }

            RejectReparsePath(path, sourceRoot);
            using (SafeFileHandle handle = CreateFile(path, GenericRead | DeleteAccess,
                (uint)FileShare.Read, IntPtr.Zero, OpenExisting,
                OpenReparsePoint | SequentialScan, IntPtr.Zero))
            {
                if (handle.IsInvalid)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot exclusively verify/delete " + path);

                ByHandleFileInformation information;
                if (!GetFileInformationByHandle(handle, out information))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot inspect " + path);
                if ((information.FileAttributes & ReparsePoint) != 0)
                    throw new IOException("Refusing a replaced source reparse point: " + path);

                using (var stream = new FileStream(handle, FileAccess.Read, 128 * 1024, false))
                {
                    if (stream.Length != expectedLength ||
                        !string.Equals(Sha256(stream), expectedSha256, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("Source changed; retained without deletion: " + path);

                    var disposition = new FileDispositionInformation { DeleteFile = 1 };
                    if (!SetFileInformationByHandle(handle, 4, ref disposition, 1))
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "Verified source could not be deleted: " + path);
                    // Closing this same handle completes deletion. New opens with
                    // write/delete access were excluded throughout verification.
                }
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FileDispositionInformation { public byte DeleteFile; }

        [StructLayout(LayoutKind.Sequential)]
        private struct ByHandleFileInformation
        {
            public uint FileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
            public uint VolumeSerialNumber;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint NumberOfLinks;
            public uint FileIndexHigh;
            public uint FileIndexLow;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess,
            uint shareMode, IntPtr securityAttributes, uint creationDisposition,
            uint flagsAndAttributes, IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle file,
            out ByHandleFileInformation information);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetFileInformationByHandle(SafeFileHandle file,
            int informationClass, ref FileDispositionInformation information, uint bufferSize);
    }
}
