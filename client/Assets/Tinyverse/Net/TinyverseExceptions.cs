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

    /// <summary>Why a <c>wss://</c> server's certificate was refused.</summary>
    public enum CertificateProblem
    {
        /// <summary>No pin was given and the system does not trust the certificate (e.g. self-signed).</summary>
        Untrusted,
        /// <summary>A pin was given and the server presented a different certificate.</summary>
        PinMismatch,
    }

    /// <summary>
    /// A <c>wss://</c> server's certificate was refused before anything was sent. Carries what the
    /// server presented, so the player can be asked whether to trust it (see <see cref="CertificatePin"/>).
    /// </summary>
    public sealed class TinyverseCertificateException : Exception
    {
        public CertificateProblem Problem { get; }

        /// <summary><c>host:port</c> as in <see cref="CertificatePin.HostPortKey(Uri)"/>.</summary>
        public string HostPort { get; }

        /// <summary>SHA-256 of the certificate the server presented, lower-case hex.</summary>
        public string Fingerprint { get; }

        /// <summary>The pin that was expected (lower-case hex), for <see cref="CertificateProblem.PinMismatch"/>; else null.</summary>
        public string ExpectedPin { get; }

        /// <summary>What the system's check said, for <see cref="CertificateProblem.Untrusted"/> (e.g. RemoteCertificateChainErrors).</summary>
        public string PolicyErrors { get; }

        public TinyverseCertificateException(CertificateProblem problem, string hostPort, string fingerprint,
            string expectedPin, string policyErrors, Exception inner = null)
            : base(Describe(problem, hostPort, fingerprint, expectedPin), inner)
        {
            Problem = problem;
            HostPort = hostPort ?? "";
            Fingerprint = fingerprint ?? "";
            ExpectedPin = expectedPin;
            PolicyErrors = policyErrors ?? "";
        }

        static string Describe(CertificateProblem problem, string hostPort, string fingerprint, string expected) =>
            problem == CertificateProblem.PinMismatch
                ? $"The certificate of {hostPort} does not match the expected fingerprint: it is {CertificatePin.Colons(fingerprint)}, expected {CertificatePin.Colons(expected)}."
                : $"The certificate of {hostPort} is not trusted. Its SHA-256 fingerprint is {CertificatePin.Colons(fingerprint)}.";
    }
}
