using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;

namespace CityDwellers.Shared
{
    public sealed class ComponentBuild
    {
        public string Name;
        public string Installed;
        public string Loaded;
        public bool DiffersFromHost;
        public bool DiffersFromInstalled;
    }

    public static class BuildIdentity
    {
        private static readonly string[] Components =
            { "CityDwellers", "CityManager", "CityFlipper", "CityBuddies", "CityBankers" };
        private const string RootKey = "CITYDWELLERS_BUILD_ROOT";
        private const string HostFileKey = "CITYDWELLERS_BUILD_HOST_FILE";
        public static readonly string Component = typeof(BuildIdentity).Assembly.GetName().Name;
        public static readonly string Revision = ReadRevision();
        public static readonly string Label = Component + " build " + ShortRevision(Revision);

        private static string ReadRevision()
        {
            var attribute = Attribute.GetCustomAttribute(typeof(BuildIdentity).Assembly,
                typeof(AssemblyInformationalVersionAttribute)) as AssemblyInformationalVersionAttribute;
            return IsKnown(attribute?.InformationalVersion) ? attribute.InformationalVersion : "unknown";
        }

        public static bool IsKnown(string revision)
        {
            return revision != null && Regex.IsMatch(revision,
                @"\A(?:[0-9a-f]{40,64}(?:-modified)?|source-[0-9a-f]{64})\z");
        }

        public static string GitRevision(string revision)
        {
            return revision != null && Regex.IsMatch(revision, @"\A[0-9a-f]{40,64}(?:-modified)?\z")
                ? revision.Split('-')[0] : null;
        }

        public static string ShortRevision(string revision)
        {
            if (string.IsNullOrWhiteSpace(revision)) return "not loaded";
            if (!IsKnown(revision)) return revision;
            if (revision.StartsWith("source-", StringComparison.Ordinal)) return revision.Substring(0, 19);
            return revision.Substring(0, 12) +
                (revision.EndsWith("-modified", StringComparison.Ordinal) ? "-modified" : "");
        }

        private static string Key(string component) => "CITYDWELLERS_LOADED_BUILD_" + component.ToUpperInvariant();
        private static string Get(string key) => Environment.GetEnvironmentVariable(key, EnvironmentVariableTarget.Process);
        private static void Set(string key, string value) => Environment.SetEnvironmentVariable(key, value, EnvironmentVariableTarget.Process);

        public static void StartHost(string runtimeDirectory)
        {
            foreach (string component in Components) Set(Key(component), null);
            Set(RootKey, runtimeDirectory);
            Set(HostFileKey, typeof(BuildIdentity).Assembly.Location);
            Register();
        }

        public static void Register() => Set(Key(Component), Revision);

        private static string InstalledRevision(string component)
        {
            try
            {
                string root = Get(RootKey);
                if (string.IsNullOrWhiteSpace(root)) return "checking";
                string path = component == "CityDwellers" ? Get(HostFileKey) : Path.Combine(root, component + ".dll");
                if (!File.Exists(path)) return "missing";
                // Reads PE version metadata only; no assembly load or plugin Init/login.
                string revision = FileVersionInfo.GetVersionInfo(path).ProductVersion;
                return IsKnown(revision) ? revision : "unknown";
            }
            catch { return "unknown"; }
        }

        public static IEnumerable<ComponentBuild> GetComponents()
        {
            string host = Get(Key("CityDwellers"));
            foreach (string component in Components)
            {
                string installed = InstalledRevision(component);
                string loaded = Get(Key(component));
                string effective = string.IsNullOrWhiteSpace(loaded) ? installed : loaded;
                yield return new ComponentBuild
                {
                    Name = component, Installed = installed, Loaded = loaded,
                    DiffersFromHost = IsKnown(host) && IsKnown(effective) && effective != host,
                    DiffersFromInstalled = !string.IsNullOrWhiteSpace(loaded) && loaded != installed
                };
            }
        }

        public static IEnumerable<string> DescribeComponents(bool fullRevision = false)
        {
            foreach (ComponentBuild build in GetComponents())
            {
                string installed = fullRevision ? build.Installed : ShortRevision(build.Installed);
                string loaded = string.IsNullOrWhiteSpace(build.Loaded) ? "not loaded/reported" :
                    fullRevision ? build.Loaded : ShortRevision(build.Loaded);
                yield return build.Name + ": installed " + installed + "; loaded " + loaded +
                    (build.DiffersFromHost ? " - differs from host" : "") +
                    (build.DiffersFromInstalled ? " - loaded differs from installed" : "");
            }
        }
    }
}
