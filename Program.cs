﻿using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Web;

namespace Sockets
{
    class Program
    {
        static void Main(string[] args)
        {
            AsynchronousSocketListener.StartListening();
        }
    }

    public class AsynchronousSocketListener
    {
        private const int listeningPort = 11000;
        private static ManualResetEvent connectionEstablished = new ManualResetEvent(false);

        private class ReceivingState
        {
            public Socket ClientSocket;
            public const int BufferSize = 1024;
            public readonly byte[] Buffer = new byte[BufferSize];
            public readonly List<byte> ReceivedData = new List<byte>();
        }

        public static void StartListening()
        {
            // Определяем IP-адрес, по которому будем принимать сообщения.
            // Для этого сначала получаем DNS-имя компьютера,
            // а из всех адресов выбираем первый попавшийся IPv4 адрес.
            string hostName = Dns.GetHostName();
            IPHostEntry ipHostEntry = Dns.GetHostEntry(hostName);
            IPAddress ipV4Address = ipHostEntry.AddressList
                .Where(address => address.AddressFamily == AddressFamily.InterNetwork)
                .OrderBy(address => address.ToString())
                .FirstOrDefault();
            if (ipV4Address == null)
            {
                Console.WriteLine(">>> Can't find IPv4 address for host");
                return;
            }
            // По выбранному IP-адресу будем слушать listeningPort.
            IPEndPoint ipEndPoint = new IPEndPoint(ipV4Address, listeningPort);

            // Создаем TCP/IP сокет для приема соединений.
            Socket connectionSocket = new Socket(ipV4Address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

            try
            {
                // Присоединяем сокет к выбранной конечной точке (IP-адресу и порту).
                connectionSocket.Bind(ipEndPoint);
                // Начинаем слушать, в очереди на установку соединений не более 100 клиентов.
                connectionSocket.Listen(100);

                // Принимаем входящие соединения.
                while (true)
                    Accept(connectionSocket);
            }
            catch (Exception e)
            {
                Console.WriteLine(">>> Got exception:");
                Console.WriteLine(e.ToString());
                Console.WriteLine(">>> ");
            }
        }

        private static void Accept(Socket connectionSocket)
        {
            // Сбрасываем состояние события установки соединения: теперь оно "не произошло".
            // Это событие используется для синхронизации потоков.
            connectionEstablished.Reset();

            // Начинаем слушать асинхронно, ожидая входящих соединений.
            // Вторым параметром передаем объект, который будет передан в callback.
            connectionSocket.BeginAccept(AcceptCallback, connectionSocket);
            Console.WriteLine($">>> Waiting for a connection to http://{connectionSocket.LocalEndPoint}");

            // Поток, в котором начали слушать connectionSocket будет ждать,
            // пока кто-нибудь не установит событие connectionEstablished.
            // Это произойдет в AcceptCallback, когда соединение будет установлено.
            connectionEstablished.WaitOne();
        }

        private static void AcceptCallback(IAsyncResult asyncResult)
        {
            // Соединение установлено, сигнализируем основному потоку,
            // чтобы он продолжил принимать соединения.
            connectionEstablished.Set();

            // Получаем сокет к клиенту, с которым установлено соединение.
            Socket connectionSocket = (Socket)asyncResult.AsyncState;
            Socket clientSocket = connectionSocket.EndAccept(asyncResult);

            // Принимаем данные от клиента.
            Receive(clientSocket);
        }

        private static void Receive(Socket clientSocket)
        {
            // Создаем объект для callback.
            ReceivingState receivingState = new ReceivingState();
            receivingState.ClientSocket = clientSocket;
            // Начинаем асинхронно получать данные от клиента.
            // Передаем буфер, куда будут складываться полученные байты.
            clientSocket.BeginReceive(receivingState.Buffer, 0, ReceivingState.BufferSize, SocketFlags.None,
                ReceiveCallback, receivingState);
        }

        private static void ReceiveCallback(IAsyncResult asyncResult)
        {
            // Достаем клиентский сокет из параметра callback.
            ReceivingState receivingState = (ReceivingState)asyncResult.AsyncState;
            Socket clientSocket = receivingState.ClientSocket;

            // Читаем данные из клиентского сокета.
            int bytesReceived = clientSocket.EndReceive(asyncResult);

            if (bytesReceived > 0)
            {
                // В буфер могли поместиться не все данные.
                // Все данные от клиента складываем в другой буфер - ReceivedData.
                receivingState.ReceivedData.AddRange(receivingState.Buffer.Take(bytesReceived));

                // Пытаемся распарсить Request из полученных данных.
                byte[] receivedBytes = receivingState.ReceivedData.ToArray();
                Request request = Request.StupidParse(receivedBytes);
                if (request == null)
                {
                    // request не распарсился, значит получили не все данные.
                    // Запрашиваем еще.
                    clientSocket.BeginReceive(receivingState.Buffer, 0, ReceivingState.BufferSize, SocketFlags.None,
                        ReceiveCallback, receivingState);
                }
                else
                {
                    // Все данные были получены от клиента.
                    // Для удобства выведем их на консоль.
                    Console.WriteLine($">>> Received {receivedBytes.Length} bytes from {clientSocket.RemoteEndPoint}. Data:\n" +
                        Encoding.ASCII.GetString(receivedBytes));

                    // Сформируем ответ.
                    byte[] responseBytes = ProcessRequest(request);

                    // Отправим ответ клиенту.
                    Send(clientSocket, responseBytes);
                }
            }
        }

        private static byte[] ProcessRequest(Request request)
        {
            var uri = request.RequestUri.Split('?');
            var path = uri[0];
            var query = HttpUtility.ParseQueryString(uri.Length == 1 ? "" :uri[1]);
            var head = new StringBuilder("HTTP/1.1 200 OK\r\n");
            var body = new byte[0];
            Console.Error.WriteLine(uri);
            switch (path)
            {
                case "/" or "/hello.html":
                    body = GetHello(request, head, query);
                    break;
                case "/groot.gif":
                    head.Append("Content-Type: image/gif\r\n");
                    body = File.ReadAllBytes("groot.gif");
                    break;
                case "/time.html":
                    head.Append("Content-Type: text/html; charset=utf-8\r\n");
                    body = File.ReadAllBytes("time.template.html");
                    var htmlBody = Encoding.UTF8.GetString(body).Replace("{{ServerTime}}", DateTime.Now.ToString(CultureInfo.InvariantCulture));
                    body = Encoding.UTF8.GetBytes(htmlBody);
                    break;
                default:
                    head = new StringBuilder("HTTP/1.1 404 Not Found\r\n");
                    break;
            }
            
            // head.Append($"Content-Length: {100500}\r\n");
            head.Append("\r\n");
            Console.WriteLine(head);
            return CreateResponseBytes(head, body);
        }

        private static byte[] GetHello(Request request, StringBuilder head, NameValueCollection query)
        {
            head.Append("Content-Type: text/html; charset=utf-8\r\n");
            var body = File.ReadAllBytes("hello.html");
            var htmlBody = Encoding.UTF8.GetString(body);
            var greeting = GetQuerySafe(request, query, "greeting");
            if (greeting != null)
            {
                head.Append($"Set-Cookie: greeting={HttpUtility.UrlEncode(greeting)}\r\n");
                htmlBody = htmlBody.Replace("{{Hello}}", greeting);
            }
            var name = GetQuerySafe(request, query, "name");
            if (name != null)
            {
                head.Append($"Set-Cookie: name={HttpUtility.UrlEncode(name)}\r\n");
                htmlBody = htmlBody.Replace("{{World}}", name);
            }

            body = Encoding.UTF8.GetBytes(htmlBody);
            return body;
        }

        private static string GetQuerySafe(Request request, NameValueCollection query, string key)
        {
            var field = HttpUtility.HtmlEncode(query[key]);
            if (field != null) return field;
            var cookies = request.Headers.Find(h => h.Name == "Cookie");
            if (cookies == null) return null;
            Console.WriteLine(cookies.Value);
            field = cookies.Value.Split("; ")
                .Select(c => c.Split('='))
                .FirstOrDefault(t => t[0] == key)?[1];
            field = HttpUtility.UrlDecode(field);
            Console.WriteLine(field);
            return HttpUtility.HtmlEncode(field);
        }

        // Собирает ответ в виде массива байт из байтов строки head и байтов body.
        private static byte[] CreateResponseBytes(StringBuilder head, byte[] body)
        {
            byte[] headBytes = Encoding.ASCII.GetBytes(head.ToString());
            byte[] responseBytes = new byte[headBytes.Length + body.Length];
            Array.Copy(headBytes, responseBytes, headBytes.Length);
            Array.Copy(body, 0,
                responseBytes, headBytes.Length,
                body.Length);
            return responseBytes;
        }

        private static void Send(Socket clientSocket, byte[] responseBytes)
        {
            Console.WriteLine(">>> Sending {0} bytes to client socket.", responseBytes.Length);
            // Начинаем асинхронно отправлять данные клиенту.
            clientSocket.BeginSend(responseBytes, 0, responseBytes.Length, SocketFlags.None,
                SendCallback, clientSocket);
        }

        private static void SendCallback(IAsyncResult asyncResult)
        {
            // Достаем клиентский сокет из параметра callback.
            Socket clientSocket = (Socket)asyncResult.AsyncState;
            try
            {
                // Завершаем отправку данных клиенту.
                int bytesSent = clientSocket.EndSend(asyncResult);
                Console.WriteLine(">>> Sent {0} bytes to client.", bytesSent);

                // Закрываем соединение.
                clientSocket.Shutdown(SocketShutdown.Both);
                clientSocket.Close();
                Console.WriteLine(">>> ");
            }
            catch (Exception e)
            {
                Console.WriteLine(">>> Got exception:");
                Console.WriteLine(e.ToString());
                Console.WriteLine(">>> ");
            }
        }
    }
}
