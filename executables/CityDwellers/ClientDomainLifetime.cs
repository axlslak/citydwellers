using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Remoting;
using System.Runtime.Remoting.Lifetime;
using AOSharp.Clientless;

// Clientless 1.0.16's PluginProxy has the default remoting lease. Keep
// teardown callable for the entire host lifetime, including idle services.
internal static class ClientDomainLifetime
{
    private static readonly object Sync = new object();
    private static readonly Dictionary<ClientDomain, Registration> Domains =
        new Dictionary<ClientDomain, Registration>();
    private static readonly FieldInfo ProxyField = typeof(ClientDomain).GetField(
        "_pluginProxy", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo DomainField = typeof(ClientDomain).GetField(
        "_appDomain", BindingFlags.Instance | BindingFlags.NonPublic);

    private sealed class Sponsor : MarshalByRefObject, ISponsor
    {
        internal volatile bool Stopped;
        public TimeSpan Renewal(ILease lease) => Stopped ? TimeSpan.Zero : TimeSpan.FromMinutes(10);
        public override object InitializeLifetimeService() => null;
    }

    private sealed class Registration
    {
        internal string Character;
        internal string DomainName;
        internal AppDomain Child;
        internal ILease Lease;
        internal Sponsor Sponsor;
    }

    internal static void Track(ClientDomain domain, string character)
    {
        if (domain == null) return;
        var child = DomainField?.GetValue(domain) as AppDomain;
        if (child == null) throw new InvalidOperationException("Client AppDomain is unavailable for Manager attachment.");
        CityDwellers.Shared.ManagerMemory.Attach(child);
        lock (Sync)
        {
            if (Domains.ContainsKey(domain)) return;
            var registration = new Registration { Character = character, DomainName = child.FriendlyName };
            Domains.Add(domain, registration);
            try
            {
                registration.Child = DomainField?.GetValue(domain) as AppDomain;
                var proxy = ProxyField?.GetValue(domain) as MarshalByRefObject;
                if (registration.Child == null || proxy == null)
                    throw new InvalidOperationException("Clientless domain layout is unsupported.");
                registration.Lease = proxy.GetLifetimeService() as ILease;
                if (registration.Lease == null) return; // An infinite lifetime needs no sponsor.
                registration.Sponsor = new Sponsor();
                registration.Lease.Register(registration.Sponsor, TimeSpan.FromMinutes(10));
                registration.Lease.Renew(TimeSpan.FromMinutes(10));
            }
            catch (Exception ex)
            {
                Console.WriteLine("Client-domain lifetime protection unavailable for " + character +
                    ": " + ex.GetType().Name + ". Direct domain cleanup will be attempted if needed.");
            }
        }
    }

    internal static void Unload(ClientDomain domain)
    {
        string error;
        if (!TryUnload(domain, out error)) throw new InvalidOperationException(error);
    }

    internal static bool TryUnload(ClientDomain domain, out string error)
    {
        error = null;
        if (domain == null) return true;
        Registration registration;
        lock (Sync) Domains.TryGetValue(domain, out registration);
        string character = registration?.Character ?? "client";
        try
        {
            domain.Unload();
        }
        catch (AppDomainUnloadedException) { }
        catch (Exception graceful)
        {
            Console.WriteLine("Plugin teardown failed for " + character + ": " + graceful.GetType().Name +
                ". Trying direct AppDomain unload; plugin cleanup may be incomplete.");
            try
            {
                var child = registration?.Child ?? DomainField?.GetValue(domain) as AppDomain;
                if (child == null) throw new InvalidOperationException("Client AppDomain is unavailable.");
                AppDomain.Unload(child);
            }
            catch (AppDomainUnloadedException) { }
            catch (Exception forced)
            {
                error = "Unable to unload " + character + ": plugin teardown=" + graceful.GetType().Name +
                    "; direct domain cleanup=" + forced.GetType().Name + ".";
                return false; // Keep the sponsor/domain available for the owner's retry path.
            }
        }
        if (registration?.Sponsor != null)
        {
            registration.Sponsor.Stopped = true;
            try { registration.Lease?.Unregister(registration.Sponsor); }
            catch { /* The successfully unloaded child may own the lease object. */ }
            try { RemotingServices.Disconnect(registration.Sponsor); }
            catch { /* Disposal succeeded; the sponsor may never have been marshaled. */ }
        }
        CityDwellers.Shared.ManagerMemory.Current.AbandonAccounting(registration?.DomainName);
        CityDwellers.Shared.ManagerMemory.Current.DisconnectClient(character);
        lock (Sync) Domains.Remove(domain);
        return true;
    }
}
