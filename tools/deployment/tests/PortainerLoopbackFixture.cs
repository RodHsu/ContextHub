using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;

namespace ContextHub.Deployment.Tests {
    // Synthetic loopback only. Never outputs or persists authentication headers or TLS keys.
    public sealed class PortainerLoopbackFixture : IDisposable {
        private readonly TcpListener listener;
        private readonly X509Certificate2 certificate;
        private readonly RSA signingKey;
        private readonly Task worker;
        private TcpClient connection;
        public int Port { get; }
        public string CertificatePin { get; }
        public bool ApiKeyPresent { get; private set; }
        public bool AuthorizationAbsent { get; private set; }
        public string Phase { get; private set; } = "Listening";
        public string FailureType { get; private set; } = "None";
        public int TlsFailureCode { get; private set; }
        public PortainerLoopbackFixture(string mode) {
            signingKey = RSA.Create(2048);
            var request = new CertificateRequest("CN=localhost", signingKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(5));
            CertificatePin = Convert.ToHexString(SHA256.HashData(certificate.RawData));
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start(); Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            worker = Task.Run(async () => {
                try {
                    connection = await listener.AcceptTcpClientAsync();
                    Phase = "Accepted";
                    using var stream = new SslStream(connection.GetStream(), false);
                    await stream.AuthenticateAsServerAsync(certificate);
                    Phase = "TlsReady";
                    using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
                    string key = null, line;
                    AuthorizationAbsent = true;
                    while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync())) {
                        if (line.StartsWith("X-API-Key:", StringComparison.OrdinalIgnoreCase)) key = line.Substring(10).Trim();
                        if (line.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase)) AuthorizationAbsent = false;
                    }
                    ApiKeyPresent = !string.IsNullOrEmpty(key);
                    Phase = "HeadersReceived";
                    string body = mode == "reflect" ? key : "{\"Id\":\"fixture-id\"}";
                    if (mode == "escaped-reflect") {
                        var encoded = new StringBuilder();
                        foreach (char value in key) encoded.Append("\\u").Append(((int)value).ToString("x4"));
                        body = "{\"nested\":[{\"value\":\"" + encoded + "\"}]}";
                    }
                    string status = mode == "redirect" ? "302 Found" : mode == "failure" ? "403 Forbidden" : "200 OK";
                    string redirect = mode == "redirect" ? "Location: https://example.invalid/forbidden\r\n" : "";
                    var bytes = Encoding.UTF8.GetBytes(body);
                    var headers = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + "\r\n" + redirect + "Content-Type: application/json\r\nContent-Length: " + bytes.Length + "\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(headers); await stream.WriteAsync(bytes); await stream.FlushAsync();
                    Phase = "ResponseSent";
                    key = null; line = null; body = null; Array.Clear(bytes, 0, bytes.Length);
                } catch (Exception error) {
                    FailureType = error.GetType().Name;
                    if (Phase == "Accepted" && error.InnerException is System.ComponentModel.Win32Exception native)
                        TlsFailureCode = native.NativeErrorCode;
                }
            });
        }
        public void Dispose() {
            connection?.Dispose(); listener.Stop();
            try { worker.Wait(TimeSpan.FromSeconds(2)); } catch { }
            certificate.Dispose();
            signingKey.Dispose();
        }
    }
}
