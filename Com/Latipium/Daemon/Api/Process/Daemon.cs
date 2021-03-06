﻿//
// Daemon.cs
//
// Author:
//       Zach Deibert <zachdeibert@gmail.com>
//
// Copyright (c) 2017 Zach Deibert
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Cache;
using System.Net.WebSockets;
using System.Text;
using System.Timers;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Com.Latipium.Daemon.Api.Model;

namespace Com.Latipium.Daemon.Api.Process {
    /// <summary>
    /// The connection to the Latipium Daemon.
    /// </summary>
    public class Daemon : IDisposable {
        private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);
        private const int MaxReceiveSize = 8192;
        private const int PingTimeout = 60000;
        private const string UserAgent = "Latipium Daemon (https://github.com/latipium/daemon)";
        private ClientWebSocket Socket;
        private CancellationTokenSource CancellationTokenSource;
        private ArraySegment<byte> ReceiveBuffer;
        private Task<string> ReceiveTask;
        private Guid ClientId;
        /// <summary>
        /// Gets a value indicating whether this <see cref="Com.Latipium.Daemon.Api.Process.Daemon"/> is connected.
        /// </summary>
        /// <value><c>true</c> if connected; otherwise, <c>false</c>.</value>
        public bool Connected {
            get;
            private set;
        }
        /// <summary>
        /// Occurs when the socket is closed.
        /// </summary>
        public event Action Closed;
        private event Action _Opened;
        /// <summary>
        /// Occurs when the socket is opened.
        /// </summary>
        public event Action Opened {
            add {
                if (Connected) {
                    value();
                } else {
                    _Opened += value;
                }
            }
            remove {
                _Opened -= value;
            }
        }
        private WebClient WebClient;
        private string BaseUrl;
        private System.Timers.Timer PingTimer;

        private void WebsocketSendPart(byte[] sendBuffer, int off, CancellationTokenSource cts) {
            int left = sendBuffer.Length - off;
            Socket.SendAsync(new ArraySegment<byte>(sendBuffer, off, Math.Min(left, MaxReceiveSize)), WebSocketMessageType.Text, left <= MaxReceiveSize, CancellationTokenSource.Token).ContinueWith(t => {
                if (!t.IsCanceled && !t.IsFaulted) {
                    if (left <= MaxReceiveSize) {
                        cts.Cancel();
                    } else {
                        WebsocketSendPart(sendBuffer, off + MaxReceiveSize, cts);
                    }
                }
            });
        }

        /// <summary>
        /// Sends the raw packet.
        /// </summary>
        /// <returns>The response.</returns>
        /// <param name="message">The message.</param>
        public Task<string> SendRaw(string message) {
            if (WebClient == null) {
                byte[] buffer = Encoding.UTF8.GetBytes(message);
                CancellationTokenSource cts = new CancellationTokenSource();
                Task sendTask;
                if (ReceiveTask == null || ReceiveTask.IsCompleted) {
                    sendTask = Task.Delay(int.MaxValue, cts.Token);
                    WebsocketSendPart(buffer, 0, cts);
                } else {
                    sendTask = ReceiveTask.ContinueWith(async t => {
                        WebsocketSendPart(buffer, 0, cts);
                        await Task.Delay(int.MaxValue, cts.Token);
                    });
                }
                return ReceiveTask = sendTask.ContinueWith(t => {
                    Task<string> task = Socket.ReceiveAsync(ReceiveBuffer, CancellationTokenSource.Token).ContinueWith(ReadCallback);
                    task.Wait();
                    return task.Result;
                });
            } else {
                return Send(JsonConvert.DeserializeObject<WebSocketRequest>(message).Tasks).ContinueWith(t => JsonConvert.SerializeObject(t.Result));
            }
        }

        /// <summary>
        /// Sends the specified tasks.
        /// </summary>
        /// <param name="tasks">The tasks.</param>
        public Task<WebSocketResponse> Send(params WebSocketTask[] tasks) {
            if (WebClient == null) {
                return SendRaw(JsonConvert.SerializeObject(new WebSocketRequest() {
                    ClientId = ClientId,
                    Tasks = tasks
                })).ContinueWith(t => JsonConvert.DeserializeObject<WebSocketResponse>(t.Result));
            } else {
                return Task.Run(() => new WebSocketResponse() {
                    Responses = tasks.Select(t => {
                        WebClient.Headers["User-Agent"] = UserAgent;
                        return WebClient.UploadString(string.Concat(BaseUrl, t.Url), t.Request);
                    }).ToArray()
                });
            }
        }

        /// <summary>
        /// Sends the specified task.
        /// </summary>
        /// <param name="task">The task.</param>
        /// <typeparam name="TResponse">The type of the response.</typeparam>
        public Task<TResponse> Send<TResponse>(WebSocketTask task) where TResponse : ResponseObject {
            if (WebClient == null) {
                return Send(new [] { task }).ContinueWith(t => JsonConvert.DeserializeObject<TResponse>(t.Result.Responses[0]));
            } else {
                WebClient.Headers["User-Agent"] = UserAgent;
                return WebClient.UploadStringTaskAsync(string.Concat(BaseUrl, task.Url), task.Request).ContinueWith(t => JsonConvert.DeserializeObject<TResponse>(t.Result));
            }
        }

        private string ReadCallback(Task<WebSocketReceiveResult> task) {
            switch (task.Result.MessageType) {
                case WebSocketMessageType.Binary:
                    Socket.CloseAsync(WebSocketCloseStatus.InvalidMessageType, "Binary messages are not supported", CancellationTokenSource.Token);
                    break;
                case WebSocketMessageType.Text:
                    if (task.Result.EndOfMessage) {
                        return Encoding.UTF8.GetString(ReceiveBuffer.Array, ReceiveBuffer.Offset, task.Result.Count);
                    } else {
                        List<byte[]> parts = new List<byte[]>();
                        parts.Add(ReceiveBuffer.Array.Skip(ReceiveBuffer.Offset).Take(task.Result.Count).ToArray());
                        do {
                            task = Socket.ReceiveAsync(ReceiveBuffer, CancellationTokenSource.Token);
                            task.Wait();
                            parts.Add(ReceiveBuffer.Array.Skip(ReceiveBuffer.Offset).Take(task.Result.Count).ToArray());
                        } while (!task.Result.EndOfMessage);
                        return Encoding.UTF8.GetString(parts.SelectMany(a => a).ToArray());
                    }
                case WebSocketMessageType.Close:
                    Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client closed", CancellationTokenSource.Token);
                    break;
            }
            if (Closed != null) {
                Closed();
            }
            return null;
        }

        private void ConnectCallback(Task task) {
            Connected = true;
            if (task.IsCanceled || task.IsFaulted || Socket.State != WebSocketState.Open) {
                Console.Error.WriteLine("WebSocket failed to initialize");
                WebClient = new WebClient();
                WebClient.Headers["X-Latipium-Client-Id"] = ClientId.ToString();
                WebClient.CachePolicy = new RequestCachePolicy(RequestCacheLevel.NoCacheNoStore);
            } else {
                ReceiveBuffer = new ArraySegment<byte>(new byte[MaxReceiveSize]);
            }
            if (_Opened != null) {
                _Opened();
            }
            PingTimer.Start();
        }

        private void Ping(object sender, ElapsedEventArgs e) {
            bool pinged = false;
            Send(new WebSocketTask() {
                Url = "/ping"
            }).ContinueWith(t => pinged = t.Result != null && t.Result.Successful);
            Task.Delay(PingTimeout, CancellationTokenSource.Token).ContinueWith(t => {
                if (!pinged) {
                    Dispose();
                }
            });
        }

        /// <summary>
        /// Releases all resource used by the <see cref="Com.Latipium.Daemon.Api.Process.Daemon"/> object.
        /// </summary>
        /// <remarks>Call <see cref="Dispose()"/> when you are finished using the
        /// <see cref="Com.Latipium.Daemon.Api.Process.Daemon"/>. The <see cref="Dispose()"/> method leaves the
        /// <see cref="Com.Latipium.Daemon.Api.Process.Daemon"/> in an unusable state. After calling
        /// <see cref="Dispose()"/>, you must release all references to the
        /// <see cref="Com.Latipium.Daemon.Api.Process.Daemon"/> so the garbage collector can reclaim the memory that
        /// the <see cref="Com.Latipium.Daemon.Api.Process.Daemon"/> was occupying.</remarks>
        public void Dispose() {
            Dispose(true);
        }

        /// <summary>
        /// Releases all resource used by the <see cref="Com.Latipium.Daemon.Api.Process.Daemon"/> object.
        /// </summary>
        /// <remarks>Call <see cref="Dispose()"/> when you are finished using the
        /// <see cref="Com.Latipium.Daemon.Api.Process.Daemon"/>. The <see cref="Dispose()"/> method leaves the
        /// <see cref="Com.Latipium.Daemon.Api.Process.Daemon"/> in an unusable state. After calling
        /// <see cref="Dispose()"/>, you must release all references to the
        /// <see cref="Com.Latipium.Daemon.Api.Process.Daemon"/> so the garbage collector can reclaim the memory that
        /// the <see cref="Com.Latipium.Daemon.Api.Process.Daemon"/> was occupying.</remarks>
        protected void Dispose(bool disposing) {
            if (disposing) {
                Closed?.Invoke();
                Connected = false;
                CancellationTokenSource.Cancel();
                CancellationTokenSource cts = new CancellationTokenSource();
                Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Module disposed", cts.Token).ContinueWith(t => cts.Cancel());
                Task.Delay(TimeSpan.FromSeconds(10), cts.Token).ContinueWith(t => cts.Cancel()).Wait();
                PingTimer.Enabled = false;
                PingTimer.Close();
                PingTimer.Dispose();
            }
        }

        internal Daemon(string url, Guid clientId) {
            CancellationTokenSource = new CancellationTokenSource();
            Socket = new ClientWebSocket();
            Socket.Options.AddSubProtocol("latipium");
            ClientId = clientId;
            BaseUrl = url.Replace("+", "localhost").Replace("*", "localhost");
            CancellationTokenSource cts = new CancellationTokenSource();
            Socket.ConnectAsync(new Uri(BaseUrl.Replace("http", "ws")), cts.Token).ContinueWith(t => cts.Cancel()).ContinueWith(ConnectCallback);
            Task.Delay(ConnectTimeout, cts.Token).ContinueWith(t => cts.Cancel());
            PingTimer = new System.Timers.Timer() {
                AutoReset = true,
                Interval = PingTimeout
            };
            PingTimer.Elapsed += Ping;
        }

        /// <summary>
        /// Releases unmanaged resources and performs other cleanup operations before the
        /// <see cref="Com.Latipium.Daemon.Api.Process.Daemon"/> is reclaimed by garbage collection.
        /// </summary>
        ~Daemon() {
            Dispose(false);
        }
    }
}

