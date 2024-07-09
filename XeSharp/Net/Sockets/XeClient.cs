﻿using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Text;
using XeSharp.Helpers;
using XeSharp.Net.Events;

namespace XeSharp.Net.Sockets
{
    public class XeClient : IDisposable
    {
        public const int XbdmPort = 730;

        internal TcpClient Client { get; private set; }
        internal StreamReader Reader { get; private set; }
        internal BinaryWriter Writer { get; private set; }

        /// <summary>
        /// Information about this client.
        /// </summary>
        public XeClientInfo Info { get; private set; }

        /// <summary>
        /// The last response from this client.
        /// </summary>
        public XeResponse Response { get; private set; }

        /// <summary>
        /// The host name or IP address of the server this client is connected to.
        /// </summary>
        public string HostName { get; private set; } = "0.0.0.0";

        /// <summary>
        /// Determines whether the client is connected to the server.
        /// </summary>
        public bool IsConnected => Client != null && Client.Connected;

        /// <summary>
        /// The cancellation token source for interrupting operations.
        /// </summary>
        public CancellationTokenSource CancellationTokenSource { get; set; } = new();

        /// <summary>
        /// Determines whether the current operation has been cancelled.
        /// </summary>
        public bool IsCancelled => CancellationTokenSource.IsCancellationRequested;

        public event ClientReadEventHandler ReadEvent;
        public event ClientWriteEventHandler WriteEvent;

        public XeClient() { }

        /// <summary>
        /// Connects to a server via its host name or IP address.
        /// </summary>
        /// <param name="in_hostName">The host name or IP address of the server.</param>
        public XeClient(string in_hostName)
        {
            Connect(in_hostName);
        }

        /// <summary>
        /// Connects to a server via its host name or IP address.
        /// </summary>
        /// <param name="in_hostName">The host name or IP address of the server.</param>
        /// <param name="in_isChecked">Determines if the client checks if it's already connected to the server.</param>
        public void Connect(string in_hostName, bool in_isChecked = true)
        {
            // Console is already connected.
            if (in_isChecked && in_hostName == HostName && IsConnected)
                return;

            HostName = in_hostName;

            Client = new TcpClient();

            // Don't resolve hostname if an ip was provided
            // If we try to resolve IP as a host then it can act weird on ipv6 enabled devices
            if (IPAddress.TryParse(in_hostName, out var ip))
            {
                Client.Connect(ip, XbdmPort);
            }
            else
            {
                Client.Connect(HostName, XbdmPort);
            }

            Reader = new StreamReader(Client.GetStream());
            Writer = new BinaryWriter(Client.GetStream());

            ResetCancel();
            Pop();

            Info = new XeClientInfo(this);
            Response = null;
        }

        /// <summary>
        /// Disconnects from the server.
        /// </summary>
        public void Disconnect()
        {
            SendCommand("bye");
            Dispose();
        }

        /// <summary>
        /// Reconnects to the server.
        /// <para>Only use this if absolutely necessary.</para>
        /// </summary>
        public void Reconnect()
        {
            Dispose();
            Connect(HostName, false);
        }

        /// <summary>
        /// Flushes the client stream.
        /// </summary>
        public void Flush()
        {
            /* This exists merely to hide the fact
               that we're reconnecting... lol */
            Reconnect();
        }

        /// <summary>
        /// Cancels the current operation.
        /// </summary>
        public void Cancel()
        {
            CancellationTokenSource.Cancel();
        }

        /// <summary>
        /// Resets the cancellation token source.
        /// </summary>
        public void ResetCancel()
        {
            CancellationTokenSource = new();
        }

        /// <summary>
        /// Pings the server.
        /// </summary>
        /// <param name="in_timeout">The time (in milliseconds) to wait for a response.</param>
        public bool Ping(int in_timeout = 1000)
        {
            try
            {
                CancellationTokenSource = new(in_timeout);

                var task = Task.Run(() => SendCommand("dbgname", false));

                // Wait for response.
                task.Wait(CancellationTokenSource.Token);

                if (task.Status != TaskStatus.RanToCompletion)
                    return false;

                var response = task.Result;

                if (response?.Status?.ToHResult() != EXeStatusCode.XBDM_NOERR)
                    return false;
            }
            catch
            {
                return false;
            }

            ResetCancel();

            return true;
        }

        /// <summary>
        /// Flushes the last response's message from the client stream.
        /// </summary>
        public string Pop()
        {
            if (!IsConnected)
                return string.Empty;

            return ReadLine();
        }

        /// <summary>
        /// Sends a command to the server.
        /// </summary>
        /// <param name="in_command">The command to send.</param>
        /// <param name="in_isThrowExceptionOnServerError">Determines whether an exception will be thrown if the client encounters an error response from the server.</param>
        public XeResponse SendCommand(string in_command, bool in_isThrowExceptionOnServerError = true)
        {
            if (!IsConnected)
                return null;

            Writer?.Write(Encoding.ASCII.GetBytes($"{in_command}\r\n"));

            return GetResponse(in_isThrowExceptionOnServerError);
        }

        /// <summary>
        /// Gets the last response from the server.
        /// </summary>
        /// <param name="in_isThrowExceptionOnServerError">Determines whether an exception will be thrown if the client encounters an error response from the server.</param>
        public XeResponse GetResponse(bool in_isThrowExceptionOnServerError = true)
        {
            if (!IsConnected)
                return null;

            var response = new XeResponse(this);

            if (in_isThrowExceptionOnServerError && response.Status.IsFailed())
                throw new Exception(response.Status.ToString());

            if (response.Message?.Equals("bye", StringComparison.CurrentCultureIgnoreCase) ?? false)
                Dispose();

            return Response = response;
        }

        #region Reading Methods

        /// <summary>
        /// Reads a single line from the client stream.
        /// </summary>
        public string ReadLine()
        {
            var result = string.Empty;

            ExceptionHelper.OperationCancelledHandler(
                () => result = Reader.ReadLineAsync(CancellationTokenSource.Token).GetAwaiter().GetResult());

            return result ?? string.Empty;
        }

        /// <summary>
        /// Reads all lines from the client stream.
        /// </summary>
        public string[] ReadLines()
        {
            return ReadLinesAsync().GetAwaiter().GetResult();
        }

        /// <summary>
        /// Reads all lines from the client stream asynchronously.
        /// </summary>
        public async Task<string[]> ReadLinesAsync()
        {
            var result = new List<string>();

            var bytesRead = 0U;
            var isBegin = true;

            while (!IsCancelled)
            {
                var line = ReadLine();

                if (string.IsNullOrEmpty(line) || line == "." || XeResponse.IsStatusMessage(line))
                    break;

                result.Add(line);

                bytesRead += (uint)line.Length;

                OnRead(new ClientReadEventArgs(isBegin, bytesRead, 0));

                isBegin = false;
            }

            if (IsCancelled)
                throw new OperationCanceledException();

            return [.. result];
        }

        /// <summary>
        /// Reads the 32-bit data size from the client stream.
        /// <para>Used for reading data for binary responses.</para>
        /// </summary>
        private uint ReadDataSize()
        {
            var buffer = new byte[4];

            Client.GetStream().Read(buffer, 0, buffer.Length);

            return BitConverter.ToUInt32(buffer);
        }

        /// <summary>
        /// Reads all bytes from the client stream.
        /// <para>Used for reading data for binary responses.</para>
        /// <para>Not recommended for large binary responses.</para>
        /// </summary>
        public byte[] ReadBytes()
        {
            return ReadBytesAsync().GetAwaiter().GetResult();
        }

        /// <summary>
        /// Reads all bytes from the client stream asynchronously.
        /// <para>Used for reading data for binary responses.</para>
        /// <para>Not recommended for large binary responses.</para>
        /// </summary>
        public async Task<byte[]> ReadBytesAsync()
        {
            var bytesRead = 0U;
            var bytesTotal = ReadDataSize();

            if (bytesTotal <= 0)
                return [];

            var buffer = new byte[bytesTotal];
            var isBegin = true;

            while (bytesRead < bytesTotal && !IsCancelled)
            {
                var bufferSize = Math.Min(0x1000, bytesTotal - bytesRead);
                var bufferRead = await Client.GetStream().ReadAsync(buffer, (int)bytesRead, (int)bufferSize);

                if (bufferRead == 0)
                    break;

                bytesRead += (uint)bufferRead;

                OnRead(new ClientReadEventArgs(isBegin, bytesRead, bytesTotal));

                isBegin = false;
            }

            if (IsCancelled)
                throw new OperationCanceledException();

            return buffer;
        }

        /// <summary>
        /// Streams all bytes from the client stream to a destination stream.
        /// <para>Used for reading data for binary responses.</para>
        /// <para>Recommended for large binary responses.</para>
        /// </summary>
        /// <param name="in_destination">The destination stream to write to.</param>
        /// <param name="in_bufferSize">The size of the buffer to copy.</param>
        public void CopyTo(Stream in_destination, int in_bufferSize = 81920)
        {
            var bytesRead = 0U;
            var bytesTotal = ReadDataSize();

            if (bytesTotal <= 0)
                return;

            // Allocate resizable buffer with preset size.
            var buffer = ArrayPool<byte>.Shared.Rent(in_bufferSize);

            try
            {
                var isBegin = true;

                while (bytesRead < bytesTotal && !IsCancelled)
                {
                    var bufferSize = Math.Min(0x1000, bytesTotal - bytesRead);
                    var bufferRead = Client.GetStream().Read(buffer, 0, (int)bufferSize);

                    if (bufferRead == 0)
                        break;

                    in_destination.Write(buffer, 0, bufferRead);

                    bytesRead += (uint)bufferRead;

                    OnRead(new ClientReadEventArgs(isBegin, bytesRead, bytesTotal));

                    isBegin = false;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            if (IsCancelled)
                throw new OperationCanceledException();
        }

        protected virtual void OnRead(ClientReadEventArgs in_args)
        {
            ReadEvent?.Invoke(this, in_args);
        }

        #endregion // Reading Methods

        #region Writing Methods

        /// <summary>
        /// Writes a buffer to the client stream.
        /// <para>The client only supports a maximum buffer size of 4,294,967,295 bytes.</para>
        /// <para>This operation cannot be cancelled.</para>
        /// </summary>
        /// <param name="in_data">The buffer to write.</param>
        public void WriteBytes(byte[] in_data)
        {
            WriteBytesAsync(in_data).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Writes a buffer to the client stream asynchronously.
        /// <para>The client only supports a maximum buffer size of 4,294,967,295 bytes.</para>
        /// <para>This operation cannot be cancelled.</para>
        /// </summary>
        /// <param name="in_data">The buffer to write.</param>
        public async Task WriteBytesAsync(byte[] in_data)
        {
            if (in_data.Length > uint.MaxValue)
                throw new InvalidDataException($"The input buffer is too large ({uint.MaxValue:N0} bytes max).");

            var bytesWritten = 0U;
            var bytesTotal = (uint)in_data.Length;
            var isBegin = true;

            while (bytesWritten < bytesTotal)
            {
                var bufferSize = Math.Min(0x1000, bytesTotal - bytesWritten);
                var buffer = new byte[bufferSize];

                Buffer.BlockCopy(in_data, (int)bytesWritten, buffer, 0, (int)bufferSize);

                await Client.GetStream().WriteAsync(buffer);

                bytesWritten += bufferSize;

                OnWrite(new ClientWriteEventArgs(isBegin, bytesWritten, bytesTotal));

                isBegin = false;
            }
        }

        /// <summary>
        /// Streams all bytes from the source stream to the client stream.
        /// <para>The client only supports a maximum buffer size of 4,294,967,295 bytes.</para>
        /// <para>Used for writing data for binary responses.</para>
        /// <para>Recommended for large binary responses.</para>
        /// <para>This operation cannot be cancelled.</para>
        /// </summary>
        /// <param name="in_source">The source stream to read from.</param>
        /// <param name="in_bufferSize">The size of the buffer to copy.</param>
        public void CopyFrom(Stream in_source, int in_bufferSize = 81920)
        {
            var bytesWritten = 0U;
            var bytesTotal = (uint)in_source.Length;

            if (bytesTotal <= 0)
                return;

            // Allocate resizable buffer with preset size.
            var buffer = ArrayPool<byte>.Shared.Rent(in_bufferSize);

            try
            {
                var isBegin = true;

                while (bytesWritten < bytesTotal)
                {
                    var bufferSize = Math.Min(0x1000, bytesTotal - bytesWritten);
                    var bufferRead = in_source.Read(buffer, 0, (int)bufferSize);

                    if (bufferRead == 0)
                        break;

                    Client.GetStream().Write(buffer, 0, bufferRead);

                    bytesWritten += (uint)bufferRead;

                    OnWrite(new ClientWriteEventArgs(isBegin, bytesWritten, bytesTotal));

                    isBegin = false;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        protected virtual void OnWrite(ClientWriteEventArgs in_args)
        {
            WriteEvent?.Invoke(this, in_args);
        }

        #endregion // Writing Methods

        /// <summary>
        /// Disposes any associated members of this client.
        /// <para>Use <see cref="Disconnect"/> to disconnect from the server.</para>
        /// </summary>
        public void Dispose()
        {
            Client?.Close();
            Reader?.Dispose();
            Writer?.Dispose();
        }
    }
}
