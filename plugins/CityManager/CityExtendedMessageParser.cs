using System;
using System.Collections.Generic;

namespace CityManager
{
    internal static class CityExtendedMessageParser
    {
        private const int CityMessageCategory = 1001;
        private const int CloakMessageInstance = 1;
        private const int RadarMessageInstance = 2;
        private const int AttackMessageInstance = 3;

        public static string DecodeOrOriginal(string message)
        {
            string decoded;
            return TryDecode(message, out decoded) ? decoded : message;
        }

        public static bool TryDecode(string message, out string decoded)
        {
            decoded = null;

            if (string.IsNullOrEmpty(message) ||
                message.Length < 13 ||
                !message.StartsWith("~&", StringComparison.Ordinal))
            {
                return false;
            }

            try
            {
                int offset = 2;
                int category = DecodeBase85(message, ref offset);
                int instance = DecodeBase85(message, ref offset);

                if (category != CityMessageCategory)
                    return false;

                List<object> arguments = ParseArguments(message, ref offset);

                switch (instance)
                {
                    case CloakMessageInstance:
                        if (arguments.Count < 2)
                            return false;

                        string actor = Convert.ToString(arguments[0]) ?? string.Empty;
                        string action = Convert.ToString(arguments[1]) ?? string.Empty;

                        if (!string.Equals(action, "on", StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(action, "off", StringComparison.OrdinalIgnoreCase))
                        {
                            return false;
                        }

                        decoded =
                            $"{actor} turned the cloaking device in your city {action.ToLowerInvariant()}.";
                        return true;

                    case RadarMessageInstance:
                        decoded =
                            "Your radar station is picking up alien activity in the area surrounding your city.";
                        return true;

                    case AttackMessageInstance:
                        if (arguments.Count < 1)
                            return false;

                        string location = Convert.ToString(arguments[0]) ?? string.Empty;
                        decoded =
                            $"Your city in {location} has been targeted by hostile forces.";
                        return true;

                    default:
                        return false;
                }
            }
            catch
            {
                decoded = null;
                return false;
            }
        }

        private static int DecodeBase85(string value, ref int offset)
        {
            if (offset < 0 || offset + 5 > value.Length)
                throw new FormatException("Truncated base85 value.");

            long result = 0;

            for (int index = 0; index < 5; index++)
            {
                int digit = value[offset++] - 33;
                if (digit < 0 || digit >= 85)
                    throw new FormatException("Invalid base85 digit.");

                result = (result * 85) + digit;
            }

            if (result > int.MaxValue)
                throw new OverflowException("Extended-message number is too large.");

            return (int)result;
        }

        private static List<object> ParseArguments(string value, ref int offset)
        {
            var result = new List<object>();

            while (offset < value.Length)
            {
                char type = value[offset++];

                if (type == '~')
                    break;

                switch (type)
                {
                    case 's':
                    {
                        if (offset >= value.Length)
                            throw new FormatException("Missing short-string length.");

                        int encodedLength = value[offset++];
                        int stringLength = encodedLength - 1;

                        if (stringLength < 0 || offset + stringLength > value.Length)
                            throw new FormatException("Invalid short-string length.");

                        result.Add(value.Substring(offset, stringLength));
                        offset += stringLength;
                        break;
                    }

                    case 'S':
                    {
                        if (offset + 2 > value.Length)
                            throw new FormatException("Missing long-string length.");

                        int stringLength =
                            (value[offset] << 8) |
                            value[offset + 1];
                        offset += 2;

                        if (offset + stringLength > value.Length)
                            throw new FormatException("Invalid long-string length.");

                        result.Add(value.Substring(offset, stringLength));
                        offset += stringLength;
                        break;
                    }

                    case 'i':
                    case 'u':
                        result.Add(DecodeBase85(value, ref offset));
                        break;

                    case 'I':
                    {
                        if (offset + 4 > value.Length)
                            throw new FormatException("Missing integer data.");

                        int number =
                            (value[offset] << 24) |
                            (value[offset + 1] << 16) |
                            (value[offset + 2] << 8) |
                            value[offset + 3];
                        offset += 4;
                        result.Add(number);
                        break;
                    }

                    default:
                        throw new FormatException(
                            $"Unsupported extended-message argument type '{type}'.");
                }
            }

            return result;
        }
    }
}
