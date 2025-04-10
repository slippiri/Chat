using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace ksis3
{
    class Program
    {
        static async Task Main(string[] args)
        {
            Console.WriteLine("====================================");
            Console.WriteLine("= 1. Создать сервер                =");
            Console.WriteLine("= 2. Продолжить как клиент         =");
            Console.WriteLine("====================================");
            string mode = Console.ReadLine();

            string ip = "";
            int port = 0;

            while (true)
            {
                Console.Write("Введите IP адрес: ");
                ip = Console.ReadLine();
                if (IPAddress.TryParse(ip, out _)) break;
                Console.WriteLine("Неверный формат IP адреса! Попробуйте снова.");
            }

            while (true)
            {
                Console.Write("Введите порт: ");
                string portInput = Console.ReadLine();
                if (int.TryParse(portInput, out port) && port > 0 && port <= 65535) break;
                Console.WriteLine("Неверный формат порта! Введите число от 1 до 65535.");
            }

            if (mode == "1")
            {
                var server = new ChatServer();
                await server.Start(ip, port);
            }
            else if (mode == "2")
            {
                var client = new ChatClient();
                await client.Connect(ip, port);
            }
            else
            {
                Console.WriteLine("Неверный режим");
            }
        }
    }

    class ChatServer
    {
        private TcpListener _tcpListener;
        private UdpClient _udpListener;
        private readonly List<TcpClient> _tcpClients = new List<TcpClient>();
        private readonly List<IPEndPoint> _udpEndPoints = new List<IPEndPoint>();
        private readonly object _lock = new object();

        public async Task Start(string ip, int port)
        {
            try
            {
                _tcpListener = new TcpListener(IPAddress.Parse(ip), port);
                _tcpListener.Start();
                _udpListener = new UdpClient(port);

                Console.WriteLine($"Сервер запущен на {ip}:{port}");

                var tcpTask = AcceptTcpClients();
                var udpTask = AcceptUdpClients();

                await Task.WhenAll(tcpTask, udpTask);
            }
            catch (SocketException ex) when (ex.ErrorCode == 10048)
            {
                Console.WriteLine($"Порт {port} занят!");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка: {ex.Message}");
            }
        }

        private async Task AcceptTcpClients()
        {
            while (true)
            {
                var client = await _tcpListener.AcceptTcpClientAsync();
                lock (_lock) _tcpClients.Add(client);
                _ = HandleTcpClient(client);
            }
        }

        private async Task HandleTcpClient(TcpClient client)
        {
            string clientInfo = null;
            string userName = null;
            int udpPort = 0;

            try
            {
                using (var stream = client.GetStream())
                {
                    var buffer = new byte[1024];
                    int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);
                    if (bytesRead > 0)
                    {
                        string message = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                        var ipEndPoint = (IPEndPoint)client.Client.RemoteEndPoint;


                        if (message.StartsWith("UDP_PORT:"))
                        {
                            udpPort = int.Parse(message.Substring(9));
                            var udpEndPoint = new IPEndPoint(ipEndPoint.Address, udpPort);
                            lock (_lock) _udpEndPoints.Add(udpEndPoint);
                        }

                        bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);
                        userName = Encoding.UTF8.GetString(buffer, 0, bytesRead);

                        clientInfo = $"{userName} [{ipEndPoint.Address}:{ipEndPoint.Port} TCP, {udpPort} UDP]";

                        string connectMessage = $"[Сервер] {clientInfo} подключился";
                        Console.WriteLine(connectMessage);
                        await BroadcastUdp(connectMessage);
                    }

                    while (true)
                    {
                        bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);
                        if (bytesRead == 0) break;

                        string msg = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                        Console.WriteLine($"{userName}: {msg}"); 
                        await BroadcastTcp($"{userName}: {msg}", client); 
                    }
                }
            }
            finally
            {
                if (clientInfo != null)
                {
                    string disconnectMessage = $"[Сервер] {clientInfo} отключился";
                    Console.WriteLine(disconnectMessage);
                    await BroadcastUdp(disconnectMessage);

                    lock (_lock)
                    {
                        _tcpClients.Remove(client);
                        var ipEndPoint = (IPEndPoint)client.Client.RemoteEndPoint;
                        var udpEndPoint = _udpEndPoints.Find(ep =>
                            ep.Address.Equals(ipEndPoint.Address) &&
                            ep.Port == udpPort);
                        if (udpEndPoint != null)
                            _udpEndPoints.Remove(udpEndPoint);
                    }
                }
                client.Dispose();
            }
        }

        private async Task AcceptUdpClients()
        {
            while (true)
            {
                var result = await _udpListener.ReceiveAsync();
            }
        }

        private async Task BroadcastTcp(string message, TcpClient sender)
        {
            var data = Encoding.UTF8.GetBytes(message);
            List<TcpClient> clientsCopy;
            lock (_lock) clientsCopy = new List<TcpClient>(_tcpClients);

            foreach (var client in clientsCopy)
            {
                if (client != sender && client.Connected)
                {
                    try
                    {
                        await client.GetStream().WriteAsync(data, 0, data.Length);
                    }
                    catch
                    {
                        lock (_lock) _tcpClients.Remove(client);
                    }
                }
            }
        }

        private async Task BroadcastUdp(string message)
        {
            var data = Encoding.UTF8.GetBytes(message);
            List<IPEndPoint> endpointsCopy;
            lock (_lock) endpointsCopy = new List<IPEndPoint>(_udpEndPoints);

            foreach (var endpoint in endpointsCopy)
            {
                await _udpListener.SendAsync(data, data.Length, endpoint);
            }
        }
    }

    class ChatClient
    {
        private TcpClient _tcpClient = new TcpClient();
        private UdpClient _udpClient;
        private int _udpPort;
        private string _userName;
        private IPEndPoint _serverEndPoint;


        public async Task Connect(string ip, int port)
        {
            try
            {
                _serverEndPoint = new IPEndPoint(IPAddress.Parse(ip), port);
                await _tcpClient.ConnectAsync(ip, port);
                _udpClient = new UdpClient(0);
                _udpPort = ((IPEndPoint)_udpClient.Client.LocalEndPoint).Port;

                var stream = _tcpClient.GetStream();

                byte[] portData = Encoding.UTF8.GetBytes($"UDP_PORT:{_udpPort}");
                await stream.WriteAsync(portData, 0, portData.Length);

                Console.Write("Введите ваше имя: ");
                _userName = Console.ReadLine();
                byte[] nameData = Encoding.UTF8.GetBytes(_userName);
                await stream.WriteAsync(nameData, 0, nameData.Length);

                var receiveTcpTask = ReceiveTcpMessages();
                var receiveUdpTask = ReceiveUdpNotifications();
                var sendTask = SendMessages();

                await Task.WhenAny(receiveTcpTask, receiveUdpTask, sendTask);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка: {ex.Message}");
            }
        }

        private async Task ReceiveTcpMessages()
        {
            var stream = _tcpClient.GetStream();
            var buffer = new byte[1024];
            while (true)
            {
                int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);
                if (bytesRead == 0) break;
                Console.WriteLine(Encoding.UTF8.GetString(buffer, 0, bytesRead));
            }
        }

        private async Task ReceiveUdpNotifications()
        {
            while (true)
            {
                var result = await _udpClient.ReceiveAsync();
                Console.WriteLine(Encoding.UTF8.GetString(result.Buffer));
            }
        }

        private async Task SendMessages()
        {
            var stream = _tcpClient.GetStream();
            while (true)
            {
                string input = await Task.Run(() => Console.ReadLine());
                byte[] data = Encoding.UTF8.GetBytes(input);
                await stream.WriteAsync(data, 0, data.Length);
            }
        }
    }
}
