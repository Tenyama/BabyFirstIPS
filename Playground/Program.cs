using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Playground
{
    class Program
    {
        private const string PipeName = "SingleInstanceChannel";
        private static readonly ConcurrentDictionary<Guid, NamedPipeServerStream> ConnectedClients = new();

        static async Task Main(string[] args)
        {
            Mutex mutex = new(true, "SingleInstanceMutex", out bool isFirstInstance);
            if (!isFirstInstance)
            {
                // 🚀 Another instance exists → connect and send a message
                try
                {
                    await ConnectAsClient();
                    return; // Client should exit after connecting and handling messages
                }
                catch (TimeoutException)
                {
                    Console.WriteLine("Original host is gone - becoming new host");
                    await RunServerAsync();
                }
                catch (IOException)
                {
                    Console.WriteLine("Original host is gone - becoming new host");

                    Mutex newMutex = new(true, "PeerServerMutex", out bool isHost);
                    if (isHost) await RunServerAsync();
                    else
                    {
                        Console.WriteLine("Looks like a new boss is in town");
                        await ConnectAsClient();
                    }
                }
            }
            else
            {
                Console.WriteLine("✅ This is the first instance. Hosting Named Pipe server...");
                await RunServerAsync();
            }

            // Keep alive so server stays running
            Console.WriteLine("Press ENTER to quit...");
            Console.ReadLine();
            mutex?.Dispose();
        }

        static async Task RunServerAsync()
        {
            _ = Task.Run(async () =>
            {
                while (true)
                {
                    try
                    {
                        var server = new NamedPipeServerStream(
                            PipeName,
                            PipeDirection.InOut,
                            NamedPipeServerStream.MaxAllowedServerInstances,
                            PipeTransmissionMode.Message,
                            PipeOptions.Asynchronous);

                        await server.WaitForConnectionAsync();

                        // Handle each client in its own task
                        _ = Task.Run(async () => await HandleClientConnection(server));
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Server error: {ex.Message}");
                    }
                }
            });
        }

        static async Task HandleClientConnection(NamedPipeServerStream server)
        {
            var clientId = Guid.NewGuid();
            ConnectedClients[clientId] = server;

            Console.WriteLine($"Client connected: {clientId}. Total clients: {ConnectedClients.Count}");

            try
            {
                byte[] buffer = new byte[1024];

                while (server.IsConnected)
                {
                    int bytesRead = await server.ReadAsync(buffer, 0, buffer.Length);
                    if (bytesRead > 0)
                    {
                        string message = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                        Console.WriteLine($"📩 Received from {clientId}: {message}");

                        // Broadcast to all other clients
                        await BroadcastMessage(message, clientId);
                    }
                    else
                    {
                        // No data read usually means client disconnected
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error with client {clientId}: {ex.Message}");
            }
            finally
            {
                // Clean up
                ConnectedClients.TryRemove(clientId, out _);
                server?.Dispose();
                Console.WriteLine($"Client disconnected: {clientId}. Total clients: {ConnectedClients.Count}");
            }
        }

        static async Task BroadcastMessage(string message, Guid senderId)
        {
            var tasks = ConnectedClients
                .Where(client => client.Key != senderId && client.Value.IsConnected)
                .Select(async client =>
                {
                    try
                    {
                        byte[] messageBytes = Encoding.UTF8.GetBytes($"Broadcast from {senderId}: {message}");
                        await client.Value.WriteAsync(messageBytes, 0, messageBytes.Length);
                        await client.Value.FlushAsync();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Failed to send to client {client.Key}: {ex.Message}");
                        ConnectedClients.TryRemove(client.Key, out _);
                    }
                });

            await Task.WhenAll(tasks);
        }

        static async Task ConnectAsClient()
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(2000);

            Console.WriteLine("Connected to server! Type messages to broadcast (type 'exit' to quit):");

            // Start reading messages in background
            var readTask = Task.Run(async () =>
            {
                byte[] buffer = new byte[1024];
                while (client.IsConnected)
                {
                    try
                    {
                        int bytesRead = await client.ReadAsync(buffer, 0, buffer.Length);
                        if (bytesRead > 0)
                        {
                            string receivedMessage = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                            Console.WriteLine($"📨 {receivedMessage}");
                        }
                    }
                    catch (IOException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        if (client.IsConnected)
                            Console.WriteLine($"Unknown Read error: {ex.Message}");
                        break;
                    }
                }
            });

            // Handle writing messages with proper message boundaries
            while (client.IsConnected)
            {
                var message = Console.ReadLine();
                if (string.Equals(message, "exit", StringComparison.OrdinalIgnoreCase))
                    break;

                if (!string.IsNullOrEmpty(message))
                {
                    try
                    {
                        byte[] messageBytes = Encoding.UTF8.GetBytes(message);
                        await client.WriteAsync(messageBytes, 0, messageBytes.Length);
                        await client.FlushAsync();
                        Console.WriteLine($"✓ Sent: {message}");
                    }
                    catch (IOException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Failed to send message: {ex.Message}");
                        break;
                    }
                }
            }

            // Wait for read task to complete
            try
            {
                await readTask;
            }
            catch (IOException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Final cleanup error: {ex.Message}");
            }
        }
    }
}
