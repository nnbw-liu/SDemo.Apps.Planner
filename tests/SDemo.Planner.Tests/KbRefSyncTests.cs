using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using SDemo.App.Planner.Core;
using SDemo.App.Planner.Storage;

namespace SDemo.Planner.Tests;

public sealed class KbRefSyncTests
{
    [Fact]
    public async Task Planner链接登记到KB并在移除后撤销()
    {
        var directory = Path.Combine(Path.GetTempPath(), "sdemo-planner-kbref", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        var port = GetFreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        var received = new ConcurrentQueue<(string Path, string Body)>();
        using var serverCts = new CancellationTokenSource();
        var serverTask = Task.Run(async () =>
        {
            while (!serverCts.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await listener.GetContextAsync();
                }
                catch (Exception) when (serverCts.IsCancellationRequested)
                {
                    return;
                }
                catch (HttpListenerException)
                {
                    return;
                }

                using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                var body = await reader.ReadToEndAsync();
                received.Enqueue((context.Request.Url!.AbsolutePath, body));

                var response = Encoding.UTF8.GetBytes("{}");
                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = response.Length;
                await context.Response.OutputStream.WriteAsync(response);
                context.Response.Close();
            }
        });

        var previousUrl = Environment.GetEnvironmentVariable("SDEMO_KB_URL");
        var previousToken = Environment.GetEnvironmentVariable("SDEMO_KB_TOKEN");
        Environment.SetEnvironmentVariable("SDEMO_KB_URL", $"http://127.0.0.1:{port}/v1/refs");
        Environment.SetEnvironmentVariable("SDEMO_KB_TOKEN", "test-token");

        try
        {
            var store = new PlannerStore(Path.Combine(directory, "planner.db"));
            store.Migrate();

            using (var db = store.Open())
            {
                db.Links.Add(new LinkRow
                {
                    Id = "lk_test",
                    OwnerType = "todo",
                    OwnerId = "t1",
                    Uri = "sdemo.kb/doc/d1@v1#b1",
                    CreatedAt = DateTime.UtcNow,
                });
                db.SaveChanges();
            }

            using (var sync = new KbRefSyncService(store))
            {
                var first = await sync.SyncOnceAsync();
                Assert.False(first.Skipped);
                Assert.Equal(1, first.Added);
                Assert.NotNull(store.GetSetting("kb.refs.state"));

                var registered = await WaitForAsync(received, 1);
                Assert.Equal("/v1/refs", registered[0].Path);
                using (var json = JsonDocument.Parse(registered[0].Body))
                {
                    Assert.Equal("sdemo.planner/todo/t1", json.RootElement.GetProperty("source").GetProperty("entityUri").GetString());
                    Assert.Equal("sdemo.kb/doc/d1@v1#b1", json.RootElement.GetProperty("to").GetString());
                    Assert.Equal("based_on", json.RootElement.GetProperty("rel").GetString());
                }

                // 第二次同步没有变化，不应再发请求。
                var second = await sync.SyncOnceAsync();
                Assert.Equal(0, second.Added);
                Assert.Equal(0, second.Removed);
                Assert.Single(received);

                using (var db = store.Open())
                {
                    db.Links.RemoveRange(db.Links.Where(link => link.Id == "lk_test"));
                    db.SaveChanges();
                }

                var third = await sync.SyncOnceAsync();
                Assert.Equal(1, third.Removed);

                var retracted = await WaitForAsync(received, 2);
                Assert.Equal("/v1/refs/retract", retracted[1].Path);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("SDEMO_KB_URL", previousUrl);
            Environment.SetEnvironmentVariable("SDEMO_KB_TOKEN", previousToken);
            serverCts.Cancel();
            try
            {
                listener.Stop();
            }
            catch (ObjectDisposedException)
            {
                // 已停止。
            }

            try
            {
                await serverTask;
            }
            catch (Exception)
            {
                // 停止时的监听异常。
            }

            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // 清理失败不影响断言。
            }
        }
    }

    private static int GetFreePort()
    {
        using var socket = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        return ((IPEndPoint)socket.LocalEndpoint).Port;
    }

    private static async Task<List<(string Path, string Body)>> WaitForAsync(
        ConcurrentQueue<(string Path, string Body)> received,
        int count)
    {
        for (var attempt = 0; attempt < 100 && received.Count < count; attempt++)
        {
            await Task.Delay(20);
        }

        return received.ToList();
    }
}
