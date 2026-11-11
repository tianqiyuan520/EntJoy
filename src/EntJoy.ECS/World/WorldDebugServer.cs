using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace EntJoy.ECS
{
    /// <summary>只在宿主显式启动时监听本机，返回宿主发布的只读快照。</summary>
    public sealed class WorldDebugServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Task _worker;
        private volatile bool _stopping;
        private byte[] _snapshot;

        public WorldDebugServer(int port)
        {
            if (port < 1 || port > 65535) throw new ArgumentOutOfRangeException(nameof(port));
            _listener.Prefixes.Add($"http://localhost:{port}/");
            _listener.Start();
            _worker = Task.Run(ServeAsync);
        }

        /// <summary>由 World 宿主线程在安全点调用；快照发布后才可通过 HTTP 读取。</summary>
        public void Publish(World world)
        {
            if (world == null) throw new ArgumentNullException(nameof(world));
            Volatile.Write(ref _snapshot, world.EntityManager.ExportDebugJson());
        }

        private async Task ServeAsync()
        {
            while (!_stopping)
            {
                HttpListenerContext context;
                // 关闭时 Close() 会中断挂起的 accept，抛出的是「被请求中止」；
                // 先置 _stopping 再 Close，因此这条路径必定命中过滤器而不是冒泡。
                try { context = await _listener.GetContextAsync().ConfigureAwait(false); }
                catch (Exception) when (_stopping) { return; }
                try
                {
                    var response = context.Response;
                    if (context.Request.HttpMethod != "GET") response.StatusCode = 405;
                    else if (context.Request.Url.AbsolutePath != "/world") response.StatusCode = 404;
                    else
                    {
                        byte[] snapshot = Volatile.Read(ref _snapshot);
                        if (snapshot == null) response.StatusCode = 503;
                        else
                        {
                            response.ContentType = "application/json; charset=utf-8";
                            response.ContentLength64 = snapshot.Length;
                            await response.OutputStream.WriteAsync(snapshot, 0, snapshot.Length).ConfigureAwait(false);
                        }
                    }
                    response.Close();
                }
                catch (HttpListenerException) { context.Response.Abort(); }
                catch (System.IO.IOException) { context.Response.Abort(); }
                catch (ObjectDisposedException) { }
            }
        }

        public void Dispose()
        {
            _stopping = true;
            _listener.Close();
            _worker.GetAwaiter().GetResult();
        }
    }
}
