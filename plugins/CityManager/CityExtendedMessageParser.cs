using System;
using AOSharp.Clientless.Chat;
using System.Collections.Generic;

namespace CityManager
{
    internal static class CityExtendedMessageParser
    {
        private const int CityMessageCategory = 1001;
        private const int CloakMessageInstance = 1;
        private const int RadarMessageInstance = 2;
        private const int AttackMessageInstance = 3;

        // Sender names can be unresolved for real players. Require the AO system
        // sender and the encoded city category, never matching relayed prose.
        public static bool TryDecodeNative(GroupMsg message, out string decoded)
        {
            decoded = null;
            return message != null && message.SenderId == 0 && TryDecode(message.Message, out decoded);
        }

        public static string DecodeOrOriginal(string message)
        {
            string decoded;
            return TryDecode(message, out decoded) ? decoded : message;
        }

        internal static bool TryReadExtended(string message, out int category, out int instance,
            out List<object> arguments, out string error)
        {
            category = instance = 0; arguments = null; error = null;
            if (string.IsNullOrEmpty(message) || !message.StartsWith("~&", StringComparison.Ordinal)) return false;
            try
            {
                int offset = 2;
                category = checked((int)ReadServiceBase85(message, ref offset));
                instance = checked((int)ReadServiceBase85(message, ref offset));
                arguments = ReadServiceArguments(message, ref offset);
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        // Generic AO extended-message grammar, independent of the older city-only
        // decoder below. The SDK supplies decoded text, not the original wire bytes.
        private static long ReadServiceBase85(string value, ref int offset)
        {
            if (offset + 5 > value.Length) throw new FormatException("Truncated base85 value.");
            long number = 0;
            for (int i = 0; i < 5; i++)
            {
                int digit = value[offset++] - 33;
                if (digit < 0 || digit >= 85) throw new FormatException("Invalid base85 digit.");
                number = number * 85 + digit;
            }
            if (number > uint.MaxValue) throw new OverflowException("Extended-message number exceeds 32 bits.");
            return number;
        }

        private static int ReadServiceByte(string value, ref int offset)
        {
            if (offset >= value.Length) throw new FormatException("Truncated binary argument.");
            char next = value[offset++];
            // Current AOSharp reads chat strings as ASCII. A question mark in a
            // binary field may be a replaced byte; do not invent its numeric value.
            if (next == '?' || next > 255) throw new FormatException("Binary argument is ambiguous in SDK-decoded text.");
            return next;
        }

        private static long ReadServiceInteger(string value, ref int offset)
        {
            long number = 0;
            for (int i = 0; i < 4; i++) number = number * 256 + ReadServiceByte(value, ref offset);
            return number;
        }

        private static object ServiceReference(long category, long instance)
        {
            string[] sides = { "Neutral", "Clan", "Omni", "Monster", "Advisor", "Guardian", "GM", "Mixed", "Red", "Blue" };
            if (category == 2005 && instance < sides.Length) return sides[(int)instance];
            return new Dictionary<string, long> { ["Category"] = category, ["Instance"] = instance };
        }

        private static List<object> ReadServiceArguments(string value, ref int offset)
        {
            var arguments = new List<object>();
            while (offset < value.Length)
            {
                char type = value[offset++];
                switch (type)
                {
                    case '~':
                        if (offset != value.Length) throw new FormatException("Text follows the extended-message terminator.");
                        return arguments;
                    case 's':
                    case 'S':
                        int length = type == 's' ? ReadServiceByte(value, ref offset) - 1 :
                            ReadServiceByte(value, ref offset) * 256 + ReadServiceByte(value, ref offset);
                        if (length < 0 || offset + length > value.Length) throw new FormatException("Invalid service string length.");
                        arguments.Add(value.Substring(offset, length));
                        offset += length;
                        break;
                    case 'i':
                    case 'u':
                        arguments.Add(ReadServiceBase85(value, ref offset));
                        break;
                    case 'I':
                        arguments.Add(ReadServiceInteger(value, ref offset));
                        break;
                    case 'R':
                        long category = ReadServiceBase85(value, ref offset);
                        long instance = ReadServiceBase85(value, ref offset);
                        arguments.Add(ServiceReference(category, instance));
                        break;
                    case 'l':
                        arguments.Add(ServiceReference(20000, ReadServiceInteger(value, ref offset)));
                        break;
                    default: throw new FormatException("Unknown service argument type: " + type);
                }
            }
            throw new FormatException("Missing extended-message terminator.");
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
