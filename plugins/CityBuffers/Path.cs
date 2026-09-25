using System.IO;
using AOSharp.Clientless;
using CityDwellers.Shared;

namespace MalisBuffBots
{
    public class Path
    {
        public static string PLUGIN_DIR, SETTINGS_JSON, BUFF_JSON, REBUFF_JSON;
        public static void Init(string pluginDir)
        {
            string settings, data, error;
            if (!SettingsPaths.TryEnsureDirectories(out settings, out data, out error))
                throw new System.InvalidOperationException(error);
            PLUGIN_DIR = System.IO.Path.Combine(pluginDir, "Buffers");
            SETTINGS_JSON = System.IO.Path.Combine(PLUGIN_DIR, "JSON", "Settings.json");
            BUFF_JSON = System.IO.Path.Combine(PLUGIN_DIR, "JSON", "BuffsDb.json");
            REBUFF_JSON = System.IO.Path.Combine(PLUGIN_DIR, "JSON", "RebuffInfo.json");
        }
    }
}
