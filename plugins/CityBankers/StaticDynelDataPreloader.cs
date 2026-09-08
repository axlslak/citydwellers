using System;
using System.Reflection;
using System.Threading;

using AOSharp.Clientless;
using AOSharp.Clientless.Logging;

namespace CityBankers
{
    /// <summary>
    /// AOSharp.Clientless 1.0.16 lazily opens GameData/StaticDynelData.bin with
    /// exclusive sharing. Each banker lives in its own AppDomain, so concurrent
    /// first access can race and throw IOException. Warm each domain's private
    /// cache before Client.Start(), while sharing the same named mutex already
    /// used by City Dwellers for this AOSharp version.
    /// </summary>
    public class StaticDynelDataPreloader : ClientlessPluginEntry
    {
        // Intentionally identical to City Dwellers so the sibling processes also
        // serialize against us if they happen to run at the same time.
        private const string StaticDynelDataMutexName =
            @"Local\CityDwellers.StaticDynelData.1.0.16";

        private static readonly TimeSpan StaticDynelDataMutexTimeout =
            TimeSpan.FromSeconds(30);

        public override void Init(string pluginDir)
        {
            PreloadStaticDynelData();
        }

        public override void Teardown()
        {
        }

        private static void PreloadStaticDynelData()
        {
            bool mutexHeld = false;

            using (var mutex = new Mutex(false, StaticDynelDataMutexName))
            {
                try
                {
                    try
                    {
                        mutexHeld = mutex.WaitOne(StaticDynelDataMutexTimeout);
                    }
                    catch (AbandonedMutexException)
                    {
                        // The previous owner exited while holding the mutex. The
                        // current caller owns it now and can safely retry.
                        mutexHeld = true;
                    }

                    if (!mutexHeld)
                    {
                        throw new TimeoutException(
                            "Timed out waiting to preload AOSharp static-dynel data.");
                    }

                    Type dataType = typeof(DynelManager).Assembly.GetType(
                        "AOSharp.Clientless.StaticDynelData",
                        true);
                    PropertyInfo staticDynels = dataType.GetProperty(
                        "StaticDynels",
                        BindingFlags.Static | BindingFlags.NonPublic);

                    if (staticDynels == null)
                    {
                        throw new MissingMemberException(
                            dataType.FullName,
                            "StaticDynels");
                    }

                    // Force AOSharp's lazy load now, while this domain exclusively
                    // owns the cross-process mutex. The resulting dictionary stays
                    // cached inside this AppDomain for the rest of the session.
                    staticDynels.GetValue(null, null);
                    Logger.Information(
                        $"CityBankers AOSharp static-dynel preload completed for {Client.CharacterName}.");
                }
                catch (TargetInvocationException ex)
                {
                    throw new InvalidOperationException(
                        "AOSharp static-dynel data preload failed.",
                        ex.InnerException ?? ex);
                }
                finally
                {
                    if (mutexHeld)
                        mutex.ReleaseMutex();
                }
            }
        }
    }
}

