using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EntJoy.Collections;
using EntJoy.JobSystem;
using Xunit;

namespace EntJoy.ECS.Tests
{
    public class WorldDebugTests
    {
        private struct WaitJob : IJob
        {
            public NativeArray<int> Flag;
            public void Execute() { Thread.Sleep(100); Flag[0] = 1; }
        }

        [Fact]
        public void Export_ContainsLiveEntitiesAndTheirValues()
        {
            TestBackend.EnsureInitialized();
            using var world = new World("debug-export");
            var manager = world.EntityManager;
            for (int i = 0; i < 1000; i++)
            {
                var entity = manager.NewEntity(typeof(Position));
                manager.Set(entity, new Position { X = i, Y = -i });
            }

            using var document = JsonDocument.Parse(manager.ExportDebugJson());
            var root = document.RootElement;
            Assert.Equal(1000, root.GetProperty("entityCount").GetInt32());
            var entities = root.GetProperty("entities");
            Assert.Equal(1000, entities.GetArrayLength());
            foreach (var entity in entities.EnumerateArray())
            {
                int id = entity.GetProperty("id").GetInt32();
                var component = entity.GetProperty("components")[0];
                Assert.Equal(typeof(Position).FullName, component.GetProperty("type").GetString());
                Assert.True(component.GetProperty("fieldsAvailable").GetBoolean());
                var fields = component.GetProperty("fields");
                Assert.Equal(id, fields.GetProperty("X").GetSingle());
                Assert.Equal(-id, fields.GetProperty("Y").GetSingle());
            }
        }

        [Fact]
        public void Export_WaitsForTrackedJobsAndExcludesDestroyedEntities()
        {
            TestBackend.EnsureInitialized();
            using var world = new World("debug-jobs");
            var manager = world.EntityManager;
            var removed = manager.NewEntity(typeof(Position));
            manager.DestroyEntity(removed);
            var live = manager.NewEntity(typeof(Position));
            manager.Set(live, new Position { X = float.NaN, Y = float.PositiveInfinity });
            var flag = new NativeArray<int>(1, Allocator.Persistent);
            try
            {
                var job = new WaitJob { Flag = flag };
                var handle = JobScheduler.Schedule(ref job);
                manager.TrackEntityJob(handle, null);
                using var document = JsonDocument.Parse(manager.ExportDebugJson());
                Assert.Equal(1, flag[0]);
                var entities = document.RootElement.GetProperty("entities");
                Assert.Equal(1, entities.GetArrayLength());
                Assert.Equal(live.Version, entities[0].GetProperty("version").GetInt32());
                var fields = entities[0].GetProperty("components")[0].GetProperty("fields");
                Assert.Equal("NaN", fields.GetProperty("X").GetString());
                Assert.Equal("Infinity", fields.GetProperty("Y").GetString());
            }
            finally { flag.Dispose(); }
        }

        [Fact]
        public async Task Server_OnlyServesPublishedSnapshots()
        {
            TestBackend.EnsureInitialized();
            using var world = new World("debug-http");
            var socket = new TcpListener(IPAddress.Loopback, 0);
            socket.Start();
            int port = ((IPEndPoint)socket.LocalEndpoint).Port;
            socket.Stop();
            using var server = new WorldDebugServer(port);
            using var client = new HttpClient { BaseAddress = new Uri($"http://localhost:{port}/") };
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("world")).StatusCode);
            world.EntityManager.NewEntity(typeof(Position));
            server.Publish(world);
            world.EntityManager.NewEntity(typeof(Position));
            using var document = JsonDocument.Parse(await client.GetByteArrayAsync("world"));
            Assert.Equal(1, document.RootElement.GetProperty("entityCount").GetInt32());
            Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.PostAsync("world", null)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("unknown")).StatusCode);
        }
    }
}
