using System;
using System.IO;

namespace CityDwellers.Shared
{
    // Parsing and input validation share the same lexical rule. This is not a
    // character-existence check; existence comes from game/bot observations.
    public static class CharacterNames
    {
        public const string RegexClass = @"[\p{L}\p{Nd}-]";
        public const string RegexToken = RegexClass + "+";
        public static bool IsCharacter(char value) => char.IsLetterOrDigit(value) || value == '-';

        // Match the existing banker writer. Never strip valid name characters:
        // removing '-' makes distinct names collide and breaks heartbeat lookup.
        public static string FileToken(string value)
        {
            string token = string.IsNullOrWhiteSpace(value) ? "unknown" : value;
            foreach (char invalid in Path.GetInvalidFileNameChars())
                token = token.Replace(invalid, '_');
            return token;
        }
    }
}
