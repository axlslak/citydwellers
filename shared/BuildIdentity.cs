using System;
using System.Collections.Generic;
using System.Reflection;

namespace CityDwellers.Shared
{
    // Compiled into each binary: read that binary's stamp, never runtime Git or disk DLLs.
    public static class BuildIdentity
    {
        private static readonly string[] Components =
            { "CityDwellers", "CityManager", "CityFlipper", "CityBuddies", "CityBankers" };
        public static readonly string Component = typeof(BuildIdentity).Assembly.GetName().Name;
        public static readonly string Revision = ReadRevision();
        public static readonly string Label = Component + " build " + ShortRevision(Revision);

        private static string ReadRevision()
        {
            var attribute = Attribute.GetCustomAttribute(typeof(BuildIdentity).Assembly,
                typeof(AssemblyInformationalVersionAttribute)) as AssemblyInformationalVersionAttribute;
            return string.IsNullOrWhiteSpace(attribute?.InformationalVersion)
                ? "unknown" : attribute.InformationalVersion;
        }

        private static string ShortRevision(string revision)
        {
            if (string.IsNullOrWhiteSpace(revision)) return "not reported";
            if (revision.Length < 40) return revision;
            return revision.Substring(0, 12) +
                (revision.EndsWith("-modified", StringComparison.Ordinal) ? "-modified" : "");
        }

        private static string Key(string component)
        {
            return "CITYDWELLERS_LOADED_BUILD_" + component.ToUpperInvariant();
        }

        // Process environment is shared across the client AppDomains, but never persisted.
        // Clear inherited values before any clients start; unknown/legacy plugins cannot
        // accidentally inherit a version claim from another run.
        public static void StartHost()
        {
            foreach (string component in Components)
                Environment.SetEnvironmentVariable(Key(component), null, EnvironmentVariableTarget.Process);
            Register();
        }

        public static void Register()
        {
            Environment.SetEnvironmentVariable(Key(Component), Revision, EnvironmentVariableTarget.Process);
        }

        public static IEnumerable<string> DescribeComponents(bool fullRevision = false)
        {
            string host = Environment.GetEnvironmentVariable(Key("CityDwellers"), EnvironmentVariableTarget.Process);
            foreach (string component in Components)
            {
                string revision = Environment.GetEnvironmentVariable(Key(component), EnvironmentVariableTarget.Process);
                string display = string.IsNullOrWhiteSpace(revision)
                    ? "not reported (not loaded or older binary)"
                    : fullRevision ? revision : ShortRevision(revision);
                bool mismatch = !string.IsNullOrWhiteSpace(host) && host != "unknown" &&
                    !string.IsNullOrWhiteSpace(revision) && revision != "unknown" && revision != host;
                yield return component + ": " + display + (mismatch ? " - differs from host" : "");
            }
        }
    }
}
