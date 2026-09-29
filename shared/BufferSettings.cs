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
        public List<BufferAccount> Paid = new List<BufferAccount>();
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
            result.Paid = result.Paid ?? new List<BufferAccount>();
            if (!result.Enabled) return result;
            // Repeated character entries describe one worker. An enabled Paid entry
            // selects on-demand operation when the character is also listed in Froobs.
            result.Paid = result.Paid.Where(a => a == null || a.Enabled)
                .GroupBy(a => a?.Character, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First()).ToList();
            var paidCharacters = new HashSet<string>(result.Paid.Where(a => a != null)
                .Select(a => a.Character), StringComparer.OrdinalIgnoreCase);
            result.Froobs = result.Froobs.Where(a => a == null || a.Enabled)
                .Where(a => a == null || !paidCharacters.Contains(a.Character))
                .GroupBy(a => a?.Character, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First()).ToList();
            var froobAccounts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var account in result.Froobs.Where(a => a == null || a.Enabled))
            {
                if (account == null || string.IsNullOrWhiteSpace(account.Username) ||
                    string.IsNullOrWhiteSpace(account.Password) ||
                    !Regex.IsMatch(account.Character ?? "", @"\A" + CharacterNames.RegexClass + @"{1,30}\z"))
                    throw new InvalidOperationException("Each enabled Buffers.Froobs entry needs Username, Password and a valid Character.");
                if (!froobAccounts.Add(account.Username))
                    throw new InvalidOperationException("Buffers.Froobs must use distinct accounts.");
            }

            // Paid characters can share accounts with each other and Flipper.
            var paidAccounts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var account in result.Paid.Where(a => a == null || a.Enabled))
            {
                if (account == null || string.IsNullOrWhiteSpace(account.Username) ||
                    string.IsNullOrWhiteSpace(account.Password) ||
                    !Regex.IsMatch(account.Character ?? "", @"\A" + CharacterNames.RegexClass + @"{1,30}\z"))
                    throw new InvalidOperationException("Each enabled Buffers.Paid entry needs Username, Password and a valid Character.");
                if (froobAccounts.Contains(account.Username))
                    throw new InvalidOperationException("Paid buffers cannot share a persistent froob buffer account.");
                paidAccounts.Add(account.Username);
            }

            // Froobs are persistent workers and must remain on dedicated accounts.
            // Paid may share only with the other account-arbitrated service, Flipper.
            foreach (var section in root.Properties().Where(p => !string.Equals(p.Name, "Buffers", StringComparison.OrdinalIgnoreCase)))
            {
                var container = section.Value as JContainer;
                if (container == null) continue;
                foreach (var property in container.Descendants().OfType<JProperty>())
                {
                    if (string.Equals(property.Name, "Username", StringComparison.OrdinalIgnoreCase) &&
                        property.Value.Type == JTokenType.String && froobAccounts.Contains((string)property.Value))
                        throw new InvalidOperationException("An enabled froob buffer account is also configured in " + section.Name + ". Use a separate account.");
                    if (!string.Equals(section.Name, "Flipper", StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(property.Name, "Username", StringComparison.OrdinalIgnoreCase) &&
                        property.Value.Type == JTokenType.String && paidAccounts.Contains((string)property.Value))
                        throw new InvalidOperationException("A paid buffer account may share only with Flipper, not " + section.Name + ".");
                }
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
        public string Profession;
    }
}
