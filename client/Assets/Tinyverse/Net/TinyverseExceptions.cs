using System;
using Tinyverse.Proto;

namespace Tinyverse.Net
{
    /// <summary>The server refused something: a Hello (join refused) or a request (Error reply).</summary>
    public sealed class TinyverseServerException : Exception
    {
        public ErrorCode Code { get; }

        /// <summary>The server's human-readable explanation.</summary>
        public string ServerMessage { get; }

        /// <summary>The seq of the refused request; 0 for a refused Hello.</summary>
        public uint ReplyTo { get; }

        public TinyverseServerException(ErrorCode code, string serverMessage, uint replyTo = 0)
            : base(string.IsNullOrEmpty(serverMessage) ? code.ToString() : $"{code}: {serverMessage}")
        {
            Code = code;
            ServerMessage = serverMessage ?? "";
            ReplyTo = replyTo;
        }
    }

    /// <summary>The connection failed, was lost, or is not open.</summary>
    public sealed class TinyverseConnectionException : Exception
    {
        public TinyverseConnectionException(string message, Exception inner = null) : base(message, inner) { }
    }
}
