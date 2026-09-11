using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace CityDwellers.Shared
{
    // Common newline-delimited named-pipe transport for the unified host's services.
    // Service messages remain owned by each service; socket lifetime and deadlines do not.
    public static class LocalIpc
    {
        public static TResponse Request<TRequest, TResponse>(string pipeName, TRequest request,
            int connectTimeoutMilliseconds, int responseTimeoutMilliseconds = 120000)
        {
            string line = RequestLineAsync(pipeName, JsonConvert.SerializeObject(request),
                connectTimeoutMilliseconds, responseTimeoutMilliseconds).GetAwaiter().GetResult();
            if (string.IsNullOrWhiteSpace(line))
                throw new IOException("IPC endpoint closed without a response: " + pipeName);
            TResponse response = JsonConvert.DeserializeObject<TResponse>(line);
            if (ReferenceEquals(response, null))
                throw new IOException("IPC endpoint returned invalid JSON: " + pipeName);
            return response;
        }

        public static async Task<string> RequestLineAsync(string pipeName, string request,
            int connectTimeoutMilliseconds, int responseTimeoutMilliseconds)
        {
            using (var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous))
            using (var timeout = new CancellationTokenSource(responseTimeoutMilliseconds))
            using (timeout.Token.Register(() => pipe.Dispose()))
            {
                await pipe.ConnectAsync(connectTimeoutMilliseconds, timeout.Token).ConfigureAwait(false);
                using (var reader = new StreamReader(pipe))
                using (var writer = new StreamWriter(pipe) { AutoFlush = true })
                {
                    await writer.WriteLineAsync(request).ConfigureAwait(false);
                    return await reader.ReadLineAsync().ConfigureAwait(false);
                }
            }
        }

        public static async Task RespondAsync(Stream connection, Func<string, Task<string>> handle,
            int timeoutMilliseconds, CancellationToken lifetime)
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime))
            using (timeout.Token.Register(() => connection.Dispose()))
            using (var reader = new StreamReader(connection))
            using (var writer = new StreamWriter(connection) { AutoFlush = true })
            {
                timeout.CancelAfter(timeoutMilliseconds);
                string request = await reader.ReadLineAsync().ConfigureAwait(false);
                string response = await handle(request).ConfigureAwait(false);
                await writer.WriteLineAsync(response).ConfigureAwait(false);
            }
        }

        public static void Respond<TRequest, TResponse>(Stream connection,
            Func<TRequest, TResponse> handle, Func<Exception, TResponse> onError)
        {
            RespondAsync(connection, line =>
            {
                TResponse response;
                try { response = handle(JsonConvert.DeserializeObject<TRequest>(line ?? string.Empty)); }
                catch (Exception ex) { response = onError(ex); }
                return Task.FromResult(JsonConvert.SerializeObject(response));
            }, 120000, CancellationToken.None).GetAwaiter().GetResult();
        }
    }
}
