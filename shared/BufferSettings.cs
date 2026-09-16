using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace CityDwellers.Shared
{
    internal sealed class BufferSettings
    {
        public bool Enabled;
        public List<BufferAccount> Froobs = new List<BufferAccount>();
        public JObject Behavior = new JObject();

        public static BufferSettings Read()
        {
            string settingsDirectory, error;
            if (!SettingsPaths.TryEnsureDirectory(out settingsDirectory, out error))
                throw new InvalidOperationException(error);
            var root = JObject.Parse(File.ReadAllText(System.IO.Path.Combine(
                settingsDirectory, "citydwellers.json")));
            var token = root.GetValue("Buffers", StringComparison.OrdinalIgnoreCase);
            var result = token == null ? new BufferSettings() : token.ToObject<BufferSettings>();
            if (result == null) throw new InvalidOperationException("Buffers must be an object.");
            result.Behavior = result.Behavior ?? new JObject();
            result.Froobs = result.Froobs ?? new List<BufferAccount>();
            if (!result.Enabled) return result;
            var characters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var accounts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var account in result.Froobs.Where(a => a == null || a.Enabled))
            {
                if (account == null || string.IsNullOrWhiteSpace(account.Username) ||
                    string.IsNullOrWhiteSpace(account.Password) ||
                    !Regex.IsMatch(account.Character ?? "", @"\A[A-Za-z0-9-]{1,30}\z"))
                    throw new InvalidOperationException("Each enabled Buffers.Froobs entry needs Username, Password and a valid Character.");
                if (!characters.Add(account.Character) || !accounts.Add(account.Username))
                    throw new InvalidOperationException("Buffers.Froobs must use distinct characters and accounts.");
            }
            // Never log a buffer on an account already owned by Flipper/Buddies/etc.
            foreach (var section in root.Properties().Where(p => !string.Equals(p.Name, "Buffers", StringComparison.OrdinalIgnoreCase)))
            {
                var container = section.Value as JContainer;
                if (container == null) continue;
                foreach (var property in container.Descendants().OfType<JProperty>())
                    if (string.Equals(property.Name, "Username", StringComparison.OrdinalIgnoreCase) &&
                        property.Value.Type == JTokenType.String && accounts.Contains((string)property.Value))
                        throw new InvalidOperationException("An enabled froob buffer account is also configured in " + section.Name + ". Use a separate account.");
            }
            return result;
        }

        public IEnumerable<BufferAccount> Active => Enabled
            ? Froobs.Where(a => a != null && a.Enabled) : Enumerable.Empty<BufferAccount>();
        public static string PipeName(string character) => "CityDwellers.Buffer." + character.ToLowerInvariant();
    }

    internal sealed class BufferAccount
    {
        public bool Enabled = true;
        public string Username;
        public string Password;
        public string Character;
    }
}
