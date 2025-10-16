using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
        private const string pipeName = "SingleInstanceChannel";
        private static readonly ConcurrentDictionary<Guid, NamedPipeServerStream> connectedClients = new();

        static async Task Main()
        {
            Mutex mutex = new(true, "SingleInstanceMutex", out bool isFirstInstance);
            if (!isFirstInstance)
            {
                // 🚀 Another instance exists → connect and send a message
                try
                {
                    await ConnectAsClientAsync();
                    return; // Client should exit after connecting and handling messages
                }
                catch (TimeoutException)
                {
                    Console.WriteLine("Original host is gone or None initiated- becoming new host");
                    await RunServerAsync();
                }
                catch (IOException)
                {
                    Console.WriteLine("Original host is gone - becoming new host");

                    _ = new Mutex(true, "PeerServerMutex", out bool isHost);
                    if (isHost)
                    {
                        await RunServerAsync();
                    }
                    else
                    {
                        Console.WriteLine("Looks like a new boss is in town");
                        await ConnectAsClientAsync();
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
            _ = Console.ReadLine();
            mutex?.Dispose();
        }

        // THE WARNING SUPPRESSION BELOW IS INTENTIONAL
        // Because this method is designed to run indefinitely in the background,
#pragma warning disable CS1998 // Async method lacks 'await' operators and will run synchronously
        static async Task RunServerAsync()
            => _ = Task.Run(async () =>
               {
                   while (true)
                   {
                       try
                       {
                           NamedPipeServerStream server = new(
                               pipeName,
                               PipeDirection.InOut,
                               NamedPipeServerStream.MaxAllowedServerInstances,
                               PipeTransmissionMode.Message,
                               PipeOptions.Asynchronous);

                           await server.WaitForConnectionAsync();

                           // Handle each client in its own task
                           _ = Task.Run(async () => await HandleClientConnectionAsync(server));
                       }
                       catch (Exception ex)
                       {
                           Console.WriteLine($"Server error: {ex.Message}");
                       }
                   }
               });
#pragma warning restore CS1998 // Async method lacks 'await' operators and will run synchronously

        static async Task HandleClientConnectionAsync(NamedPipeServerStream server)
        {
            Guid clientId = Guid.NewGuid();
            connectedClients[clientId] = server;

            Console.WriteLine($"Client connected: {clientId}. Total clients: {connectedClients.Count}");

            try
            {
                byte[] buffer = new byte[1024];

                while (server.IsConnected)
                {
                    int bytesRead = await server.ReadAsync(buffer);
                    if (bytesRead > 0)
                    {
                        string message = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                        Console.WriteLine($"📩 Received from {clientId}: {message}");

                        // Broadcast to all other clients
                        await BroadcastMessageAsync(message, clientId);
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
                _ = connectedClients.TryRemove(clientId, out _);
                server?.Dispose();
                Console.WriteLine($"Client disconnected: {clientId}. Total clients: {connectedClients.Count}");
            }
        }

        static async Task BroadcastMessageAsync(string message, Guid senderId)
        {
            IEnumerable<Task> tasks = connectedClients
                .Where(client => client.Key != senderId && client.Value.IsConnected)
                .Select(async client =>
                {
                    try
                    {
                        byte[] messageBytes = Encoding.UTF8.GetBytes($"Broadcast from {senderId}: {message}");
                        await client.Value.WriteAsync(messageBytes);
                        await client.Value.FlushAsync();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Failed to send to client {client.Key}: {ex.Message}");
                        _ = connectedClients.TryRemove(client.Key, out _);
                    }
                });

            await Task.WhenAll(tasks);
        }

        static async Task ConnectAsClientAsync()
        {
            using NamedPipeClientStream client = new(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(2000);

            Console.WriteLine("Connected to server! Type messages to broadcast (type 'exit' to quit):");

            // Start reading messages in background
            Task readTask = Task.Run(async () =>
            {
                byte[] buffer = new byte[1024];
                while (client.IsConnected)
                {
                    try
                    {
                        int bytesRead = await client.ReadAsync(buffer);
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
                        {
                            Console.WriteLine($"Unknown Read error: {ex.Message}");
                        }

                        break;
                    }
                }
            });

            // Handle writing messages with proper message boundaries
            while (client.IsConnected)
            {
                string? message = Console.ReadLine();
                if (string.Equals(message, "exit", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                if (!string.IsNullOrEmpty(message))
                {
                    try
                    {
                        byte[] messageBytes = Encoding.UTF8.GetBytes(message);
                        await client.WriteAsync(messageBytes);
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
