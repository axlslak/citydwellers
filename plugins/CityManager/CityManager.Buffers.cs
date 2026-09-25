using System;
using System.Linq;
using System.Threading;
using CityDwellers.Shared;
using Newtonsoft.Json.Linq;

namespace CityManager
{
    public partial class CityManager
    {
        private const int BufferControlTimeoutMilliseconds = 30000;

        private void BeginBufferControl(ReplyTarget target, string action, string character)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string id = Guid.NewGuid().ToString("N");
                bool began = false;
                try
                {
                    BufferAccount account = BufferSettings.Read().Active.FirstOrDefault(candidate =>
                        string.Equals(candidate.Character, character, StringComparison.OrdinalIgnoreCase));
                    if (account == null)
                    {
                        Reply(target, "No enabled buffer named " + character + ".");
                        return;
                    }

                    began = ManagerMemory.Current.BeginBufferControl(id, action, account.Character);
                    if (!began)
                    {
                        Reply(target, "Another buffer sleep/wakeup is already in progress.");
                        return;
                    }

                    BufferControlOperation result =
                        ManagerMemory.Current.WaitForBufferControlResult(
                            id, BufferControlTimeoutMilliseconds);
                    if (result == null || result.Result == null)
                    {
                        Reply(target, "Buffer " + action + " timed out; check the host log before retrying.");
                        return;
                    }

                    Reply(target, result.Success == true
                        ? "Buffers: " + result.Result
                        : "Buffers failed: " + result.Result);
                }
                catch (Exception ex)
                {
                    Reply(target, "Buffer " + action + " failed: " + ex.Message);
                }
                finally
                {
                    if (began) ManagerMemory.Current.FinishBufferControl(id);
                }
            });
        }

        private void BeginBufferStatus(ReplyTarget target)
        {
            QueuePublicWork(target, () =>
            {
                try
                {
                    var accounts = BufferSettings.Read().Active.ToList();
                    if (accounts.Count == 0) { Reply(target, "No froob buffers enabled."); return; }
                    foreach (var account in accounts)
                    {
                        string message;
                        if (ManagerMemory.Current.BufferSleeping(account.Character))
                        {
                            Reply(target, account.Character + ": sleeping for manual owner use.");
                            continue;
                        }
                        try
                        {
                            string response = LocalIpc.RequestLineAsync(BufferSettings.PipeName(account.Character),
                                "{\"Kind\":\"status\"}", 1000, 3000).GetAwaiter().GetResult();
                            var state = JObject.Parse(response);
                            DateTime? observed = (DateTime?)state["ObservedUtc"];
                            if (!observed.HasValue || observed.Value < DateTime.UtcNow.AddSeconds(-5))
                                message = account.Character + ": waiting for a fresh buffer update.";
                            else
                                message = account.Character + ": " + ((bool?)state["Ready"] == true ? "ready" : "starting") +
                                    ", " + (string)state["Profession"] + ", " + (int?)state["NanoCount"] +
                                    " known nanos, " + (int?)state["QueueLength"] + " queued buffs.";
                        }
                        catch (Exception) { message = account.Character + ": buffer status unavailable."; }
                        Reply(target, message);
                    }
                }
                catch (Exception) { Reply(target, "Unable to read Buffers configuration; check the host log."); }
            });
        }
    }
}
