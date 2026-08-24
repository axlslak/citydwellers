using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;

public static class ClientlessGameDataBootstrap
{
    private const string RestoreCommand = "restore-gamedata";

    // AOSharp.Clientless 1.0.16 was built from this revision. Pinning the
    // source keeps its runtime data paired with the clientless DLL instead of
    // following a moving branch.
    private const string SourceRevision =
        "5f2411cc0eea283287d356fc1147d2351a1bb1c8";

    private const string SourceRoot =
        "https://gitlab.com/never-knows-best/aosharp.clientless/-/raw/" +
        SourceRevision +
        "/AOSharp.Clientless/GameData/";

    private static readonly RequiredFile[] RequiredFiles =
    {
        new RequiredFile(
            "ItemData.bin",
            "40f5dec59f96828741b5c1e79573df7562b4b06e7e8c691e37d052c112e567ae"),
        new RequiredFile(
            "ItemData.idx",
            "d308e3ec47a5aa5734c75a535a5a9e9a021dd647953f4fe9b14b544c11a65fd8"),
        new RequiredFile(
            "PlayfieldNames.json",
            "11611a5b782fbee5a5e0919961e68c75d59d40c3a98ee9475ad1405ec561a15a"),
        new RequiredFile(
            "SkillTrickle.json",
            "4071a1f6ef2d7ba0a5c9ed12bdeef5ed5aebb95dfea95108fdf53b0e021ac26a"),
        new RequiredFile(
            "StaticDynelData.bin",
            "7ddd7859c5dbf5d83f87c2e5b7676316129e877132ef629262fd7d86d2525b45")
    };

    public static bool IsRestoreCommand(string[] args)
    {
        return args != null &&
               args.Length > 0 &&
               string.Equals(
                   args[0],
                   RestoreCommand,
                   StringComparison.OrdinalIgnoreCase);
    }

    public static int Run(string[] args)
    {
        if (args == null || args.Length != 3)
        {
            Console.Error.WriteLine(
                "Internal usage: Flipper.exe restore-gamedata <cache-directory> <output-directory>");
            return 2;
        }

        try
        {
            string cacheDirectory = Path.GetFullPath(args[1]);
            string outputDirectory = Path.GetFullPath(args[2]);

            Directory.CreateDirectory(cacheDirectory);
            Directory.CreateDirectory(outputDirectory);

            using (var httpClient = new HttpClient())
            {
                httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
                    "CityDwellers-GameDataBootstrap/1.0");

                foreach (RequiredFile requiredFile in RequiredFiles)
                {
                    ValidateExpectedHash(requiredFile);

                    string cachePath =
                        Path.Combine(cacheDirectory, requiredFile.Name);
                    string outputPath =
                        Path.Combine(outputDirectory, requiredFile.Name);

                    EnsureCached(httpClient, requiredFile, cachePath);
                    EnsureOutputCopy(requiredFile, cachePath, outputPath);
                }
            }

            Console.WriteLine("AOSharp.Clientless GameData is ready.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                "Unable to restore AOSharp.Clientless GameData: " + ex.Message);
            return 1;
        }
    }

    private static void EnsureCached(
        HttpClient httpClient,
        RequiredFile requiredFile,
        string cachePath)
    {
        if (File.Exists(cachePath) &&
            HashMatches(cachePath, requiredFile.Sha256))
        {
            return;
        }

        if (File.Exists(cachePath))
        {
            Console.WriteLine(
                $"Cached GameData/{requiredFile.Name} failed verification; " +
                "downloading a clean copy.");
        }
        else
        {
            Console.WriteLine(
                $"Downloading AOSharp.Clientless GameData/{requiredFile.Name}...");
        }

        string temporaryPath =
            cachePath + ".download-" + Guid.NewGuid().ToString("N");

        try
        {
            using (HttpResponseMessage response = httpClient
                .GetAsync(
                    new Uri(SourceRoot + requiredFile.Name),
                    HttpCompletionOption.ResponseHeadersRead)
                .GetAwaiter()
                .GetResult())
            {
                response.EnsureSuccessStatusCode();

                using (Stream source = response.Content
                    .ReadAsStreamAsync()
                    .GetAwaiter()
                    .GetResult())
                using (FileStream destination = File.Create(temporaryPath))
                {
                    source.CopyTo(destination);
                }
            }

            string downloadedHash = ComputeSha256(temporaryPath);
            if (!string.Equals(
                    downloadedHash,
                    requiredFile.Sha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Hash verification failed for {requiredFile.Name}. " +
                    $"Expected {requiredFile.Sha256}, received {downloadedHash}.");
            }

            if (File.Exists(cachePath))
                File.Delete(cachePath);

            File.Move(temporaryPath, cachePath);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static void EnsureOutputCopy(
        RequiredFile requiredFile,
        string cachePath,
        string outputPath)
    {
        if (File.Exists(outputPath) &&
            HashMatches(outputPath, requiredFile.Sha256))
        {
            return;
        }

        File.Copy(cachePath, outputPath, true);
    }

    private static bool HashMatches(string path, string expectedHash)
    {
        return string.Equals(
            ComputeSha256(path),
            expectedHash,
            StringComparison.OrdinalIgnoreCase);
    }

    private static string ComputeSha256(string path)
    {
        using (var stream = File.OpenRead(path))
        using (SHA256 sha256 = SHA256.Create())
        {
            return BitConverter
                .ToString(sha256.ComputeHash(stream))
                .Replace("-", string.Empty);
        }
    }

    private static void ValidateExpectedHash(RequiredFile requiredFile)
    {
        if (requiredFile.Sha256 == null ||
            requiredFile.Sha256.Length != 64)
        {
            throw new InvalidDataException(
                $"The expected SHA-256 for {requiredFile.Name} must contain " +
                "exactly 64 hexadecimal characters.");
        }

        foreach (char value in requiredFile.Sha256)
        {
            if (!Uri.IsHexDigit(value))
            {
                throw new InvalidDataException(
                    $"The expected SHA-256 for {requiredFile.Name} contains " +
                    "a non-hexadecimal character.");
            }
        }
    }

    private sealed class RequiredFile
    {
        public readonly string Name;
        public readonly string Sha256;

        public RequiredFile(string name, string sha256)
        {
            Name = name;
            Sha256 = sha256;
        }
    }
}
