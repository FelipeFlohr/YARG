using System;
using System.Text;
using YARG.Multiplayer.Transport;
#if UNITY_WEBGL && !UNITY_EDITOR
using System.Runtime.InteropServices;
#endif

namespace YARG.Multiplayer
{
    /// <summary>
    /// The browser's WebSocket (see YargWebSocket.jslib). Only works in a web build; elsewhere it stays closed.
    /// </summary>
    public sealed class BrowserWebSocket : IWebSocket
    {
        private int    _id;
        private byte[] _buffer = new byte[64 * 1024];

        public WebSocketStatus Status
        {
            get
            {
                if (_id == 0)
                {
                    return WebSocketStatus.Closed;
                }

                return StateNative(_id) switch
                {
                    0 => WebSocketStatus.Connecting,
                    1 => WebSocketStatus.Open,
                    _ => WebSocketStatus.Closed,
                };
            }
        }

#nullable enable
        public string? CloseReason => _id == 0 ? "not supported outside the web build" : CloseReasonNative(_id);
#nullable disable

        public long BufferedAmount => _id == 0 ? 0 : (long) BufferedAmountNative(_id);

        /// <summary>
        /// The relay on the page's origin (<c>wss://host/ws</c>), or null outside a web build.
        /// </summary>
        public static string DefaultRelayUrl()
        {
            string url = DefaultRelayUrlNative();
            return string.IsNullOrEmpty(url) ? null : url;
        }

        public void Open(string url)
        {
            if (_id != 0)
            {
                throw new InvalidOperationException("The socket was already opened");
            }
            _id = OpenNative(url);
        }

        public void SendText(string text)
        {
            if (_id != 0)
            {
                SendTextNative(_id, text);
            }
        }

        public void SendBinary(byte[] data)
        {
            if (_id != 0)
            {
                SendBinaryNative(_id, data, data.Length);
            }
        }

        public bool TryReceive(out WebSocketFrame frame)
        {
            frame = default;
            if (_id == 0)
            {
                return false;
            }

            int size = PeekSizeNative(_id);
            if (size < 0)
            {
                return false;
            }

            if (size > _buffer.Length)
            {
                _buffer = new byte[Math.Max(size, _buffer.Length * 2)];
            }

            bool isText = PeekIsTextNative(_id) != 0;
            int read = ReceiveNative(_id, _buffer, _buffer.Length);
            if (read < 0)
            {
                return false;
            }

            if (isText)
            {
                frame = WebSocketFrame.FromText(Encoding.UTF8.GetString(_buffer, 0, read));
            }
            else
            {
                var data = new byte[read];
                Buffer.BlockCopy(_buffer, 0, data, 0, read);
                frame = WebSocketFrame.FromData(data);
            }
            return true;
        }

        public void Close()
        {
            if (_id != 0)
            {
                CloseNative(_id);
            }
        }

        public void Dispose()
        {
            if (_id != 0)
            {
                FreeNative(_id);
                _id = 0;
            }
        }

#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal", EntryPoint = "YargWebSocket_Open")]
        private static extern int OpenNative(string url);

        [DllImport("__Internal", EntryPoint = "YargWebSocket_State")]
        private static extern int StateNative(int id);

        [DllImport("__Internal", EntryPoint = "YargWebSocket_CloseReason")]
        private static extern string CloseReasonNative(int id);

        [DllImport("__Internal", EntryPoint = "YargWebSocket_BufferedAmount")]
        private static extern double BufferedAmountNative(int id);

        [DllImport("__Internal", EntryPoint = "YargWebSocket_SendText")]
        private static extern void SendTextNative(int id, string text);

        [DllImport("__Internal", EntryPoint = "YargWebSocket_SendBinary")]
        private static extern void SendBinaryNative(int id, byte[] data, int length);

        [DllImport("__Internal", EntryPoint = "YargWebSocket_PeekSize")]
        private static extern int PeekSizeNative(int id);

        [DllImport("__Internal", EntryPoint = "YargWebSocket_PeekIsText")]
        private static extern int PeekIsTextNative(int id);

        [DllImport("__Internal", EntryPoint = "YargWebSocket_Receive")]
        private static extern int ReceiveNative(int id, byte[] buffer, int length);

        [DllImport("__Internal", EntryPoint = "YargWebSocket_Close")]
        private static extern void CloseNative(int id);

        [DllImport("__Internal", EntryPoint = "YargWebSocket_Free")]
        private static extern void FreeNative(int id);

        [DllImport("__Internal", EntryPoint = "YargWebSocket_DefaultRelayUrl")]
        private static extern string DefaultRelayUrlNative();
#else
        private static int OpenNative(string url) => 0;

        private static int StateNative(int id) => 3;

        private static string CloseReasonNative(int id) => null;

        private static double BufferedAmountNative(int id) => 0;

        private static void SendTextNative(int id, string text)
        {
        }

        private static void SendBinaryNative(int id, byte[] data, int length)
        {
        }

        private static int PeekSizeNative(int id) => -1;

        private static int PeekIsTextNative(int id) => 0;

        private static int ReceiveNative(int id, byte[] buffer, int length) => -1;

        private static void CloseNative(int id)
        {
        }

        private static void FreeNative(int id)
        {
        }

        private static string DefaultRelayUrlNative() => null;
#endif
    }
}
